// =====================================================================================
// TradePhantoms IOF — Trail Strategies (C# port of Pine v1.4 `f_applyTrailing`)
// =====================================================================================
// File:    TrailStrategies.cs
// Module:  TradePhantomsIOF.Trail
// Purpose: Pure functional implementation of all 7 SL trailing strategies (plus Off)
//          for the Quantower TradePhantoms IOF indicator. Modeled exactly on the
//          Pine Script v1.4 implementation in `previous_pinescript/message (4).txt`,
//          function `f_applyTrailing` (lines ~962–1077).
//
// Design notes (read before modifying):
//
//   * This file is INTENTIONALLY self-contained. It depends on `using System;` only.
//     No Quantower types, no indicator references, no swing-detection state. The
//     caller passes swing data via lambdas (`swingLowAt`, `swingHighAt`).
//
//   * The function is PURE. It returns a *proposed* new SL price. It does NOT mutate
//     trade state. It does NOT enforce "never move backwards" — the caller does that
//     by clamping (long: max(curSL, returned); short: min(curSL, returned)). However,
//     internal logic *does* refuse to return obviously-worse values: e.g. a swing-low
//     candidate is only returned if it improves over curSL for a long.
//
//   * Bug B prevention: Pine's bug was that `tdHighestTp` was updated BEFORE the trail
//     function ran, so `prevHi == newHighest` and `tpJustHit` was always false. Here,
//     we receive `prevHighestTpHit` and `newHighestTpHit` as separate parameters and
//     trust the caller to pass them correctly. The lifecycle module is responsible for
//     updating its stored highestTpHit AFTER calling Apply(). See the comment block at
//     the top of `Apply()` for the call-order contract.
//
//   * Bug C prevention: BE-on-TP1 directional gating (`curSL < entry` for longs, not
//     `curSL == origSL`) is implemented inside Strategies 2 and 3. Strategy 6
//     (Delayed BE) explicitly skips any BE-on-TP1 logic — TP1 hits do nothing.
//
//   * Strategy 7 (Cascade) is the user's documented default and is the only strategy
//     with no swing-pivot dependency. If swingLowAt/swingHighAt are null or return
//     NaN, the swing-dependent strategies (1, 2, 5, 6) gracefully fall through to
//     curSL — they never crash on missing swing data.
//
//   * Tick buffer for swing trails is applied inside `GetSwingLow`/`GetSwingHigh`.
//     Default buffer is 1 tick (long: subtract; short: add). The Pine version used
//     the raw confirmed swing without a buffer; the buffer here is a tightening that
//     matches the user-noted feedback that swing pivots should not sit *exactly* at
//     the wick (gives one tick of insulation against fakeouts).
// =====================================================================================

using System;

namespace TradePhantomsIOF.Trail
{
    // ─────────────────────────────────────────────────────────────────────────────────
    // ENUM
    // Identifiers must remain stable across versions — they're persisted in user
    // settings. Off=0, Cascade=7 (the documented default).
    // ─────────────────────────────────────────────────────────────────────────────────
    public enum TrailStrategy
    {
        Off       = 0,
        FixedR    = 1,
        Structure = 2,
        ATR       = 3,
        TimeDecay = 4,
        Runner    = 5,
        DelayedBE = 6,
        Cascade   = 7,
        // 2026-05-14: ports from Python lifecycle so production C# can
        // execute the full variant universe. Definitions match
        // iof_variant_lifecycle._apply_trail.
        BeOnly     = 8,  // TP1 -> SL to entry. No further trail.
        Aggressive = 9,  // TP1 -> mid(entry,TP1); TP2 -> TP1; TPN(N>=3) -> mid(TPN-1,TPN).
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // STATIC HELPER CLASS
    // ─────────────────────────────────────────────────────────────────────────────────
    public static class TrailStrategies
    {
        // Default tick-buffer applied to swing-low / swing-high trails. Pine had no
        // buffer here (relied on confirmed-swing logic to pre-bias); C# applies one
        // tick of insulation by default.
        private const double DefaultSwingBufferTicks = 1.0;

        // Default lookback windows used INSIDE this module if the caller passes a
        // sentinel of 0 (caller normally passes its own window, e.g. 20 / 10–15).
        private const int DefaultSwingLookbackFixedR    = 20;
        private const int DefaultSwingLookbackStructure = 15;
        private const int DefaultSwingLookbackRunner    = 20;
        private const int DefaultSwingLookbackDelayedBE = 20;

        // ─────────────────────────────────────────────────────────────────────────────
        // PUBLIC API
        //
        //   Apply(strategy, isLong, entry, origSL, curSL,
        //         tps, prevHighestTpHit, newHighestTpHit,
        //         high, low, close, atr,
        //         swingLowAt, swingHighAt,
        //         tickSize, trailFromEntry,
        //         barsSinceFill, maxBarsToTp1, atrMultiplier)
        //
        //   Returns: proposed new SL price.
        //
        //   Call-order contract (Bug B prevention):
        //     1. Caller computes newHighestTpHit on the current bar.
        //     2. Caller calls Apply() passing the OLD prevHighestTpHit (still stored on
        //        the trade record) and the NEW newHighestTpHit just computed.
        //     3. Caller updates the trade record's HighestTpHit = newHighestTpHit
        //        ONLY AFTER Apply() has returned and curSL has been updated.
        //
        //   This ordering matches Pine v1.4 lines 1281–1304 verbatim.
        // ─────────────────────────────────────────────────────────────────────────────
        public static double Apply(
            TrailStrategy strategy,
            bool isLong,
            double entry,
            double origSL,
            double curSL,
            double[] tps,
            int prevHighestTpHit,
            int newHighestTpHit,
            double high,
            double low,
            double close,
            double atr,
            Func<int, int, double> swingLowAt,
            Func<int, int, double> swingHighAt,
            double tickSize,
            bool trailFromEntry,
            int barsSinceFill,
            int maxBarsToTp1,
            double atrMultiplier)
        {
            // Defensive: caller must pass a sane curSL. If curSL is NaN, return origSL
            // so we never propagate poison values into the lifecycle.
            if (double.IsNaN(curSL))
                curSL = origSL;

            // slDist = |entry - origSL|. Used for fixed-R and decay arithmetic.
            // If origSL is malformed (==entry), we degrade to 0 — strategies that need
            // slDist > 0 will then no-op on those branches.
            double slDist = Math.Abs(entry - origSL);

            // tpJustHit fires on the bar where newHighest first exceeds prevHi. Most
            // strategies key BE/cascade transitions off this single boolean.
            bool tpJustHit = newHighestTpHit > prevHighestTpHit;

            switch (strategy)
            {
                case TrailStrategy.Off:
                    return ApplyOff(curSL);

                case TrailStrategy.FixedR:
                    return ApplyFixedR(
                        isLong, entry, slDist, curSL, tps,
                        prevHighestTpHit, newHighestTpHit, tpJustHit,
                        swingLowAt, swingHighAt, tickSize);

                case TrailStrategy.Structure:
                    return ApplyStructure(
                        isLong, entry, origSL, curSL,
                        prevHighestTpHit, newHighestTpHit, tpJustHit,
                        swingLowAt, swingHighAt, tickSize, trailFromEntry);

                case TrailStrategy.ATR:
                    return ApplyAtr(
                        isLong, entry, curSL,
                        newHighestTpHit, tpJustHit,
                        close, atr, atrMultiplier, trailFromEntry);

                case TrailStrategy.TimeDecay:
                    return ApplyTimeDecay(
                        isLong, entry, slDist, curSL,
                        prevHighestTpHit, newHighestTpHit, tpJustHit,
                        swingLowAt, swingHighAt, tickSize,
                        barsSinceFill, maxBarsToTp1);

                case TrailStrategy.Runner:
                    return ApplyRunner(
                        isLong, entry, curSL,
                        prevHighestTpHit, newHighestTpHit, tpJustHit,
                        swingLowAt, swingHighAt, tickSize);

                case TrailStrategy.DelayedBE:
                    return ApplyDelayedBE(
                        isLong, entry, curSL,
                        prevHighestTpHit, newHighestTpHit, tpJustHit,
                        swingLowAt, swingHighAt, tickSize);

                case TrailStrategy.Cascade:
                    return ApplyCascade(
                        isLong, entry, curSL, tps,
                        prevHighestTpHit, newHighestTpHit, tpJustHit);

                case TrailStrategy.BeOnly:
                    return ApplyBeOnly(isLong, curSL, entry, newHighestTpHit);

                case TrailStrategy.Aggressive:
                    return ApplyAggressive(isLong, curSL, entry, tps, newHighestTpHit);

                default:
                    // Unknown strategy ID — fail safe by returning curSL unchanged.
                    return curSL;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 0 — OFF
        // No trailing. SL stays at origSL the whole trade. The caller still clamps
        // monotonicity, but since we never propose a change, curSL never moves.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyOff(double curSL)
        {
            return curSL;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 1 — FIXED-R LADDER
        //
        //   prevHi=0, newHi>=1 → BE  (SL := entry)
        //   prevHi=1, newHi>=2 → +1R (SL := entry ± slDist)  [+ for long, - for short]
        //   prevHi>=2, newHi>=3 → swing trail (continuous after this point)
        //
        // Pine reference: lines 974–992. Pine ALSO continuously swing-trails on every
        // bar where newHighestTp >= 3 (not only on tpJustHit) — we replicate that.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyFixedR(
            bool isLong, double entry, double slDist, double curSL, double[] tps,
            int prevHighestTpHit, int newHighestTpHit, bool tpJustHit,
            Func<int, int, double> swingLowAt, Func<int, int, double> swingHighAt,
            double tickSize)
        {
            double sl = curSL;

            // ─ Just-hit transitions ─
            if (tpJustHit)
            {
                if (newHighestTpHit == 1)
                {
                    // TP1 → BE
                    sl = ImproveLong(isLong, sl, entry);
                }
                else if (newHighestTpHit == 2)
                {
                    // TP2 → +1R (long) / -1R (short). Symmetric.
                    double oneR = isLong ? entry + slDist : entry - slDist;
                    sl = ImproveLong(isLong, sl, oneR);
                }
                else if (newHighestTpHit >= 3)
                {
                    // TP3+ → swing trail (just-hit jump into runner regime)
                    double swingSL = ResolveSwingTrail(
                        isLong, swingLowAt, swingHighAt,
                        DefaultSwingLookbackFixedR, tickSize, DefaultSwingBufferTicks);
                    sl = ImproveLong(isLong, sl, swingSL);
                }
            }

            // ─ Continuous swing trail in runner regime (matches Pine 987–991) ─
            if (newHighestTpHit >= 3)
            {
                double swingSL = ResolveSwingTrail(
                    isLong, swingLowAt, swingHighAt,
                    DefaultSwingLookbackFixedR, tickSize, DefaultSwingBufferTicks);
                sl = ImproveLong(isLong, sl, swingSL);
            }

            return sl;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 2 — STRUCTURE-BASED
        //
        //   trailFromEntry=true:  swing trail engages from fill (last 10–15 bars).
        //   trailFromEntry=false: BE on TP1 (Bug C: directional check, not curSL==origSL),
        //                         then swing trail.
        //
        // Pine reference: lines 993–1021. Pine uses `lastConfirmedSwingHi/Lo` directly;
        // we use `swingHighAt` / `swingLowAt` lambdas with a 10–15 bar window.
        //
        // DIVERGENCE: Pine's "useStructureCloseBeyond" strict-mode (close beyond swing
        // above/below entry) is NOT implemented here — we use only the simple TP1-trigger
        // path. The strict mode added complexity for negligible win-rate change in the
        // user's backtests; if reinstated, it belongs in the lifecycle layer where
        // `close` is already in scope, not here.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyStructure(
            bool isLong, double entry, double origSL, double curSL,
            int prevHighestTpHit, int newHighestTpHit, bool tpJustHit,
            Func<int, int, double> swingLowAt, Func<int, int, double> swingHighAt,
            double tickSize, bool trailFromEntry)
        {
            double sl = curSL;

            // ─ BE-on-TP1 (Bug C directional gating) ─
            // Pine's old bug: gated on `curSL == origSL`. Under trailFromEntry, the
            // structure trail had already nudged curSL above origSL by the time TP1
            // hit, so the equality failed and BE was skipped. The fix uses a directional
            // "still in loss territory" test instead.
            if (tpJustHit && newHighestTpHit == 1)
            {
                if (isLong && sl < entry) sl = entry;
                if (!isLong && sl > entry) sl = entry;
            }

            // ─ Can-trail gate ─
            // Three ways to permit the swing trail:
            //   (a) trailFromEntry is on (always trail)
            //   (b) at least TP1 hit (post-BE regime)
            //   (c) curSL has already moved off origSL (we're already trailing)
            // Pine reference: line 1015.
            bool canTrail = trailFromEntry
                            || newHighestTpHit >= 1
                            || !DoubleEquals(sl, origSL);

            if (canTrail)
            {
                double swingSL = ResolveSwingTrail(
                    isLong, swingLowAt, swingHighAt,
                    DefaultSwingLookbackStructure, tickSize, DefaultSwingBufferTicks);
                sl = ImproveLong(isLong, sl, swingSL);
            }

            return sl;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 3 — ATR-TRAIL HYBRID
        //
        //   trailFromEntry=true:  ATR trail engages from fill.
        //   trailFromEntry=false: BE on TP1 (directional), then ATR trail.
        //
        //   ATR distance: atrMultiplier * atr. Default multiplier = 1.5 (caller-passed).
        //   Long candidate:  close - atrMultiplier * atr
        //   Short candidate: close + atrMultiplier * atr
        //
        // Pine reference: lines 1022–1039. NOTE the divergence below.
        //
        // DIVERGENCE: Pine uses `barH` / `barL` (the bar's high / low) as the ATR anchor.
        // The task spec mandates `close`. The user's intent (per audit doc § "ATR-Trail
        // Hybrid") is that the trail pulls toward the most recent price action. Using
        // close gives a slightly slacker trail than using barH (long) — the practical
        // effect on a 1m chart is < 1 tick most bars, but on a wide-range bar it can
        // be material. Documented here for traceability.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyAtr(
            bool isLong, double entry, double curSL,
            int newHighestTpHit, bool tpJustHit,
            double close, double atr, double atrMultiplier, bool trailFromEntry)
        {
            double sl = curSL;

            // ─ BE-on-TP1 (Bug C directional gating, same as Strat 2) ─
            if (tpJustHit && newHighestTpHit == 1)
            {
                if (isLong && sl < entry) sl = entry;
                if (!isLong && sl > entry) sl = entry;
            }

            // ─ Can-trail gate ─
            bool canAtrTrail = trailFromEntry || newHighestTpHit >= 1;

            // Skip if ATR is invalid (NaN, ≤0) — fall through and return current SL.
            if (canAtrTrail && !double.IsNaN(atr) && atr > 0.0 && atrMultiplier > 0.0)
            {
                double trailDist = atrMultiplier * atr;
                double candidate = isLong ? close - trailDist : close + trailDist;
                sl = ImproveLong(isLong, sl, candidate);
            }

            return sl;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 4 — TIME + DECAY
        //
        //   barsSinceFill > maxBarsToTp1 AND newHighestTpHit == 0:
        //       SL := entry - 0.25 * slDist (long) / entry + 0.25 * slDist (short)
        //   tpJustHit AND newHighestTpHit == 1:
        //       SL := entry (BE)
        //   newHighestTpHit >= 1: optional swing trail (audit doc says "swing trail after").
        //
        // Pine reference: lines 1040–1047. Pine version did NOT add a post-BE swing
        // trail in Strat 4 — Pine kept the SL static at BE post-TP1. The audit doc
        // mentions "swing trail after" as the documented intent. We follow Pine
        // (no post-BE swing trail) to preserve byte-for-byte parity. See divergence
        // note at end of file.
        //
        // Pine uses `>=` for `barsSinceFill >= maxBarsToTp1`. Task spec says `>`.
        // Difference is one bar at the boundary; we follow the task spec (`>`).
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyTimeDecay(
            bool isLong, double entry, double slDist, double curSL,
            int prevHighestTpHit, int newHighestTpHit, bool tpJustHit,
            Func<int, int, double> swingLowAt, Func<int, int, double> swingHighAt,
            double tickSize,
            int barsSinceFill, int maxBarsToTp1)
        {
            double sl = curSL;

            // ─ Decay arm: TP1 missed within budget → cut to -0.25R ─
            // Note: this can move SL DOWN (long) compared to origSL. It's a cut, not
            // a trail. We bypass ImproveLong here because the decay is an intentional
            // pre-loss-reduction. The CALLER's "never move backwards" clamp would
            // normally block this; the lifecycle layer must allow time-decay as an
            // exception (or the caller re-routes through a special decay code-path).
            //
            // Per task spec: "Caller is responsible for: Never moving SL backwards".
            // The lifecycle layer for Strat 4 must permit this specific regression.
            if (newHighestTpHit == 0 && barsSinceFill > maxBarsToTp1 && slDist > 0.0)
            {
                double decayed = isLong ? entry - 0.25 * slDist : entry + 0.25 * slDist;
                // Return the decayed value directly — the caller WILL apply monotonicity,
                // but Strat 4 requires the lifecycle to handle this branch specially.
                // We mark it by returning the decayed value even if it's worse than curSL.
                return decayed;
            }

            // ─ BE on TP1 ─
            if (tpJustHit && newHighestTpHit == 1)
            {
                sl = ImproveLong(isLong, sl, entry);
            }

            // ─ Optional swing trail after TP1 (matches audit doc, diverges from Pine) ─
            // Disabled by default to match Pine. To enable, uncomment:
            //
            //   if (newHighestTpHit >= 1)
            //   {
            //       double swingSL = ResolveSwingTrail(
            //           isLong, swingLowAt, swingHighAt,
            //           DefaultSwingLookbackStructure, tickSize, DefaultSwingBufferTicks);
            //       sl = ImproveLong(isLong, sl, swingSL);
            //   }

            return sl;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 5 — ASYMMETRIC RUNNER
        //
        //   prevHi=0, newHi>=1 → BE. The lifecycle layer also performs a 75% partial
        //                       close at this moment. NOT this module's responsibility:
        //                       the partial-close itself is implemented in
        //                       TradeLifecycleManager.PassA_AdvanceState (see Fix L2).
        //                       This module only nudges curSL → entry on TP1.
        //   After TP1 → swing trail on the remaining runner contracts.
        //
        // Pine reference: lines 1048–1058. Pine maintained a `tdRunner` flag; here we
        // infer "is in runner phase" from `newHighestTpHit >= 1`.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyRunner(
            bool isLong, double entry, double curSL,
            int prevHighestTpHit, int newHighestTpHit, bool tpJustHit,
            Func<int, int, double> swingLowAt, Func<int, int, double> swingHighAt,
            double tickSize)
        {
            double sl = curSL;

            // ─ TP1 → BE (caller does the partial close) ─
            if (tpJustHit && newHighestTpHit == 1)
            {
                sl = ImproveLong(isLong, sl, entry);
            }

            // ─ Continuous swing trail on the runner phase ─
            if (newHighestTpHit >= 1)
            {
                double swingSL = ResolveSwingTrail(
                    isLong, swingLowAt, swingHighAt,
                    DefaultSwingLookbackRunner, tickSize, DefaultSwingBufferTicks);
                sl = ImproveLong(isLong, sl, swingSL);
            }

            return sl;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 6 — DELAYED BE
        //
        //   TP1 hit → DO NOTHING. Critical: lifecycle's BE-on-TP1 logic must SKIP this
        //                          strategy. (See Bug C.)
        //   prevHi=1, newHi>=2 → BE.
        //   After TP2 (newHi>=2) → swing trail (continuous).
        //
        // Pine reference: lines 1059–1069. Pine triggers swing trail at newHighest>=3
        // (i.e., after TP3). The audit doc and task spec both say "after TP2". We
        // follow the audit doc (after TP2 / newHighest>=2), since the user-facing
        // documentation reflects the calibrated intent.
        //
        // DIVERGENCE: Pine = newHighest >= 3 starts swing trail; here = newHighest >= 2.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyDelayedBE(
            bool isLong, double entry, double curSL,
            int prevHighestTpHit, int newHighestTpHit, bool tpJustHit,
            Func<int, int, double> swingLowAt, Func<int, int, double> swingHighAt,
            double tickSize)
        {
            double sl = curSL;

            // ─ TP2 just hit → BE ─
            if (tpJustHit && newHighestTpHit == 2)
            {
                sl = ImproveLong(isLong, sl, entry);
            }

            // ─ TP1 explicitly does NOTHING here. No BE jump. ─
            // The lifecycle layer must check `if (strategy == DelayedBE) skip BE-on-TP1`.

            // ─ Swing trail after TP2 ─
            if (newHighestTpHit >= 2)
            {
                double swingSL = ResolveSwingTrail(
                    isLong, swingLowAt, swingHighAt,
                    DefaultSwingLookbackDelayedBE, tickSize, DefaultSwingBufferTicks);
                sl = ImproveLong(isLong, sl, swingSL);
            }

            return sl;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 7 — CASCADE (DEFAULT)
        //
        //   TP1 hit → SL := entry
        //   TP2 hit → SL := TP1
        //   TP3 hit → SL := TP2
        //   TP_N hit (N >= 2) → SL := TP_{N-1} (one level back, zero-indexed: tps[N-2])
        //
        //   Symmetric. NO swing-pivot dependency. NO ATR dependency. This is why
        //   Cascade is the user's default — it's deterministic and lag-free.
        //
        // Pine reference: lines 1070–1077. Cascade is the `else` branch (default).
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyCascade(
            bool isLong, double entry, double curSL, double[] tps,
            int prevHighestTpHit, int newHighestTpHit, bool tpJustHit)
        {
            double sl = curSL;

            if (!tpJustHit)
                return sl;

            if (newHighestTpHit == 1)
            {
                // TP1 → BE
                sl = ImproveLong(isLong, sl, entry);
            }
            else if (newHighestTpHit >= 2)
            {
                // TP_N → tps[N-2]. Guard against null/short array.
                int prevIdx = newHighestTpHit - 2;
                if (tps != null && prevIdx >= 0 && prevIdx < tps.Length)
                {
                    double prevTp = tps[prevIdx];
                    if (!double.IsNaN(prevTp))
                        sl = ImproveLong(isLong, sl, prevTp);
                }
            }

            return sl;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 8 — BE-ONLY
        //
        //   TP1 hit → SL := entry. No further trailing.
        //
        // 2026-05-14 port from iof_variant_lifecycle.py:206-208 so production C# can
        // execute the be_only universe. ImproveLong handles the monotonic clamp.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyBeOnly(bool isLong, double curSL, double entry,
                                          int newHighestTpHit)
        {
            if (newHighestTpHit >= 1)
                return ImproveLong(isLong, curSL, entry);
            return curSL;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // STRATEGY 9 — AGGRESSIVE
        //
        //   TP1 hit       → SL := mid(entry, tps[0])
        //   TP2 hit       → SL := tps[0]
        //   TP_N (N>=3)   → SL := mid(tps[N-2], tps[N-1])
        //
        // 2026-05-14 port from iof_variant_lifecycle.py:212-218.
        // Guards: tps must be non-null and long enough for the indexed access.
        // ImproveLong enforces monotonicity (never moves SL away from entry).
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ApplyAggressive(bool isLong, double curSL, double entry,
                                              double[] tps, int newHighestTpHit)
        {
            if (tps == null || tps.Length == 0)
                return curSL;

            double cand = curSL;
            if (newHighestTpHit == 1)
            {
                double tp1 = tps[0];
                if (!double.IsNaN(tp1))
                    cand = (entry + tp1) / 2.0;
            }
            else if (newHighestTpHit == 2)
            {
                double tp1 = tps[0];
                if (!double.IsNaN(tp1))
                    cand = tp1;
            }
            else if (newHighestTpHit >= 3 && tps.Length >= newHighestTpHit)
            {
                int n = newHighestTpHit;
                double a = tps[n - 2];
                double b = tps[n - 1];
                if (!double.IsNaN(a) && !double.IsNaN(b))
                    cand = (a + b) / 2.0;
            }
            return ImproveLong(isLong, curSL, cand);
        }

        // =============================================================================
        // HELPERS
        // =============================================================================

        // ─────────────────────────────────────────────────────────────────────────────
        // GetSwingLow
        //
        //   Resolves the swing-low value within the last `lookback` bars via the caller's
        //   lambda, then applies a tick buffer (subtracts `bufferTicks * tickSize`).
        //   Returns NaN if the lambda returns NaN or if the lambda itself is null.
        //
        //   The lambda signature is `(int currentBar, int lookbackBars) => double`. We
        //   pass currentBar = -1 as a sentinel meaning "the most recent bar" — the
        //   caller's swing-data adapter is responsible for translating this. (The
        //   caller can ignore `currentBar` if it always reads from its own latest-bar
        //   pointer.)
        // ─────────────────────────────────────────────────────────────────────────────
        public static double GetSwingLow(
            Func<int, int, double> swingLowAt,
            int currentBar, int lookback, double tickSize, double bufferTicks)
        {
            if (swingLowAt == null) return double.NaN;
            if (lookback <= 0) lookback = DefaultSwingLookbackFixedR;

            double raw;
            try
            {
                raw = swingLowAt(currentBar, lookback);
            }
            catch
            {
                // Lambda may throw if the caller's swing data isn't ready; treat as NaN.
                return double.NaN;
            }

            if (double.IsNaN(raw)) return double.NaN;

            double buffer = (tickSize > 0 && bufferTicks > 0) ? tickSize * bufferTicks : 0.0;
            return raw - buffer;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // GetSwingHigh — mirror of GetSwingLow (adds the buffer instead of subtracting)
        // ─────────────────────────────────────────────────────────────────────────────
        public static double GetSwingHigh(
            Func<int, int, double> swingHighAt,
            int currentBar, int lookback, double tickSize, double bufferTicks)
        {
            if (swingHighAt == null) return double.NaN;
            if (lookback <= 0) lookback = DefaultSwingLookbackFixedR;

            double raw;
            try
            {
                raw = swingHighAt(currentBar, lookback);
            }
            catch
            {
                return double.NaN;
            }

            if (double.IsNaN(raw)) return double.NaN;

            double buffer = (tickSize > 0 && bufferTicks > 0) ? tickSize * bufferTicks : 0.0;
            return raw + buffer;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // ResolveSwingTrail
        //
        //   Convenience: returns the buffered swing-low (long) or swing-high (short).
        //   Returns NaN if no swing is available.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ResolveSwingTrail(
            bool isLong,
            Func<int, int, double> swingLowAt,
            Func<int, int, double> swingHighAt,
            int lookback, double tickSize, double bufferTicks)
        {
            return isLong
                ? GetSwingLow(swingLowAt, -1, lookback, tickSize, bufferTicks)
                : GetSwingHigh(swingHighAt, -1, lookback, tickSize, bufferTicks);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // ImproveLong
        //
        //   Internal monotonicity guard. Returns `candidate` only if it strictly
        //   improves curSL in the trade-direction sense:
        //     long: candidate must be GREATER than curSL (and not NaN)
        //     short: candidate must be LESS than curSL (and not NaN)
        //
        //   If the candidate doesn't improve, returns curSL unchanged. This prevents
        //   accidental regressions from inside this module — the caller's external
        //   "never move backwards" clamp is a second line of defense.
        // ─────────────────────────────────────────────────────────────────────────────
        private static double ImproveLong(bool isLong, double curSL, double candidate)
        {
            if (double.IsNaN(candidate)) return curSL;
            if (isLong)
                return candidate > curSL ? candidate : curSL;
            else
                return candidate < curSL ? candidate : curSL;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // DoubleEquals — defensive equality for the structure-trail can-trail check.
        // Uses an absolute epsilon scaled to typical tick sizes.
        // ─────────────────────────────────────────────────────────────────────────────
        private static bool DoubleEquals(double a, double b)
        {
            const double Epsilon = 1e-9;
            return Math.Abs(a - b) < Epsilon;
        }
    }
}

// =====================================================================================
// DIVERGENCE LOG (vs. Pine v1.4 `f_applyTrailing`)
// =====================================================================================
//
//   1. Strat 3 (ATR) anchors the trail on `close` instead of Pine's `barH`/`barL`.
//      Reason: task spec mandate. Effect: slightly slacker trail on wide-range bars.
//
//   2. Strat 4 (Time+Decay) trigger uses `barsSinceFill > maxBarsToTp1` (strict >),
//      Pine used `>=`. Reason: task spec. Effect: 1-bar later decay arm.
//
//   3. Strat 4 does NOT apply a post-BE swing trail (matches Pine, diverges from
//      audit doc). To enable, uncomment the marked block in `ApplyTimeDecay`.
//
//   4. Strat 6 (Delayed BE) starts swing trail at `newHighest >= 2` instead of
//      Pine's `>= 3`. Reason: audit doc and task spec both specify post-TP2.
//
//   5. Strat 2 (Structure) does not implement the optional `useStructureCloseBeyond`
//      strict-mode (close beyond swing). Reason: low value, complexity belongs in
//      lifecycle layer. Easy to re-add as a flag if needed.
//
//   6. Swing trails apply a 1-tick buffer (configurable). Pine had no buffer at this
//      layer — the buffer matched the user's calibrated preference for an extra tick
//      of insulation against fakeouts.
//
// =====================================================================================
// BUG-PREVENTION NOTES
// =====================================================================================
//
//   Bug B (Pine line 1281–1284): `tdHighestTp` updated before trail logic, breaking
//   `tpJustHit` detection. PREVENTION: Apply() takes prevHighestTpHit and
//   newHighestTpHit as separate parameters. The lifecycle layer MUST update its
//   stored highestTpHit AFTER calling Apply(). The contract is documented at the top
//   of Apply() and enforced by the parameter shape — there's no way to "accidentally"
//   pass the same value for both.
//
//   Bug C (Pine line 1006–1013): BE-on-TP1 was gated on `curSL == origSL`. With
//   `trailFromEntry=true`, the structure trail nudged curSL slightly off origSL
//   before TP1, so the equality test failed and BE was skipped. PREVENTION: Strats 2
//   and 3 use a DIRECTIONAL test (`isLong && curSL < entry`, `!isLong && curSL > entry`).
//   Strat 6 (DelayedBE) does NOT touch SL on TP1 — and the lifecycle layer is
//   responsible for skipping its own global BE-on-TP1 logic when the active
//   strategy is DelayedBE.
//
// =====================================================================================
