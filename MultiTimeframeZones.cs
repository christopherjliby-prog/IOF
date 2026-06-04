// =============================================================================
// MultiTimeframeZones.cs — Multi-Timeframe IOF zone detection + MTFC overlap
// =============================================================================
// 2026-05-06: Added per-timeframe trend tracking via TrendStateMachine.
// New methods: ScanTrendsAllTiers, ScanTrendForTimeframe.
// MTFCOverlap now exposes TrendAgreement flag (per TP MTFC + PDF trend doctrine).
// =============================================================================
// Self-contained component for the v2 master indicator. Restores the
// LTF / ITF / HTF zone detection that the Pine v1.4 indicator had but which
// was missing from the v2 port. Also implements the "MTFC" (Multi-Timeframe
// Correlation) overlap detector referenced by the project audit (Sec. 6 / 7).
//
// Public API surface lives in namespace TradePhantomsIOF.MultiTF:
//
//   * enum  ZoneTimeframe                 (LTF / ITF / HTF)
//   * class TimeframeZone                  (one detected zone, scored externally)
//   * class MTFCOverlap                    (one higher/lower zone correlation)
//   * static class MultiTFZoneScanner      (entry points)
//
// Mirrors the per-tier IBI cluster detection from Pine v1.4 (lines 510-600 of
// previous_pinescript/message (4).txt) and the v2 master ScanZones method
// (TradePhantoms_IOF_v2.cs, ~lines 800-950). Async data fetching uses the
// Quantower pattern from AccessCustomVolumeAnalysisData.cs.
//
// HARD EXCLUSION: NO BOS / NO CHoCH logic anywhere in this file. The MTFC
// overlap is a simple geometric box intersection between same-direction zones
// across two tiers — nothing more.
//
// CRITICAL FIX (audit Critical #1): for SUPPLY zones, WickHi MUST be the
// highest wick of the base (max bar.High), NOT the highest body. The
// BuildZoneRect helper in this file does that correctly per the asymmetric
// rule documented in step 3 of the spec.
// =============================================================================

using System;
using System.Collections.Generic;
using TradingPlatform.BusinessLayer;
using TradePhantomsIOF.Trend;

namespace TradePhantomsIOF.MultiTF
{
    // =========================================================================
    // PUBLIC TYPES
    // =========================================================================

    /// <summary>
    /// Identifies which tier a zone was detected on. LTF = the chart timeframe
    /// (or a smaller one), ITF = mid, HTF = the largest of the three.
    /// </summary>
    public enum ZoneTimeframe
    {
        LTF,
        ITF,
        HTF
    }

    /// <summary>
    /// One detected zone (demand or supply) on a specific timeframe.
    /// Mirrors the v2 master's IofZone fields but is decoupled from it so this
    /// component compiles without referencing internals of the master class.
    /// </summary>
    public class TimeframeZone
    {
        public string Id;                          // stable identifier, see scanner
        public ZoneTimeframe Tier;
        public bool IsLong;                        // true = demand (RBR/DBR); false = supply (RBD/DBD)
        public DateTime BaseStartTime;
        public DateTime BaseEndTime;
        public int BaseStartIndex;
        public int BaseEndIndex;
        public double BodyHi;                      // highest body of the base
        public double BodyLo;                      // lowest body of the base
        public double WickHi;                      // for supply: highest base wick (CRITICAL)
        public double WickLo;                      // for demand: lowest base wick
        public bool Active = true;                 // false once invalidated
        public DateTime InvalidatedAt = DateTime.MinValue;
        public double Score;                       // 0-21, populated by master after detection
        public string FormationCode;               // "RBR" / "DBR" / "RBD" / "DBD"
        public List<int> BaseCandleIndices = new List<int>();
    }

    /// <summary>
    /// One MTFC correlation: a higher-tier zone whose price box overlaps with
    /// a lower-tier zone of the same direction. Both must be active.
    /// </summary>
    public class MTFCOverlap
    {
        public TimeframeZone Higher;               // HTF or ITF zone
        public TimeframeZone Lower;                // ITF or LTF zone
        public double OverlapTop;                  // intersection top (price)
        public double OverlapBottom;               // intersection bottom (price)
        public bool BothLong;                      // mirrors Higher.IsLong (both must match)
        // 2026-05-06: TrendAgreement is true iff the per-tier trend snapshots
        // for both the Higher and Lower zones' tiers agree with the zone
        // direction (long zone → up trend on both TFs; short zone → down).
        // When trend snapshots are not supplied to FindOverlaps this stays
        // false (the neutral / unknown default — callers should treat false
        // as "no signal" rather than "actively against").
        public bool TrendAgreement;
    }

    // =========================================================================
    // ZONE SCANNER
    // =========================================================================

    public static class MultiTFZoneScanner
    {
        // --------------------- Top-level scan -------------------------------

        /// <summary>
        /// Scan a single timeframe's HistoricalData for IOF zones. Returns a
        /// (possibly empty) list. Never throws on null/short data — empty list.
        /// Detection algorithm mirrors the v1 ScanZones logic (see file header).
        /// </summary>
        public static List<TimeframeZone> ScanTimeframe(
            HistoricalData data,
            ZoneTimeframe tier,
            int lookbackBars,
            double baseCandleMaxBodyPct,
            double minImpulseRatio,
            int maxBaseCandles,
            double tickSize = 0.25,
            double clusterMaxRangeTicks = 120)
        {
            var result = new List<TimeframeZone>();

            // Robust null/empty handling — HistoricalData may be null while a
            // GetHistory request is still loading on a background thread.
            if (data == null) return result;
            int total = data.Count;
            if (total < 4) return result;

            // Sanity-clamp inputs.
            if (lookbackBars < 4) lookbackBars = 4;
            if (maxBaseCandles < 1) maxBaseCandles = 1;
            if (maxBaseCandles > 15) maxBaseCandles = 15;
            if (tickSize <= 0) tickSize = 0.25;
            if (clusterMaxRangeTicks <= 0) clusterMaxRangeTicks = 120;
            if (baseCandleMaxBodyPct <= 0) baseCandleMaxBodyPct = 0.5;
            if (minImpulseRatio <= 0) minImpulseRatio = 2.0;

            int firstBar = Math.Max(2, total - lookbackBars);

            // Walk from firstBar forward. Each (startIndex..endIndex) is a
            // candidate base segment; the bar BEFORE is the "leg in" and the
            // bar AFTER is the "leg out" (the impulse).
            for (int endIndex = firstBar; endIndex < total - 2; endIndex++)
            {
                for (int baseLen = 1; baseLen <= maxBaseCandles; baseLen++)
                {
                    int startIndex = endIndex - baseLen + 1;
                    if (startIndex < 1) continue;
                    if (endIndex + 1 >= total) continue;

                    // Step 2a: cluster high-to-low must fit within clusterMaxRangeTicks.
                    if (!IsValidBase(data, startIndex, endIndex, tickSize, clusterMaxRangeTicks))
                        continue;

                    // Step 2b: bar BEFORE the base is the leg-in directional
                    // candle. body % must clear threshold + 0.05 (matches v2 master).
                    LegDir legIn = ClassifyLeg(data, startIndex - 1, baseCandleMaxBodyPct);
                    if (legIn == LegDir.None) continue;

                    // Step 2c: bar AFTER the base is the impulse-out candle.
                    LegDir legOut = ClassifyLeg(data, endIndex + 1, baseCandleMaxBodyPct);
                    if (legOut == LegDir.None) continue;

                    // Step 2d: combine in/out direction → formation code.
                    string formation = ToFormation(legIn, legOut);
                    if (formation == null) continue;
                    bool isDemand = formation == "RBR" || formation == "DBR";

                    // Step 3: build the asymmetric zone rectangle.
                    var rect = BuildZoneRect(data, startIndex, endIndex, isDemand);
                    if (rect == null) continue;

                    double baseHeight = isDemand
                        ? rect.BodyHi - rect.WickLo
                        : rect.WickHi - rect.BodyLo;
                    if (baseHeight <= 0) continue;

                    // Step 2e: impulse-out extension must be at least
                    //          minImpulseRatio * baseHeight measured from the
                    //          end of the base outward.
                    double moveOut = MeasureMoveOut(data, endIndex, total, isDemand, lookbackBars);
                    if (moveOut < minImpulseRatio * baseHeight) continue;

                    // Step 4: stable Id and zone object.
                    var zone = new TimeframeZone
                    {
                        Id              = $"{tier}-{formation}-{rect.StartTime:yyyyMMddHHmmss}-{baseLen}",
                        Tier            = tier,
                        IsLong          = isDemand,
                        BaseStartTime   = rect.StartTime,
                        BaseEndTime     = rect.EndTime,
                        BaseStartIndex  = startIndex,
                        BaseEndIndex    = endIndex,
                        BodyHi          = rect.BodyHi,
                        BodyLo          = rect.BodyLo,
                        WickHi          = rect.WickHi,
                        WickLo          = rect.WickLo,
                        Active          = true,
                        FormationCode   = formation
                    };
                    for (int i = startIndex; i <= endIndex; i++)
                        zone.BaseCandleIndices.Add(i);

                    // Skip near-duplicates (same direction, very similar geom).
                    if (IsDuplicate(result, zone)) continue;

                    result.Add(zone);
                }
            }

            // Step 5: invalidate zones whose far wick has been violated by a
            // later confirmed-bar CLOSE.
            ApplyInvalidations(data, result);

            return result;
        }

        // --------------------- Candle/leg helpers ---------------------------

        private enum LegDir { None, Up, Down }

        /// <summary>True if every bar in [start..end] is a valid base candle
        /// (body / range &lt;= baseCandleMaxBodyPct).</summary>
        private static bool IsValidBase(HistoricalData data, int start, int end, double tickSize, double clusterMaxRangeTicks)
        {
            double clusterHigh = double.MinValue;
            double clusterLow  = double.MaxValue;
            for (int i = start; i <= end; i++)
            {
                var bar = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) return false;
                if (bar.High > clusterHigh) clusterHigh = bar.High;
                if (bar.Low  < clusterLow)  clusterLow  = bar.Low;
            }
            if (clusterHigh == double.MinValue || clusterLow == double.MaxValue) return false;
            return (clusterHigh - clusterLow) <= clusterMaxRangeTicks * tickSize;
        }

        /// <summary>Classify a candle as Up / Down / None per the v2 master rule:
        /// body% must exceed (baseCandleMaxBodyPct + 0.05) to count as a leg.</summary>
        private static LegDir ClassifyLeg(HistoricalData data, int idx, double baseCandleMaxBodyPct)
        {
            var bar = data[idx, SeekOriginHistory.Begin] as HistoryItemBar;
            if (bar == null) return LegDir.None;
            double range = bar.High - bar.Low;
            if (range <= 0) return LegDir.None;
            double body = Math.Abs(bar.Close - bar.Open);
            if (body / range < baseCandleMaxBodyPct + 0.05) return LegDir.None;
            if (bar.Close > bar.Open) return LegDir.Up;
            if (bar.Close < bar.Open) return LegDir.Down;
            return LegDir.None;
        }

        private static string ToFormation(LegDir legIn, LegDir legOut)
        {
            if (legIn == LegDir.Up   && legOut == LegDir.Up)   return "RBR";  // Rally-Base-Rally   (demand)
            if (legIn == LegDir.Down && legOut == LegDir.Up)   return "DBR";  // Drop-Base-Rally    (demand)
            if (legIn == LegDir.Up   && legOut == LegDir.Down) return "RBD";  // Rally-Base-Drop    (supply)
            if (legIn == LegDir.Down && legOut == LegDir.Down) return "DBD";  // Drop-Base-Drop     (supply)
            return null;
        }

        // --------------------- Zone rectangle build -------------------------

        /// <summary>Internal scratch object for BuildZoneRect.</summary>
        private class ZoneRect
        {
            public double BodyHi, BodyLo, WickHi, WickLo;
            public DateTime StartTime, EndTime;
        }

        /// <summary>
        /// Build the asymmetric zone box per the spec (step 3):
        ///   * DEMAND  (RBR/DBR): BodyHi=max(body high), BodyLo=min(body low),
        ///                        WickHi = BodyHi (top wick irrelevant),
        ///                        WickLo = lowest base wick.
        ///   * SUPPLY  (RBD/DBD): BodyHi = highest body, BodyLo = lowest body,
        ///                        WickHi = highest base wick (CRITICAL),
        ///                        WickLo = BodyLo (bottom wick irrelevant).
        /// Note the supply WickHi is the audit's Critical #1 fix.
        /// </summary>
        private static ZoneRect BuildZoneRect(HistoricalData data, int start, int end, bool isDemand)
        {
            double highestBody = double.MinValue;
            double lowestBody  = double.MaxValue;
            double highestWick = double.MinValue;
            double lowestWick  = double.MaxValue;
            DateTime tStart = DateTime.MinValue, tEnd = DateTime.MinValue;

            for (int i = start; i <= end; i++)
            {
                var bar = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) return null;

                double bh = Math.Max(bar.Open, bar.Close);
                double bl = Math.Min(bar.Open, bar.Close);
                if (bh > highestBody)  highestBody = bh;
                if (bl < lowestBody)   lowestBody  = bl;
                if (bar.High > highestWick) highestWick = bar.High;
                if (bar.Low  < lowestWick)  lowestWick  = bar.Low;

                if (i == start) tStart = bar.TimeLeft;
                if (i == end)   tEnd   = bar.TimeLeft;
            }

            var rect = new ZoneRect { StartTime = tStart, EndTime = tEnd };
            if (isDemand)
            {
                // Demand: top is the highest body (entry edge).
                //         bottom wick is the deepest low (SL anchor).
                rect.BodyHi = highestBody;
                rect.BodyLo = lowestBody;
                rect.WickHi = highestBody;   // top-wick not used for demand
                rect.WickLo = lowestWick;
            }
            else
            {
                // Supply: top wick is the highest high (SL anchor) — THIS is
                // the audit Critical #1 field. Bottom is the lowest body.
                rect.BodyHi = highestBody;
                rect.BodyLo = lowestBody;
                rect.WickHi = highestWick;   // <-- CRITICAL: highest wick, not body
                rect.WickLo = lowestBody;    // bottom-wick not used for supply
            }
            return rect;
        }

        /// <summary>
        /// Measure how far price extended after the base in the impulse
        /// direction. Mirrors the v2 master's MeasureMoveOut. Stops scanning
        /// once price closes back through the base (rough impulse end).
        /// </summary>
        private static double MeasureMoveOut(HistoricalData data, int endOfBase, int total, bool isDemand, int lookbackBars)
        {
            int scanLimit = Math.Min(total - 1, endOfBase + Math.Max(20, lookbackBars / 4));
            var baseBar = data[endOfBase, SeekOriginHistory.Begin] as HistoryItemBar;
            if (baseBar == null) return 0.0;
            double baseRange = baseBar.High - baseBar.Low;
            if (baseRange <= 0) baseRange = Math.Abs(baseBar.Close - baseBar.Open);

            double extreme = double.NaN;
            for (int i = endOfBase + 1; i <= scanLimit; i++)
            {
                var b = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (b == null) break;
                if (isDemand)
                {
                    if (double.IsNaN(extreme) || b.High > extreme) extreme = b.High;
                    if (b.Close < baseBar.Close - baseRange) break;
                }
                else
                {
                    if (double.IsNaN(extreme) || b.Low < extreme) extreme = b.Low;
                    if (b.Close > baseBar.Close + baseRange) break;
                }
            }
            if (double.IsNaN(extreme)) return 0.0;
            return isDemand ? (extreme - baseBar.High) : (baseBar.Low - extreme);
        }

        // --------------------- Dedup + invalidation -------------------------

        /// <summary>
        /// Skip near-identical overlapping zones (same direction, &lt;25% box
        /// difference) — keeps the list compact when multiple base lengths
        /// satisfy detection on the same cluster.
        /// </summary>
        private static bool IsDuplicate(List<TimeframeZone> existing, TimeframeZone candidate)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                var z = existing[i];
                if (z.IsLong != candidate.IsLong) continue;

                double aTop = candidate.IsLong ? candidate.BodyHi : candidate.WickHi;
                double aBot = candidate.IsLong ? candidate.WickLo : candidate.BodyLo;
                double bTop = z.IsLong         ? z.BodyHi         : z.WickHi;
                double bBot = z.IsLong         ? z.WickLo         : z.BodyLo;

                double aH = aTop - aBot;
                double bH = bTop - bBot;
                if (aH <= 0 || bH <= 0) continue;

                double overlap = Math.Min(aTop, bTop) - Math.Max(aBot, bBot);
                if (overlap <= 0) continue;
                double union = Math.Max(aTop, bTop) - Math.Min(aBot, bBot);
                if (union <= 0) continue;

                // Jaccard-style: if 75%+ overlap, treat as same zone.
                if (overlap / union >= 0.75) return true;
            }
            return false;
        }

        /// <summary>
        /// Mark zones inactive once a later bar's CLOSE is past the far wick.
        /// Demand: invalidated when any bar after the base closes &lt; WickLo.
        /// Supply: invalidated when any bar after the base closes &gt; WickHi.
        /// </summary>
        private static void ApplyInvalidations(HistoricalData data, List<TimeframeZone> zones)
        {
            int total = data.Count;
            for (int z = 0; z < zones.Count; z++)
            {
                var zone = zones[z];
                int from = zone.BaseEndIndex + 1;
                if (from < 0) from = 0;
                for (int i = from; i < total; i++)
                {
                    var bar = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (bar == null) continue;
                    bool broken = zone.IsLong
                        ? bar.Close < zone.WickLo
                        : bar.Close > zone.WickHi;
                    if (broken)
                    {
                        zone.Active = false;
                        zone.InvalidatedAt = bar.TimeLeft;
                        break;
                    }
                }
            }
        }

        // =====================================================================
        // MTFC OVERLAP DETECTION
        // =====================================================================

        /// <summary>
        /// For each (higher, lower) zone pair where direction matches, both are
        /// active, and their boxes overlap on price, return one MTFCOverlap.
        /// The overlap range is the simple intersection of the two boxes.
        /// </summary>
        public static List<MTFCOverlap> FindOverlaps(
            List<TimeframeZone> higherTfZones,
            List<TimeframeZone> lowerTfZones)
        {
            var result = new List<MTFCOverlap>();
            if (higherTfZones == null || lowerTfZones == null) return result;
            if (higherTfZones.Count == 0 || lowerTfZones.Count == 0) return result;

            for (int h = 0; h < higherTfZones.Count; h++)
            {
                var hi = higherTfZones[h];
                if (hi == null || !hi.Active) continue;

                // 2026-05-10: MTFC overlap uses the FULL VISIBLE zone extent
                // (the asymmetric box drawn on chart), not just body extents.
                // Per user clarification: "MTFC is zone-in-zone." Previous
                // body-in-body math was internally consistent but produced
                // overlaps that didn't match the visible zone intersection.
                //   Demand (IsLong=true):  top = BodyHi, bottom = WickLo
                //   Supply (IsLong=false): top = WickHi, bottom = BodyLo
                double hiTop, hiBot;
                if (hi.IsLong) { hiTop = hi.BodyHi; hiBot = hi.WickLo; }
                else           { hiTop = hi.WickHi; hiBot = hi.BodyLo; }
                if (hiTop <= hiBot) continue;

                for (int l = 0; l < lowerTfZones.Count; l++)
                {
                    var lo = lowerTfZones[l];
                    if (lo == null || !lo.Active) continue;
                    if (hi.IsLong != lo.IsLong) continue;

                    double loTop, loBot;
                    if (lo.IsLong) { loTop = lo.BodyHi; loBot = lo.WickLo; }
                    else           { loTop = lo.WickHi; loBot = lo.BodyLo; }
                    if (loTop <= loBot) continue;

                    // Intersection: overlap iff min(hiTop, loTop) >= max(hiBot, loBot)
                    double overlapTop = Math.Min(hiTop, loTop);
                    double overlapBot = Math.Max(hiBot, loBot);
                    if (overlapTop < overlapBot) continue;

                    // Edge case: zero-thickness intersection (touching) — keep
                    // it; user can filter on (overlapTop > overlapBot) if needed.
                    result.Add(new MTFCOverlap
                    {
                        Higher         = hi,
                        Lower          = lo,
                        OverlapTop     = overlapTop,
                        OverlapBottom  = overlapBot,
                        BothLong       = hi.IsLong,
                        TrendAgreement = false      // neutral default
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// MTFC overlap detection with optional per-tier trend snapshots.
        /// Behaves identically to the 2-arg overload, but additionally fills
        /// MTFCOverlap.TrendAgreement using the supplied snapshots.
        ///
        /// Trend agreement rule (kept intentionally simple — geometric, not
        /// predictive):
        ///   * For a LONG zone (demand): both Higher.Tier and Lower.Tier must
        ///     have a trend whose direction is "Up".
        ///   * For a SHORT zone (supply): both must be "Down".
        ///   * Any missing snapshot, "None", or "Neutral" reading → false.
        ///
        /// If <paramref name="trendsByTier"/> is null OR empty the result is
        /// equivalent to the 2-arg overload (TrendAgreement = false on every
        /// returned overlap), which is the documented neutral default.
        /// </summary>
        public static List<MTFCOverlap> FindOverlaps(
            List<TimeframeZone> higherTfZones,
            List<TimeframeZone> lowerTfZones,
            Dictionary<ZoneTimeframe, TrendSnapshot> trendsByTier)
        {
            var result = FindOverlaps(higherTfZones, lowerTfZones);
            if (trendsByTier == null || trendsByTier.Count == 0) return result;

            for (int i = 0; i < result.Count; i++)
            {
                var ov = result[i];
                if (ov == null || ov.Higher == null || ov.Lower == null) continue;

                TrendSnapshot snapHi, snapLo;
                if (!trendsByTier.TryGetValue(ov.Higher.Tier, out snapHi)) continue;
                if (!trendsByTier.TryGetValue(ov.Lower.Tier,  out snapLo)) continue;
                if (snapHi == null || snapLo == null) continue;

                ov.TrendAgreement = TrendAgreesWithDirection(snapHi, ov.BothLong)
                                 && TrendAgreesWithDirection(snapLo, ov.BothLong);
            }
            return result;
        }

        /// <summary>
        /// Tolerant trend-direction check. Reads <c>Direction</c> off the
        /// snapshot via reflection so the call site does NOT hard-fail at
        /// compile time if TrendStateMachine evolves its enum/property names
        /// between rounds. Recognised tokens (case-insensitive substring):
        ///   * "up", "bull", "long"    → up
        ///   * "down", "bear", "short" → down
        /// Anything else (None, Neutral, missing property) → false.
        /// </summary>
        private static bool TrendAgreesWithDirection(TrendSnapshot snap, bool wantLong)
        {
            if (snap == null) return false;
            string dir = ReadTrendDirectionString(snap);
            if (string.IsNullOrEmpty(dir)) return false;
            string d = dir.ToLowerInvariant();
            bool isUp   = d.Contains("up")   || d.Contains("bull") || d.Contains("long");
            bool isDown = d.Contains("down") || d.Contains("bear") || d.Contains("short");
            if (!isUp && !isDown) return false;
            return wantLong ? isUp : isDown;
        }

        /// <summary>
        /// Reflection-based snapshot direction reader. Looks for the most
        /// likely property names produced by TrendStateMachine.GetSnapshot.
        /// Returns the value's ToString() or null if nothing useful is found.
        /// Kept private because it is purely a defensive shim — once the
        /// TrendStateMachine API stabilises this can be replaced with a
        /// direct property access.
        /// </summary>
        private static string ReadTrendDirectionString(TrendSnapshot snap)
        {
            var t = snap.GetType();
            string[] candidates = { "Direction", "Trend", "State", "Bias", "TrendDirection" };
            for (int i = 0; i < candidates.Length; i++)
            {
                var p = t.GetProperty(candidates[i]);
                if (p != null)
                {
                    var v = p.GetValue(snap, null);
                    if (v != null) return v.ToString();
                }
                var f = t.GetField(candidates[i]);
                if (f != null)
                {
                    var v = f.GetValue(snap);
                    if (v != null) return v.ToString();
                }
            }
            return null;
        }

        // =====================================================================
        // PER-TIMEFRAME TREND TRACKING (added 2026-05-06)
        // =====================================================================
        //
        // Each tier (LTF / ITF / HTF) gets its own TrendStateMachine. We replay
        // the historical bars in chronological order, calling OnBarClose for
        // every closed bar, then snapshot the final state. The trend logic
        // itself (engulfing-candle control points + 3-segment HH/HL/LH/LL
        // structure) lives in TrendStateMachine — we only feed it bars.
        //
        // PERFORMANCE NOTE: full-history replays are O(n) per tier. For HTF
        // (1d/1w) total bars are <= a few hundred and this is trivial. For LTF
        // (1m/5m) over multi-week lookbacks it would be the dominant cost on
        // a chart-update tick. We cap to the most recent <maxBars> bars
        // (default 500) which is more than enough for the 3-segment structure
        // logic to settle, and keeps per-tier work near O(1) on long charts.
        // Caller can override via the maxBars parameter when more depth is
        // genuinely needed (e.g. weekly trend on a 1m chart looking back years).

        /// <summary>Default cap on bars replayed into a TrendStateMachine per
        /// scan. 500 closed bars comfortably covers the 3-segment HH/HL/LH/LL
        /// window even after a long consolidation. Increase via the maxBars
        /// parameter if a tier genuinely needs deeper history.</summary>
        public const int DefaultTrendMaxBars = 500;

        /// <summary>
        /// Build a fresh <see cref="TrendStateMachine"/> for one timeframe,
        /// replay up to <paramref name="maxBars"/> of its closed history into
        /// it, and return the resulting snapshot. Returns null if data is
        /// null/empty (callers should treat null as "trend unknown — neutral").
        ///
        /// The <paramref name="useEngulfingControlPoints"/> flag is forwarded
        /// to the state machine if it exposes a matching configuration knob;
        /// today it is set on the instance via reflection for forward-compat
        /// (TrendStateMachine is being authored in parallel and its exact
        /// constructor / property surface may shift slightly).
        /// </summary>
        public static TrendSnapshot ScanTrendForTimeframe(
            HistoricalData data,
            double tickSize,
            bool useEngulfingControlPoints = true,
            int maxBars = DefaultTrendMaxBars)
        {
            if (data == null) return null;
            int total = data.Count;
            if (total < 2) return null;

            var machine = CreateTrendStateMachine(useEngulfingControlPoints);
            if (machine == null) return null;

            // Cap to most-recent maxBars of CLOSED bars. The currently-forming
            // bar is at index 0; SeekOriginHistory.End would return it as bar
            // 0 — instead we walk Begin-indexed and stop one short of total.
            int firstBar = Math.Max(1, total - Math.Max(2, maxBars));
            int lastBar  = total - 1;     // skip currently-forming bar

            for (int i = firstBar; i < lastBar; i++)
            {
                var bar = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) continue;
                try
                {
                    machine.OnBarClose(
                        i,
                        bar.TimeLeft,
                        bar.Open,
                        bar.High,
                        bar.Low,
                        bar.Close,
                        tickSize);
                }
                catch
                {
                    // Defensive: a single malformed bar shouldn't kill the
                    // whole snapshot. Skip and continue.
                }
            }

            try
            {
                return machine.GetSnapshot();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Convenience wrapper: build trend snapshots for all three tiers in
        /// one call. Tiers whose data is null/empty are silently skipped (the
        /// returned dictionary will not contain that key). The returned map
        /// can be passed straight into the 3-arg <see cref="FindOverlaps"/>.
        /// </summary>
        public static Dictionary<ZoneTimeframe, TrendSnapshot> ScanTrendsAllTiers(
            HistoricalData ltfData,
            HistoricalData itfData,
            HistoricalData htfData,
            double tickSize,
            bool useEngulfingControlPoints = true,
            int maxBars = DefaultTrendMaxBars)
        {
            var map = new Dictionary<ZoneTimeframe, TrendSnapshot>();

            var ltf = ScanTrendForTimeframe(ltfData, tickSize, useEngulfingControlPoints, maxBars);
            if (ltf != null) map[ZoneTimeframe.LTF] = ltf;

            var itf = ScanTrendForTimeframe(itfData, tickSize, useEngulfingControlPoints, maxBars);
            if (itf != null) map[ZoneTimeframe.ITF] = itf;

            var htf = ScanTrendForTimeframe(htfData, tickSize, useEngulfingControlPoints, maxBars);
            if (htf != null) map[ZoneTimeframe.HTF] = htf;

            return map;
        }

        /// <summary>
        /// Construct a TrendStateMachine instance, attempting to forward the
        /// useEngulfingControlPoints flag through whichever ctor / property
        /// the sister file ends up exposing. Falls back to the default ctor
        /// if a matching one isn't present. Returns null only if the type
        /// has no usable parameterless constructor either.
        /// </summary>
        private static TrendStateMachine CreateTrendStateMachine(bool useEngulfingControlPoints)
        {
            try
            {
                // Preferred: ctor (bool useEngulfingControlPoints).
                var t = typeof(TrendStateMachine);
                var ctor = t.GetConstructor(new[] { typeof(bool) });
                if (ctor != null)
                    return (TrendStateMachine)ctor.Invoke(new object[] { useEngulfingControlPoints });

                // Fallback: default ctor + property/field assignment.
                var inst = (TrendStateMachine)Activator.CreateInstance(typeof(TrendStateMachine));
                var prop = t.GetProperty("UseEngulfingControlPoints");
                if (prop != null && prop.CanWrite)
                    prop.SetValue(inst, useEngulfingControlPoints, null);
                else
                {
                    var fld = t.GetField("UseEngulfingControlPoints");
                    if (fld != null) fld.SetValue(inst, useEngulfingControlPoints);
                }
                return inst;
            }
            catch
            {
                return null;
            }
        }

        // =====================================================================
        // ASYNC DATA FETCH
        // =====================================================================

        /// <summary>
        /// Fetch a separate timeframe of HistoricalData for the given symbol,
        /// ending at endTime, with an estimated barsBack worth of history.
        /// Wraps Symbol.GetHistory per AccessCustomVolumeAnalysisData.cs.
        ///
        /// IMPORTANT: Quantower's GetHistory loads in the background. The
        /// returned HistoricalData may have Count == 0 immediately after this
        /// call. Callers must tolerate that — ScanTimeframe already does. The
        /// master indicator should re-scan on its periodic refresh tick rather
        /// than block here.
        /// </summary>
        public static HistoricalData FetchTimeframeData(
            Symbol symbol,
            Period period,
            int barsBack,
            DateTime endTime)
        {
            if (symbol == null) return null;
            if (barsBack <= 0) barsBack = 500;

            // Compute a generous start time. Period exposes Ticks/Duration via
            // its operators; for portability we just translate via a switch
            // over common periods (matches RecommendHigherTimeframes) and fall
            // back to a per-bar duration estimate. We aim for >= barsBack bars.
            TimeSpan barLen = EstimateBarDuration(period);
            // Pad by 50% to absorb weekends / session gaps.
            DateTime fromTime = endTime.AddTicks(-(long)(barLen.Ticks * barsBack * 1.5));

            try
            {
                // Per AccessCustomVolumeAnalysisData.cs: pass the symbol's
                // HistoryType so the resulting bars match what the chart uses.
                return symbol.GetHistory(period, symbol.HistoryType, fromTime);
            }
            catch
            {
                // GetHistory may throw if the symbol has no data for this TF.
                return null;
            }
        }

        /// <summary>Convenience overload: fetch up to "now".</summary>
        public static HistoricalData FetchTimeframeData(Symbol symbol, Period period, int barsBack)
        {
            return FetchTimeframeData(symbol, period, barsBack, DateTime.UtcNow);
        }

        /// <summary>
        /// Approximate bar duration for common Periods. Used only to estimate
        /// the start time for GetHistory. Falls back to 1 hour for unknowns.
        /// </summary>
        private static TimeSpan EstimateBarDuration(Period period)
        {
            // Period equality in Quantower is value-based.
            if (period == Period.MIN1)   return TimeSpan.FromMinutes(1);
            if (period == Period.MIN5)   return TimeSpan.FromMinutes(5);
            if (period == Period.MIN15)  return TimeSpan.FromMinutes(15);
            if (period == Period.MIN30)  return TimeSpan.FromMinutes(30);
            if (period == Period.HOUR1)  return TimeSpan.FromHours(1);
            if (period == Period.HOUR4)  return TimeSpan.FromHours(4);
            if (period == Period.DAY1)   return TimeSpan.FromDays(1);
            if (period == Period.WEEK1)  return TimeSpan.FromDays(7);
            if (period == Period.MONTH1) return TimeSpan.FromDays(31);
            return TimeSpan.FromHours(1);
        }

        // =====================================================================
        // CONFIGURATION HELPER — chart TF → recommended ITF / HTF
        // =====================================================================

        /// <summary>
        /// Map the current chart timeframe to the recommended ITF / HTF pair
        /// from the IOF playbook. Falls back to (1d, 1w) for anything exotic.
        /// </summary>
        public static (Period itf, Period htf) RecommendHigherTimeframes(Period chartTf)
        {
            if (chartTf == Period.MIN1)   return (Period.MIN15, Period.HOUR1);
            if (chartTf == Period.MIN5)   return (Period.HOUR1, Period.HOUR4);
            if (chartTf == Period.MIN15)  return (Period.HOUR4, Period.DAY1);
            if (chartTf == Period.HOUR1)  return (Period.DAY1,  Period.WEEK1);
            if (chartTf == Period.HOUR4)  return (Period.DAY1,  Period.WEEK1);
            if (chartTf == Period.DAY1)   return (Period.WEEK1, Period.MONTH1);
            // Fallback for MIN30, exotic, or unknown periods.
            return (Period.DAY1, Period.WEEK1);
        }

        // =====================================================================
        // CONVENIENCE — full multi-tier scan in one call
        // =====================================================================

        /// <summary>
        /// One-shot helper: given the three HistoricalData feeds (one per
        /// tier), return a flat list of all detected zones. Callers can
        /// partition by Tier or pass two lists straight into FindOverlaps.
        /// Any null feed is silently skipped.
        /// </summary>
        public static List<TimeframeZone> ScanAllTiers(
            HistoricalData ltfData,
            HistoricalData itfData,
            HistoricalData htfData,
            int lookbackBars,
            double baseCandleMaxBodyPct,
            double minImpulseRatio,
            int maxBaseCandles,
            double tickSize = 0.25,
            double clusterMaxRangeTicks = 120)
        {
            var all = new List<TimeframeZone>();
            if (ltfData != null)
                all.AddRange(ScanTimeframe(ltfData, ZoneTimeframe.LTF, lookbackBars,
                    baseCandleMaxBodyPct, minImpulseRatio, maxBaseCandles, tickSize, clusterMaxRangeTicks));
            if (itfData != null)
                all.AddRange(ScanTimeframe(itfData, ZoneTimeframe.ITF, lookbackBars,
                    baseCandleMaxBodyPct, minImpulseRatio, maxBaseCandles, tickSize, clusterMaxRangeTicks));
            if (htfData != null)
                all.AddRange(ScanTimeframe(htfData, ZoneTimeframe.HTF, lookbackBars,
                    baseCandleMaxBodyPct, minImpulseRatio, maxBaseCandles, tickSize, clusterMaxRangeTicks));
            return all;
        }

        /// <summary>
        /// Filter a flat zone list down to a single tier — convenience for
        /// callers who used ScanAllTiers and want LTF/ITF/HTF buckets back.
        /// </summary>
        public static List<TimeframeZone> FilterByTier(List<TimeframeZone> all, ZoneTimeframe tier)
        {
            var result = new List<TimeframeZone>();
            if (all == null) return result;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].Tier == tier)
                    result.Add(all[i]);
            return result;
        }
    }
}
