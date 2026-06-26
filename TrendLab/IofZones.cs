// IofZones.cs — IOF impulse-base-impulse zone detection.
// Ported from TradePhantoms_IOF_v2 MultiTFZoneScanner (the user's proven logic):
//   base   = 1..N small-body candles (body/range <= baseMaxBodyPct)
//   leg-in = bar BEFORE base is a directional impulse (body% > base+0.05)
//   leg-out= bar AFTER base is the impulse out; its move >= minImpulseRatio*baseHeight
//   RBR/DBR = demand (long), RBD/DBD = supply (short)
//   asymmetric box: supply WickHi = highest base wick (Critical#1), demand WickLo = lowest
//   invalidation = a later bar CLOSES through the far wick.
//
// Bars are passed in CHRONOLOGICAL order (index 0 = oldest), closed bars only, so the
// scanner is pure and avoids Quantower's newest-first indexing.

using System;
using System.Collections.Generic;
using TradingPlatform.BusinessLayer;

namespace TradePhantomsIOF.Trend
{
    public class IofZone
    {
        public string   Formation;   // "RBR" | "DBR" | "RBD" | "DBD"
        public bool     IsLong;      // demand = true (long), supply = false (short)
        public double   BodyHi, BodyLo, WickHi, WickLo;
        public DateTime BaseStartTime, BaseEndTime;
        public bool     Active = true;
        public DateTime InvalidatedAt;

        // Proximal edge price enters at on the retrace; far wick = stop anchor.
        public double EntryEdge => IsLong ? BodyHi : BodyLo;
        public double FarWick   => IsLong ? WickLo : WickHi;
    }

    public class ZoneTradeResult
    {
        public int    Wins;
        public int    Losses;
        public double TotalR;
        public int    Total => Wins + Losses;
    }

    public static class IofZoneScanner
    {
        private enum LegDir { None, Up, Down }

        // Backtest the user's method: on the FIRST retrace into a zone (after it forms),
        // enter at the proximal edge, stop beyond the far wick, target the next OPPOSING
        // zone (else 2R). Resolves forward, stop-first. RAW edge (no trend filter yet).
        public static ZoneTradeResult SimulateZoneTrades(
            IReadOnlyList<HistoryItemBar> bars, List<IofZone> zones, double tickSize)
        {
            var res = new ZoneTradeResult();
            if (bars == null || zones == null || zones.Count == 0) return res;
            int n = bars.Count;
            double buf = 2 * tickSize;

            var zlist = new List<IofZone>(zones);
            zlist.Sort((a, b) => a.BaseEndTime.CompareTo(b.BaseEndTime));

            foreach (var z in zlist)
            {
                int entryBar = -1;
                for (int i = 0; i < n; i++)
                {
                    var b = bars[i];
                    if (b == null || b.TimeLeft <= z.BaseEndTime) continue;
                    bool touched = z.IsLong ? b.Low <= z.EntryEdge : b.High >= z.EntryEdge;
                    if (touched) { entryBar = i; break; }   // first hit only
                }
                if (entryBar < 0) continue;

                double entry = z.EntryEdge;
                double stop  = z.IsLong ? z.FarWick - buf : z.FarWick + buf;
                double risk  = Math.Abs(stop - entry);
                if (risk <= 0) continue;

                double target = FindOpposingTarget(zlist, z, entry);
                double reward = z.IsLong ? target - entry : entry - target;
                if (double.IsNaN(target) || reward <= risk)
                {
                    target = z.IsLong ? entry + 2 * risk : entry - 2 * risk;
                    reward = 2 * risk;
                }
                double rr = reward / risk;

                for (int i = entryBar; i < n; i++)
                {
                    var b = bars[i];
                    if (b == null) continue;
                    bool loss = z.IsLong ? b.Low  <= stop   : b.High >= stop;
                    bool win  = z.IsLong ? b.High >= target : b.Low  <= target;
                    if (loss) { res.Losses++; res.TotalR -= 1;  break; }
                    if (win)  { res.Wins++;   res.TotalR += rr; break; }
                }
            }
            return res;
        }

        private static double FindOpposingTarget(List<IofZone> zones, IofZone z, double entry)
        {
            double best = double.NaN;
            foreach (var o in zones)
            {
                if (o.IsLong == z.IsLong) continue;
                double edge = o.EntryEdge;
                if (z.IsLong) { if (edge > entry && (double.IsNaN(best) || edge < best)) best = edge; }
                else          { if (edge < entry && (double.IsNaN(best) || edge > best)) best = edge; }
            }
            return best;
        }

        public static List<IofZone> Scan(
            IReadOnlyList<HistoryItemBar> bars,
            double baseMaxBodyPct = 0.5,
            double minImpulseRatio = 2.0,
            int    maxBaseCandles  = 5)
        {
            var result = new List<IofZone>();
            if (bars == null) return result;
            int n = bars.Count;
            if (n < 4) return result;
            if (maxBaseCandles < 1) maxBaseCandles = 1;
            if (maxBaseCandles > 7) maxBaseCandles = 7;
            if (baseMaxBodyPct <= 0) baseMaxBodyPct = 0.5;
            if (minImpulseRatio <= 0) minImpulseRatio = 2.0;

            // endIndex is the last base candle; need a bar before (legIn) and after (legOut).
            for (int endIndex = 1; endIndex < n - 1; endIndex++)
            {
                for (int baseLen = 1; baseLen <= maxBaseCandles; baseLen++)
                {
                    int startIndex = endIndex - baseLen + 1;
                    if (startIndex < 1) break;            // need a bar before the base

                    if (!IsValidBase(bars, startIndex, endIndex, baseMaxBodyPct)) continue;

                    LegDir legIn  = ClassifyLeg(bars[startIndex - 1], baseMaxBodyPct);
                    LegDir legOut = ClassifyLeg(bars[endIndex + 1],   baseMaxBodyPct);
                    string formation = ToFormation(legIn, legOut);
                    if (formation == null) continue;

                    bool isDemand = formation == "RBR" || formation == "DBR";

                    var rect = BuildRect(bars, startIndex, endIndex, isDemand);
                    if (rect == null) continue;

                    double baseHeight = isDemand ? (rect.BodyHi - rect.WickLo)
                                                 : (rect.WickHi - rect.BodyLo);
                    if (baseHeight <= 0) continue;

                    double moveOut = MeasureMoveOut(bars, endIndex, isDemand);
                    if (moveOut < minImpulseRatio * baseHeight) continue;

                    var zone = new IofZone
                    {
                        Formation     = formation,
                        IsLong        = isDemand,
                        BodyHi        = rect.BodyHi,
                        BodyLo        = rect.BodyLo,
                        WickHi        = rect.WickHi,
                        WickLo        = rect.WickLo,
                        BaseStartTime = bars[startIndex].TimeLeft,
                        BaseEndTime   = bars[endIndex].TimeLeft,
                        Active        = true
                    };

                    if (IsDuplicate(result, zone)) continue;
                    result.Add(zone);
                }
            }

            ApplyInvalidations(bars, result);
            return result;
        }

        // ── helpers ────────────────────────────────────────────────────────
        private static bool IsValidBase(IReadOnlyList<HistoryItemBar> bars, int start, int end, double maxBodyPct)
        {
            for (int i = start; i <= end; i++)
            {
                var b = bars[i];
                if (b == null) return false;
                double range = b.High - b.Low;
                if (range <= 0) return false;
                if (Math.Abs(b.Close - b.Open) / range > maxBodyPct) return false;
            }
            return true;
        }

        private static LegDir ClassifyLeg(HistoryItemBar b, double maxBodyPct)
        {
            if (b == null) return LegDir.None;
            double range = b.High - b.Low;
            if (range <= 0) return LegDir.None;
            if (Math.Abs(b.Close - b.Open) / range < maxBodyPct + 0.05) return LegDir.None;
            if (b.Close > b.Open) return LegDir.Up;
            if (b.Close < b.Open) return LegDir.Down;
            return LegDir.None;
        }

        private static string ToFormation(LegDir legIn, LegDir legOut)
        {
            if (legIn == LegDir.Up   && legOut == LegDir.Up)   return "RBR"; // demand
            if (legIn == LegDir.Down && legOut == LegDir.Up)   return "DBR"; // demand
            if (legIn == LegDir.Up   && legOut == LegDir.Down) return "RBD"; // supply
            if (legIn == LegDir.Down && legOut == LegDir.Down) return "DBD"; // supply
            return null;
        }

        private class Rect { public double BodyHi, BodyLo, WickHi, WickLo; }

        private static Rect BuildRect(IReadOnlyList<HistoryItemBar> bars, int start, int end, bool isDemand)
        {
            double hiBody = double.MinValue, loBody = double.MaxValue;
            double hiWick = double.MinValue, loWick = double.MaxValue;
            for (int i = start; i <= end; i++)
            {
                var b = bars[i];
                if (b == null) return null;
                double bh = Math.Max(b.Open, b.Close);
                double bl = Math.Min(b.Open, b.Close);
                if (bh > hiBody) hiBody = bh;
                if (bl < loBody) loBody = bl;
                if (b.High > hiWick) hiWick = b.High;
                if (b.Low  < loWick) loWick = b.Low;
            }
            var r = new Rect { BodyHi = hiBody, BodyLo = loBody };
            if (isDemand) { r.WickHi = hiBody;  r.WickLo = loWick; }   // demand: bottom wick is the SL anchor
            else          { r.WickHi = hiWick;  r.WickLo = loBody; }   // supply: top wick (Critical#1)
            return r;
        }

        private static double MeasureMoveOut(IReadOnlyList<HistoryItemBar> bars, int endOfBase, bool isDemand)
        {
            int n = bars.Count;
            int scanLimit = Math.Min(n - 1, endOfBase + 30);
            var baseBar = bars[endOfBase];
            if (baseBar == null) return 0.0;
            double baseRange = baseBar.High - baseBar.Low;
            if (baseRange <= 0) baseRange = Math.Abs(baseBar.Close - baseBar.Open);

            double extreme = double.NaN;
            for (int i = endOfBase + 1; i <= scanLimit; i++)
            {
                var b = bars[i];
                if (b == null) break;
                if (isDemand)
                {
                    if (double.IsNaN(extreme) || b.High > extreme) extreme = b.High;
                    if (b.Close < baseBar.Close - baseRange) break;   // impulse ended
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

        private static bool IsDuplicate(List<IofZone> existing, IofZone c)
        {
            foreach (var z in existing)
            {
                if (z.IsLong != c.IsLong) continue;
                double aTop = c.IsLong ? c.BodyHi : c.WickHi;
                double aBot = c.IsLong ? c.WickLo : c.BodyLo;
                double bTop = z.IsLong ? z.BodyHi : z.WickHi;
                double bBot = z.IsLong ? z.WickLo : z.BodyLo;
                double overlap = Math.Min(aTop, bTop) - Math.Max(aBot, bBot);
                if (overlap <= 0) continue;
                double union = Math.Max(aTop, bTop) - Math.Min(aBot, bBot);
                if (union > 0 && overlap / union >= 0.75) return true;
            }
            return false;
        }

        private static void ApplyInvalidations(IReadOnlyList<HistoryItemBar> bars, List<IofZone> zones)
        {
            int n = bars.Count;
            foreach (var zone in zones)
            {
                for (int i = 0; i < n; i++)
                {
                    var b = bars[i];
                    if (b == null) continue;
                    if (b.TimeLeft <= zone.BaseEndTime) continue;   // only bars after the base
                    bool broken = zone.IsLong ? b.Close < zone.WickLo : b.Close > zone.WickHi;
                    if (broken) { zone.Active = false; zone.InvalidatedAt = b.TimeLeft; break; }
                }
            }
        }
    }
}
