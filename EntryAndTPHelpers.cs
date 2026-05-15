// =====================================================================================
// EntryAndTPHelpers.cs
// TradePhantoms IOF — Entry / SL / TP / Sizing / R-Multiple math helpers
// Modeled on Pine Script v1.4 reference (previous_pinescript\message (4).txt).
//
// PURPOSE
// -------
// Pure, allocation-light static math used by the Quantower IOF indicator/strategy
// to translate a wick-based zone (bodyHi/bodyLo/wickHi/wickLo) into a complete
// trade plan: discrete OF entry depth (0/25/50/75%), an SL anchored at the far
// wick + buffer (regardless of OF depth), N take-profits stepped by ZONE HEIGHT,
// position sizing by dollar risk, and R-multiple / dollar P&L conversion.
//
// CALIBRATION DELTA vs. previous broken C# (v1) of TradePhantoms_IOF.cs
// --------------------------------------------------------------------
// PREVIOUS (v1, WRONG): TP1/TP2/TP3 were placed at 1× / 3× / 5× of *risk
//   distance* (|entry - origSL|). Risk distance shrinks as OF% grows, so deep-OF
//   entries produced absurdly tight TPs that in some cases sat *inside* the
//   originating zone. It also disconnected the TP ladder from the structural
//   feature that produced the trade (the zone itself).
//
// THIS MODULE (v2, CORRECT): TPs are 1× / 2× / 3× of *zone height* by Pine v1.4
//   default. Zone height is invariant to OF depth, so the TP ladder is anchored
//   to the structure, not to the (shrinking) risk leg. The user-tunable knob is
//   `tpStep` (Pine v1.4 input "TP Step (x zone height)", default 1.0, range
//   0.25..5.0). Calling ComputeTPs with tpStep=1.0 and tpCount=3 reproduces
//   exactly the Pine v1.4 default ladder.
//
// MENTAL MODEL
// ------------
// DEMAND zone (long): wick extends DOWN from the body. The "lip" is `bodyLo`.
//   The deepest point is `wickLo`. Higher OF% pushes the entry DEEPER, i.e.
//   LOWER. SL stays at `wickLo - buffer` regardless.
//
// SUPPLY zone (short): wick extends UP from the body. The "lip" is `bodyHi`.
//   The deepest point is `wickHi`. Higher OF% pushes the entry DEEPER, i.e.
//   HIGHER. SL stays at `wickHi + buffer` regardless.
//
// The whole point of the OF model: as OF% grows, slDist shrinks, R per move
// grows, but fill probability drops (price may reverse before reaching the
// deeper trigger). The SL is locked at the far wick — that's the anchor.
//
// File is self-contained: only `using System;` required.
// =====================================================================================

using System;

namespace TradePhantomsIOF
{
    /// <summary>
    /// Discrete OF (Order Flow) entry-depth selector. Pine v1.4 deliberately
    /// rejects a continuous slider — only these four levels are valid. The int
    /// value (0/25/50/75) doubles as the percent fraction numerator.
    /// </summary>
    public enum OFEntryLevel
    {
        // 2026-05-10: "Front of zone" — entry at the FAR BODY edge from the
        // direction of approach. For a SUPPLY (short) zone approached from
        // below, that's `bodyLo` (price first contacts the body's bottom
        // edge as it rises into the zone). For a DEMAND (long) zone
        // approached from above, that's `bodyHi`. This is the MOST
        // AGGRESSIVE depth — biggest slDist (entry far from SL), smallest R
        // per move, highest fill probability. Use this when you want to
        // catch any wick into the zone, not wait for a deep retrace.
        // Sentinel value -1 keeps the 0/25/50/75 → percent math intact for
        // the other levels.
        // Front == TP 0% (trade phantom doctrine) — both reduce to the top
        // of the zone (BodyHi for demand, BodyLo for supply).
        Front = -1,
        Of0   = 0,   // Entry at body/wick junction (the "lip") — discount entry.
        Of25  = 25,  // 25% deeper into the wick.
        Of50  = 50,  // Wick midpoint.
        Of75  = 75,  // 75% deep — best R, may not trigger if price reverses early.

        // 2026-05-13: trade-phantom-style FULL-ZONE percentage entries.
        // Tp25/50/75 use percentage of the WHOLE zone height (BodyHi → WickLo
        // for demand; BodyLo → WickHi for supply) rather than only the wick
        // portion past the body. This is the canonical trade phantom doctrine
        // — "enter at 25% / 50% / 75% of the way into the zone." Different
        // geometry from the Of* family above:
        //   demand: Tp25 = BodyHi - 0.25 * (BodyHi - WickLo)
        //   supply: Tp25 = BodyLo + 0.25 * (WickHi - BodyLo)
        // The 10xx integer values are non-overlapping with the 0..75 range so
        // existing ((int)level)/100 math doesn't accidentally match them.
        Tp25  = 1025,
        Tp50  = 1050,
        Tp75  = 1075
    }

    /// <summary>
    /// Stateless math helpers for IOF entry/SL/TP/sizing/R conversion.
    /// All methods are pure and side-effect free. Throw nothing on degenerate
    /// inputs — return safe sentinels instead, because indicator hot-paths must
    /// not raise exceptions on every bar.
    /// </summary>
    public static class EntryTPMath
    {
        // Number of TP slots reserved in the returned array. Pine v1.4 had this
        // hardcoded to 5 because of its 1D-array limitation; we keep 5 for UI
        // sanity and to match the Pine output shape. Unused slots are 0.0.
        public const int MaxTpSlots = 5;

        /// <summary>
        /// Compute the OF entry price for a given zone and depth level.
        /// Pine v1.4 reference: f_entry(isDem, bodyHi, bodyLo, wickHi, wickLo, ofPct).
        /// </summary>
        /// <param name="level">Discrete OF depth (0/25/50/75%).</param>
        /// <param name="isLong">true = demand zone (long), false = supply (short).</param>
        /// <param name="bodyHi">High of the originating bar's body.</param>
        /// <param name="bodyLo">Low of the originating bar's body.</param>
        /// <param name="wickHi">High of the originating bar's wick (top tail).</param>
        /// <param name="wickLo">Low of the originating bar's wick (bottom tail).</param>
        /// <returns>Entry price.</returns>
        public static double ComputeEntry(
            OFEntryLevel level, bool isLong,
            double bodyHi, double bodyLo, double wickHi, double wickLo)
        {
            // 2026-05-10: Front-of-zone special case (entry at the body edge
            // closest to the direction of price approach). Long zones are
            // approached from above → front edge is bodyHi. Short zones are
            // approached from below → front edge is bodyLo. SL still anchors
            // at the far wick + buffer (handled in ComputeOrigSL), so Front
            // produces the largest slDist of any level.
            if (level == OFEntryLevel.Front)
                return isLong ? bodyHi : bodyLo;

            // 2026-05-13: trade-phantom-style full-zone percentage entries.
            // Tp25/Tp50/Tp75 measure the entry as N% of the FULL zone height
            // (from the body-edge top of zone all the way to the far wick),
            // not just the wick portion past the body. Demand zones: top of
            // zone = bodyHi; bottom = wickLo. Supply zones: top = wickHi;
            // bottom = bodyLo.
            if (level == OFEntryLevel.Tp25 || level == OFEntryLevel.Tp50 || level == OFEntryLevel.Tp75)
            {
                double tpFrac = 0.25;
                if (level == OFEntryLevel.Tp50)      tpFrac = 0.50;
                else if (level == OFEntryLevel.Tp75) tpFrac = 0.75;

                if (isLong)
                {
                    // Demand: from bodyHi (top of zone) downward to wickLo.
                    double zTop = bodyHi;
                    double zBot = wickLo;
                    return zTop - tpFrac * (zTop - zBot);
                }
                else
                {
                    // Supply: from bodyLo (bottom of zone) upward to wickHi.
                    double zTop = wickHi;
                    double zBot = bodyLo;
                    return zBot + tpFrac * (zTop - zBot);
                }
            }

            // Convert the discrete enum to a [0..1] fraction. Cast through int so
            // we don't accidentally rely on the underlying enum type's order.
            double frac = ((int)level) / 100.0;

            if (isLong)
            {
                // DEMAND: wick extends DOWN from body. Lip is bodyLo, deepest is wickLo.
                // wickHeight here is measured DOWNWARD from the body (positive number).
                // Of0  -> bodyLo
                // Of25 -> bodyLo - 0.25 * wickHeight
                // Of50 -> bodyLo - 0.50 * wickHeight (midpoint of wick)
                // Of75 -> bodyLo - 0.75 * wickHeight (= wickLo + 0.25 * wickHeight)
                double wickHeight = bodyLo - wickLo;
                return bodyLo - wickHeight * frac;
            }
            else
            {
                // SUPPLY: wick extends UP from body. Lip is bodyHi, deepest is wickHi.
                // Of0  -> bodyHi
                // Of25 -> bodyHi + 0.25 * wickHeight
                // Of50 -> bodyHi + 0.50 * wickHeight (midpoint)
                // Of75 -> bodyHi + 0.75 * wickHeight
                double wickHeight = wickHi - bodyHi;
                return bodyHi + wickHeight * frac;
            }
        }

        /// <summary>
        /// Compute the original (pre-trail) stop-loss price. SL is ALWAYS anchored
        /// at the FAR wick plus a buffer in ticks — independent of OF depth. This
        /// is the load-bearing invariant of the OF model: deeper entries shrink
        /// slDist (and grow R), but the SL itself does not move.
        /// </summary>
        /// <param name="isLong">true = long (SL below wickLo), false = short (SL above wickHi).</param>
        /// <param name="wickHi">Originating bar's wick high.</param>
        /// <param name="wickLo">Originating bar's wick low.</param>
        /// <param name="tickSize">Instrument tick size in price units (e.g. 0.25 for ES).</param>
        /// <param name="bufferTicks">Whole-ticks of buffer beyond the far wick (Pine default 2).</param>
        public static double ComputeOrigSL(
            bool isLong,
            double wickHi, double wickLo,
            double tickSize, int bufferTicks)
        {
            // Defensive: negative buffer makes no sense — clamp to 0. tickSize <= 0
            // is also nonsensical (would yield SL == wick); we let the caller's
            // bad input pass through rather than mask it, but never multiply by NaN.
            if (bufferTicks < 0) bufferTicks = 0;
            double buffer = bufferTicks * tickSize;

            return isLong
                ? wickLo - buffer    // long: stop sits BELOW the demand wick low
                : wickHi + buffer;   // short: stop sits ABOVE the supply wick high
        }

        /// <summary>
        /// Compute the TP ladder. TP_n = entry +/- n * tpStep * zoneHeight.
        /// Pine v1.4 default: tpStep=1.0, tpCount=3, producing 1x/2x/3x of zone
        /// height past entry. Slots beyond `tpCount` are 0.0 (sentinel meaning
        /// "no TP at this slot"; callers must not draw 0.0 lines).
        /// </summary>
        /// <param name="isLong">Direction.</param>
        /// <param name="entry">Already-computed entry price.</param>
        /// <param name="zoneHeight">Pre-computed zone height (caller's choice of
        ///   |bodyHi - bodyLo| body-only, or full zTop-zBot including wick — Pine
        ///   v1.4 uses the body delta but exposes the choice to the indicator).</param>
        /// <param name="tpCount">How many TP slots to populate (1..5).</param>
        /// <param name="tpStep">Multiplier per step (Pine input "TP Step", default 1.0).</param>
        /// <returns>Array of length 5; unused slots are 0.0.</returns>
        public static double[] ComputeTPs(
            bool isLong,
            double entry,
            double zoneHeight,
            int tpCount,
            double tpStep)
        {
            // Always return exactly MaxTpSlots so callers can iterate uniformly
            // without a length check. New double[] is zero-initialized in C#.
            double[] tps = new double[MaxTpSlots];

            // Sanitize inputs. A zoneHeight of 0 (degenerate / single-tick body)
            // would collapse all TPs onto the entry — useless and visually noisy.
            // We leave them as 0.0 (the "no TP" sentinel) rather than emit a
            // ladder of identical entry-prices. Callers detect this by checking
            // tps[0] == 0.0 OR by validating zoneHeight upstream.
            if (zoneHeight <= 0.0 || tpStep <= 0.0 || tpCount <= 0)
                return tps;

            // Clamp tpCount to the array bound. We don't throw; this is a hot path.
            if (tpCount > MaxTpSlots) tpCount = MaxTpSlots;

            // Direction multiplier: long extends UP from entry, short extends DOWN.
            // Pine: `isDem ? ent + zH * tpMult * n : ent - zH * tpMult * n`.
            double dir = isLong ? +1.0 : -1.0;

            for (int n = 1; n <= tpCount; n++)
            {
                // n is 1-based (TP1, TP2, ...) — array index is n-1.
                tps[n - 1] = entry + dir * n * tpStep * zoneHeight;
            }

            return tps;
        }

        /// <summary>
        /// Risk leg in price units. Always positive. Used as the denominator for
        /// R-multiple math and (multiplied by point value) for position sizing.
        /// </summary>
        public static double ComputeSlDistance(double entry, double origSL)
        {
            return Math.Abs(entry - origSL);
        }

        /// <summary>
        /// Position size from a fixed-dollar-risk model:
        ///   raw = dollarRisk / (slDist * pointValue)
        /// Floor to int (a partial contract is meaningless on futures), then cap
        /// at the user-specified max. Returns 0 if any input would force a
        /// divide-by-zero or negative size.
        /// </summary>
        /// <param name="dollarRisk">Per-trade $ risk budget (e.g. $100).</param>
        /// <param name="slDist">|entry - origSL| in price units.</param>
        /// <param name="pointValue">$ per 1.00 of price (ES = $50, NQ = $20, etc.).</param>
        /// <param name="maxContracts">User-input ceiling (Pine default 20).</param>
        public static int ComputeContracts(
            double dollarRisk,
            double slDist,
            double pointValue,
            int maxContracts)
        {
            // Guard: any non-positive denominator or budget collapses to zero
            // contracts. NaN slips through as well (NaN <= 0 is false, but the
            // division would produce NaN; we check explicitly).
            if (dollarRisk <= 0.0) return 0;
            if (slDist     <= 0.0) return 0;
            if (pointValue <= 0.0) return 0;
            if (double.IsNaN(slDist) || double.IsNaN(pointValue)) return 0;
            if (maxContracts < 0) return 0;

            // Dollar risk per single contract = slDist (price) * pointValue ($/price).
            // Number of whole contracts the budget supports = dollarRisk / that.
            double riskPerContract = slDist * pointValue;
            double raw = dollarRisk / riskPerContract;

            // Floor (truncate toward zero — same as (int)cast for positive values
            // but Math.Floor is the intent-revealing form).
            int contracts = (int)Math.Floor(raw);

            // Cap at user max, then floor at 0. Order matters: capping first
            // prevents pathologically tiny slDist from emitting 9999 contracts.
            if (contracts > maxContracts) contracts = maxContracts;
            if (contracts < 0)            contracts = 0;
            return contracts;
        }

        /// <summary>
        /// Signed R-multiple for an arbitrary price relative to the trade's entry
        /// and risk leg. Positive = profit, negative = loss, regardless of side.
        /// Formula:
        ///   long:  (price - entry) / slDist
        ///   short: (entry - price) / slDist
        /// Used by the LOCK/UNRL/FLOAT live strip and for closed-trade R stats.
        /// </summary>
        public static double ComputeRMultiple(
            bool isLong, double entry, double price, double slDist)
        {
            // Defensive: slDist == 0 would NaN out the live strip on every tick.
            // Return 0.0 (neutral) rather than propagating NaN into UI cells.
            if (slDist <= 0.0 || double.IsNaN(slDist)) return 0.0;

            // Direction-symmetric: flip the numerator sign for shorts so a
            // favorable move always yields a positive number.
            double diff = isLong ? (price - entry) : (entry - price);
            return diff / slDist;
        }

        /// <summary>
        /// Convert an R-multiple back to dollars. Trivial but centralized so the
        /// indicator's P&L cells, alert payloads, and stats all share one
        /// definition (and the unit semantics are documented in one place).
        /// </summary>
        /// <param name="rMultiple">Signed R units (output of ComputeRMultiple).</param>
        /// <param name="dollarRisk">The same $ risk budget used for sizing.</param>
        /// <returns>Signed dollar P&L for that R-multiple at this risk size.</returns>
        public static double ComputeDollarPnL(double rMultiple, double dollarRisk)
        {
            return rMultiple * dollarRisk;
        }

        // =====================================================================
        // TICK SNAPPING — added 2026-05-11 so the indicator emits prices
        // brokers will actually accept without silent rounding.
        //
        // Most brokers reject (or auto-round) limit/stop orders placed at a
        // price between two valid ticks. Deep-OF entries (OF25/OF50/OF75)
        // interpolate within the wick height and frequently land sub-tick
        // — we snap before publish so every downstream consumer
        // (TradeIntentChannel, zone_armed event, reference lines, size label)
        // sees the same tick-aligned values.
        //
        // Snap policy:
        //   • Entry  → SnapToTickNearest. Half-tick drift in either direction
        //              doesn't materially change fill probability for a limit.
        //   • SL     → SnapAwayFromReference(entry). Round AWAY from entry so
        //              the stop is NEVER tightened by rounding noise. Demand
        //              long: SL < entry → round DOWN; Supply short: SL >
        //              entry → round UP.
        //   • TPs    → SnapAwayFromReference(entry). Same logic mirrored —
        //              never make a target easier to hit via rounding.
        // =====================================================================

        /// <summary>
        /// Round <paramref name="price"/> to the nearest multiple of
        /// <paramref name="tickSize"/>. Returns <paramref name="price"/>
        /// unchanged when tickSize is non-positive (degenerate symbol).
        /// </summary>
        public static double SnapToTickNearest(double price, double tickSize)
        {
            if (tickSize <= 0) return price;
            if (double.IsNaN(price) || double.IsInfinity(price)) return price;
            return Math.Round(price / tickSize) * tickSize;
        }

        /// <summary>
        /// Round <paramref name="price"/> AWAY from <paramref name="reference"/>:
        ///   • price > reference → ceiling to the next tick
        ///   • price &lt; reference → floor to the previous tick
        ///   • price == reference → unchanged
        /// Used for SL (never tighten the stop) and TPs (never make the target
        /// easier to hit) so rounding noise can only hurt the trader's
        /// expectancy in the structurally safe direction.
        /// </summary>
        public static double SnapAwayFromReference(double price, double reference, double tickSize)
        {
            if (tickSize <= 0) return price;
            if (double.IsNaN(price) || double.IsInfinity(price)) return price;
            if (price > reference) return Math.Ceiling(price / tickSize) * tickSize;
            if (price < reference) return Math.Floor  (price / tickSize) * tickSize;
            return price;
        }
    }
}
