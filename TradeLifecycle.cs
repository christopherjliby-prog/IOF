// ════════════════════════════════════════════════════════════════════════════
// TradeLifecycle.cs — Trade lifecycle state machine for the IOF Quantower port
// ════════════════════════════════════════════════════════════════════════════
//
// Self-contained C# port of the Pine Script v1.4 IOF Quick Entry lifecycle
// layer (`message (4).txt`, lines ~850-1450). Implements:
//
//   * Trade record state (ARMED → ACTIVE → WIN/LOSS/BE)
//   * Four-pass per-bar loop (PASS A advance, B drop sentinels, C arm new,
//     D FIFO cap closed)
//   * Exit-reason classification (SL / TPn / TRLn / TRL0 / BE / Force)
//   * R / dollar / LOCK / UNRL / FLOAT math
//   * Hooks for an external `TrailStrategies.Apply(...)` helper (built by A2)
//
// The Pine version went through ~7 lifecycle bugs during development. Each
// fix is preserved here with a "BUG <letter>" comment marker. The bug letters
// in this file map to the lifecycle bug list in
// feedback_iof_hard_exclusions.md (numbered 1-7 there) and to the audit
// chronology bugs #12-#18 in IOF_QuickEntry_Project_Audit.md:
//
//   BUG A — state-mutation read-after-write (audit #15 / feedback #1)
//   BUG B — TP-detection ordering: update HighestTpHit AFTER trail (audit #13 / feedback #2)
//   BUG C — BE-on-TP1 directional check, not "curSL == origSL" (audit #14 / feedback #3)
//   BUG D — Force-close uses curSL, not origSL (audit #12 / feedback #4)
//   BUG E — ARMED needs proximity-drift drop AND zone-invalidation drop (audit #16 / feedback #5)
//   BUG F — Visibility-cap iteration order: newest→oldest (audit #17 / feedback #6)
//          (relevant in the consumer's render loop; this file just exposes
//           GetClosed() in newest-first order so callers don't need to sort)
//   BUG G — TRL0 vs SL distinction for trail-outs with no TPs hit (audit #18 / feedback #7)
//   BUG H — Optional trend-broken-against-active-trade close path (Fix L5)
//          Added CloseOnTrendBroken field + TrendStateFallback delegate hook +
//          ExitReason.TrendBroken for optional trend-reversal-against-active-
//          trade auto-close. Per TP + PDF doctrine: "trend is broken when the
//          control point is broken" — but this manager defaults to passive
//          (off); user must opt in. Wired by master OnInit:
//            TrendStateFallback.GetCurrentTrendState =
//                () => (int)trendStateMachine.CurrentState;
//
// File is single-namespace, no external dependencies beyond System.* and
// System.Collections.Generic (Quantower SDK is not actually invoked from
// this file — the lifecycle layer is pure logic).
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using TradePhantomsIOF; // for OFEntryLevel + EntryTPMath (Fix L1)

namespace TradePhantomsIOF.Lifecycle
{
    // ────────────────────────────────────────────────────────────────────────
    // ENUMS
    // Values match the Pine source numerically where the Pine code used ints
    // (TradeState 0..4 + sentinel -1; TrailStrategy 0..7).
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Trade lifecycle state. Sentinel (-1) means "marked for drop in PASS B".
    /// </summary>
    public enum TradeState
    {
        Sentinel  = -1,
        Armed     = 0,
        Active    = 1,
        Win       = 2,
        Loss      = 3,
        BreakEven = 4
    }

    /// <summary>Tier of the IBI zone the trade was sourced from.</summary>
    public enum ZoneTier
    {
        LTF = 0,
        ITF = 1,
        HTF = 2
    }

    /// <summary>Direction of the zone (Demand = long bias, Supply = short bias).</summary>
    public enum ZoneDirection
    {
        Demand = 0,
        Supply = 1
    }

    /// <summary>
    /// Trail strategy selector. Numeric values mirror the Pine `trailId`
    /// dropdown (Off=0, FixedR=1, Structure=2, ATR=3, TimeDecay=4, Runner=5,
    /// DelayedBE=6, Cascade=7). Cascade is the user's documented default.
    /// </summary>
    public enum TrailStrategy
    {
        Off        = 0,
        FixedR     = 1,
        Structure  = 2,
        ATR        = 3,
        TimeDecay  = 4,
        Runner     = 5,
        DelayedBE  = 6,
        Cascade    = 7
    }

    /// <summary>
    /// Why a trade closed. Distinguishes mechanism (SL / TRL / TP / BE / Force)
    /// from outcome (Win/Loss/BE — captured in TradeState). See BUG G — the
    /// Pine version had `TRL0` mistakenly labeled `SL` for pre-TP1 trail-outs.
    /// </summary>
    public enum ExitReason
    {
        None = 0,
        SL,
        TP1, TP2, TP3, TP4, TP5,
        TRL0, TRL1, TRL2, TRL3, TRL4, TRL5,
        BE,
        Force,
        // Fix L5: optional trend-broken-against-active-trade close path. Only
        // emitted when TradeLifecycleManager.CloseOnTrendBroken = true AND the
        // current trend (queried via TrendStateFallback.Query()) opposes the
        // active trade's direction. Default OFF — see CloseOnTrendBroken docs.
        TrendBroken
    }

    /// <summary>
    /// Short-label mapping for the Closed Trades table "Exit" column. Most
    /// reasons are already 2-4 chars when expressed as ExitReason.ToString()
    /// (SL, TP1..TP5, TRL0..TRL5, BE, Force). The Force case is shortened to
    /// "FRC" and TrendBroken to "TBR" so they fit the 4-char column width.
    /// Master previously used `t.ExitReason.ToString()` directly — call this
    /// helper instead so new long-named reasons get a sane short label
    /// without touching DashboardRenderer.
    /// </summary>
    public static class ExitReasonLabel
    {
        public static string Short(ExitReason r)
        {
            switch (r)
            {
                case ExitReason.None:        return "";
                case ExitReason.SL:          return "SL";
                case ExitReason.TP1:         return "TP1";
                case ExitReason.TP2:         return "TP2";
                case ExitReason.TP3:         return "TP3";
                case ExitReason.TP4:         return "TP4";
                case ExitReason.TP5:         return "TP5";
                case ExitReason.TRL0:        return "TRL0";
                case ExitReason.TRL1:        return "TRL1";
                case ExitReason.TRL2:        return "TRL2";
                case ExitReason.TRL3:        return "TRL3";
                case ExitReason.TRL4:        return "TRL4";
                case ExitReason.TRL5:        return "TRL5";
                case ExitReason.BE:          return "BE";
                case ExitReason.Force:       return "FRC";
                // Fix L5: TrendBroken → TBR (3 chars, fits 4-char column).
                // "TRND" was considered but conflicts visually with TRL*; TBR
                // is unambiguous and reads as "Trend BRoken".
                case ExitReason.TrendBroken: return "TBR";
                default:                     return r.ToString();
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // ZONE INFO — input struct passed in per-bar from the zone detection layer.
    // The lifecycle layer doesn't OWN zones; it queries them by Id.
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot of a zone the lifecycle pass C uses to arm new trades and that
    /// pass A queries to detect zone invalidation. Zone detection lives in a
    /// separate component; this struct is the contract between them.
    /// </summary>
    public struct ZoneInfo
    {
        public string Id;             // unique zone ID, used to dedupe ARMED trades
        public ZoneTier Tier;
        public ZoneDirection Dir;
        public double BodyHi;
        public double BodyLo;
        public double WickHi;
        public double WickLo;
        public bool IsLong;           // true for Demand, false for Supply
        public bool Active;           // false → invalidation; PASS A drops/closes
        public DateTime BaseTime;     // zone-creation candle start time (for ZONE labels)
        public double Score;          // optional ranking score from upstream
    }

    // ────────────────────────────────────────────────────────────────────────
    // TRADE RECORD — all per-trade state. One instance per ARMED/ACTIVE/closed
    // trade. Pine used 35+ parallel `var float[]` arrays; C# can use a class.
    // ────────────────────────────────────────────────────────────────────────

    public class TradeRecord
    {
        // ── Identity ────────────────────────────────────────────────────────
        public int Id;
        public TradeState State;
        public string ZoneId;
        public ZoneTier Tier;
        public ZoneDirection Direction;
        public bool IsLong;

        // ── Timestamps / bar indexing ───────────────────────────────────────
        public DateTime ArmedTime;
        public DateTime FillTime;
        public int FillBarIndex;
        public DateTime CloseTime;

        // ── Price levels ────────────────────────────────────────────────────
        public double Entry;
        public double FillPrice;       // price at the actual fill bar (== Entry in this model)
        public double OrigSL;          // initial SL, FROZEN — used for slDist + R math
        public double CurSL;           // current trailed SL — moves over time
        public double[] TPs;           // up to 5 TP prices; unused slots = 0.0
        public bool[] TPHit;           // parallel to TPs; true once that level traded
        public int HighestTpHit;       // 0..5 — count of TPs reached so far

        // ── Risk math ───────────────────────────────────────────────────────
        public double SLDist;          // |entry − origSL|, frozen at fill
        public double R;               // signed live R based on close (refreshed each bar)
        public double DollarPnL;       // dollar P&L matching R
        public double DollarRisk;      // $ risked per trade (slDist × tickVal × cts)
        public int Contracts;
        public double AtrAtFill;       // captured at fill for ATR trail

        // ── Outcome ─────────────────────────────────────────────────────────
        public ExitReason ExitReason;

        // ── 2026-05-11: MFE / MAE tracking (Maximum Favorable / Adverse
        // Excursion in R-units). Updated per bar from UpdateLiveR; emitted
        // on close so the forward-test analytics can answer "could we have
        // tightened the stop?" / "did we leave R on the table?" (per
        // López de Prado triple-barrier exit analysis).
        // ─────────────────────────────────────────────────────────────────
        public double MfeR;             // best R seen while trade was active
        public double MaeR;             // worst R seen while trade was active
        public int    BarsSinceFill;    // bars elapsed since fill, for duration_bars at close

        // 2026-05-13: counter-trend flag set in PASS C from the trend state
        // machine at the moment of arm. true when the zone's direction
        // conflicts with the active trend (long zone in bear trend, or short
        // zone in bull trend). FLAT trend → IsCounterTrend = false (no bias).
        // Emitted on zone_armed for hub-side tracking + filtering.
        public bool   IsCounterTrend;

        // ── Partial closes (Fix L2 — Strat 5 Asymmetric Runner) ────────────
        // Records every partial-quantity close (e.g. 75% scale-out at TP1 for
        // TrailStrategy.Runner). Empty for non-Runner strategies. Each entry
        // is preserved through the trade's lifetime so reporting can attribute
        // P&L to the specific scale-out event.
        public List<PartialClose> PartialCloses = new List<PartialClose>();

        // ── Zone reference (snapshot at arm time, for proximity + label) ────
        public double ZoneTop;
        public double ZoneBottom;
        public DateTime ZoneBaseTime;

        // ────────────────────────────────────────────────────────────────────
        // COMPUTED PROPERTIES (LOCK / UNRL / FLOAT)
        // See feedback_iof_hard_exclusions.md "LOCK / UNRL / FLOAT" section.
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// LOCK — the R value that's already secured if the trailed SL hits
        /// right now. Negative until the trail has moved curSL past entry.
        /// </summary>
        public double Lock
        {
            get
            {
                if (SLDist <= 0) return 0.0;
                double diff = IsLong ? (CurSL - Entry) : (Entry - CurSL);
                return diff / SLDist;
            }
        }

        /// <summary>
        /// UNRL — mark-to-market R using the supplied current price (typically
        /// bar close). Pure function; takes the price as an arg to avoid
        /// stale state if Quantower delivers updates between bar closes.
        /// </summary>
        public double Unrealized(double currentPrice)
        {
            if (SLDist <= 0) return 0.0;
            double diff = IsLong ? (currentPrice - Entry) : (Entry - currentPrice);
            return diff / SLDist;
        }

        /// <summary>
        /// FLOAT — give-back gap. UNRL minus LOCK. Always ≥ 0 in normal flow
        /// (because curSL is never on the wrong side of price; if it were,
        /// the SL would already have triggered). Trader uses this for trail
        /// discipline awareness.
        /// </summary>
        public double Float(double currentPrice)
        {
            return Unrealized(currentPrice) - Lock;
        }

        /// <summary>True if this trade is in a closed state (Win/Loss/BE).</summary>
        public bool IsClosed
        {
            get
            {
                return State == TradeState.Win
                    || State == TradeState.Loss
                    || State == TradeState.BreakEven;
            }
        }

        /// <summary>True if Armed or Active — what PASS C calls "live."</summary>
        public bool IsLive
        {
            get { return State == TradeState.Armed || State == TradeState.Active; }
        }

        /// <summary>
        /// Returns true if this trade's source zone is still active in the
        /// supplied zone snapshot. Used by PASS A to detect invalidation.
        /// Looks the zone up by ZoneId; missing → treated as inactive.
        /// </summary>
        public bool ZoneStillActive(IEnumerable<ZoneInfo> activeZones)
        {
            if (activeZones == null || ZoneId == null) return false;
            foreach (var z in activeZones)
            {
                if (z.Id == ZoneId)
                    return z.Active;
            }
            return false;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // PARTIAL CLOSE — record of a partial-quantity exit on an active trade.
    // Fix L2: Strat 5 (Asymmetric Runner) closes 75% of contracts at TP1 and
    // lets the remaining 25% run on a swing-trailed SL. Each scale-out event
    // is appended to TradeRecord.PartialCloses for downstream reporting.
    //
    // Class (not struct) so callers can mutate fields after instantiation
    // and so it survives copy-by-value through any IEnumerable boundaries.
    // ────────────────────────────────────────────────────────────────────────

    public class PartialClose
    {
        public DateTime Time;
        public int Quantity;
        public double Price;
        public string Reason;     // e.g. "Runner-TP1"
        public double R;          // signed R-multiple at the partial price
        public double DollarPnL;  // signed $ P&L for this partial slice
    }

    // ────────────────────────────────────────────────────────────────────────
    // TRAIL STRATEGIES — Apply() dispatch stub.
    //
    // Trail dispatch: TrailStrategiesFallback.OverrideApply is set in master
    // OnInit to TradePhantomsIOF.Trail.TrailStrategies.Apply. This avoids a
    // circular reference between the Lifecycle and Trail components — the
    // real implementation lives in a different namespace, so we wire it via
    // a delegate at runtime rather than via a partial class.
    //
    // Fix L3: this used to be `public static partial class` but no second
    // partial declaration ever existed — the `partial` keyword was dead code
    // dating back to an early refactor sketch that was abandoned. Removed.
    // The class is just an Apply() stub that forwards to OverrideApply.
    // ────────────────────────────────────────────────────────────────────────

    public static class TrailStrategies
    {
        /// <summary>
        /// Compute the next curSL for a trade given its bar context. Always
        /// returns a value; caller is responsible for the "never move SL
        /// backwards" clamp. Default fallback returns trade.CurSL unchanged.
        /// </summary>
        /// <remarks>
        /// Forwards to TrailStrategiesFallback.OverrideApply when set by the
        /// host (master OnInit wires the real implementation from
        /// TradePhantomsIOF.Trail.TrailStrategies).
        /// </remarks>
        public static double Apply(
            TrailStrategy strategy,
            TradeRecord trade,
            int newHighestTp,
            double high,
            double low,
            double atr,
            Func<int, int, double> swingLowAt,
            Func<int, int, double> swingHighAt,
            double tickSize,
            bool trailFromEntry,
            int maxBarsToTp1,
            double runnerScaleOutPct,
            int barIndex)
        {
            // Dispatch to the partial implementation if present, otherwise
            // return curSL unchanged. We use a delegate field so the partial
            // file (when present) can supply the impl without requiring this
            // file to know about it.
            if (TrailStrategiesFallback.OverrideApply != null)
            {
                return TrailStrategiesFallback.OverrideApply(
                    strategy, trade, newHighestTp, high, low, atr,
                    swingLowAt, swingHighAt, tickSize, trailFromEntry,
                    maxBarsToTp1, runnerScaleOutPct, barIndex);
            }
            return trade.CurSL;
        }
    }

    // 2026-05-18: sl_updated event payload context — passed to
    // TradeLifecycleManager.SlUpdatedCallback at each cur_sl assignment.
    // Operator-approved per docs/REPLAY_DB_REBUILD_PLAN.md §3.2.a.
    //
    // The host (TradePhantoms_IOF_v2) consumes this and forwards into
    // EmitObserverEvent("sl_updated", ...). Class (not struct) so the host
    // can read it past the callback frame if it queues for async emission.
    public class SlUpdateContext
    {
        public TradeRecord Trade;
        public double PrevSL;
        public double NewSL;
        public string Trigger;         // see TradeLifecycleManager.EmitSlUpdated
        public DateTime BarTime;       // bar timestamp (UTC if caller passes UTC)
        public int BarIndex;           // monotonic bar index
        public TrailStrategy Strategy; // active strategy at moment of update
    }

    /// <summary>
    /// Internal hook the TrailStrategies component uses to register its real
    /// Apply implementation. See above; lets us keep this file self-contained
    /// while still allowing the dedicated trail file to take over.
    /// </summary>
    internal static class TrailStrategiesFallback
    {
        public static Func<
            TrailStrategy, TradeRecord, int, double, double, double,
            Func<int, int, double>, Func<int, int, double>,
            double, bool, int, double, int, double> OverrideApply = null;
    }

    // ────────────────────────────────────────────────────────────────────────
    // TREND STATE HOOK — Fix L5.
    //
    // The lifecycle layer optionally needs to know the current TrendState
    // (Flat/Bull/Bear) to honor the CloseOnTrendBroken setting on
    // TradeLifecycleManager. We avoid a hard dependency on TrendStateMachine
    // (built by another agent) by exposing a delegate hook the master file
    // wires in OnInit:
    //
    //   TradePhantomsIOF.Lifecycle.TrendStateFallback.GetCurrentTrendState =
    //       () => (int)trendStateMachine.CurrentState;
    //
    // The int contract is fixed: 0 = Flat, 1 = Bull, 2 = Bear. This matches
    // the TrendState enum order in TrendStateMachine.cs. If the hook isn't
    // wired (or throws), Query() returns 0 (Flat) and the trend-broken close
    // path becomes a no-op — so unwiring is the safe default.
    //
    // Mirrors the TrailStrategiesFallback pattern above (delegate-based
    // dispatch instead of partial-class or interface, to keep this file
    // self-contained).
    // ────────────────────────────────────────────────────────────────────────

    public static class TrendStateFallback
    {
        /// <summary>
        /// Returns 0=Flat, 1=Bull, 2=Bear. Set by master OnInit; null when
        /// the trend state machine isn't wired (or feature disabled).
        /// </summary>
        public static Func<int> GetCurrentTrendState;

        /// <summary>
        /// Safe accessor — calls the wired delegate inside try/catch. Any
        /// exception (or unwired delegate) collapses to Flat (0), which is
        /// the conservative no-op for the CloseOnTrendBroken check.
        /// </summary>
        public static int Query()
        {
            if (GetCurrentTrendState != null)
            {
                try { return GetCurrentTrendState(); } catch { return 0; }
            }
            return 0;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // TRADE LIFECYCLE MANAGER — the four-pass loop.
    // ════════════════════════════════════════════════════════════════════════

    public class TradeLifecycleManager
    {
        // ── Trade store ─────────────────────────────────────────────────────
        public List<TradeRecord> Trades = new List<TradeRecord>();

        /// <summary>Monotonic ID counter. First trade gets ID = 1.</summary>
        public int NextTradeId = 1;

        // ── Configuration ──────────────────────────────────────────────────
        public TrailStrategy ActiveStrategy = TrailStrategy.Cascade; // user default
        public bool UseSequentialGate = true;                        // one ACTIVE at a time
        public int MaxRetainedClosed = 50;                           // FIFO cap
        public int LifecycleHistoryBars = 1000;                      // how far back PASS A runs
        public double ArmProx = 0.5;                                 // fraction of zoneHeight
        public int StopBufferTicks = 2;                              // buffer past wick for SL
        public bool TrailFromEntry = false;                          // strats 2,3 toggle
        public int MaxBarsToTp1 = 50;                                // strat 4
        // Fix L4: RunnerScaleOutPct now actually used by the lifecycle layer —
        // PASS A performs the 75% partial close at TP1 for TrailStrategy.Runner
        // (see Fix L2 in PASS A). The trail-strategy module no longer owns the
        // partial-close behavior; it only moves curSL → entry on TP1. This is
        // the dual implementation of the comment in TrailStrategies.cs ApplyRunner.
        public double RunnerScaleOutPct = 0.75;                      // strat 5 (Fix L2/L4)
        public int NumTPs = 3;                                       // default 3 (max 5)
        public double TpMult = 1.0;                                  // TP step as zoneHeight ×

        /// <summary>
        /// Fix L5 — optional trend-broken-against-active-trade close path.
        /// When true, PASS A force-closes any ACTIVE trade whose direction
        /// conflicts with the current TrendState (queried via
        /// TrendStateFallback.Query()). Default OFF (conservative): leaves
        /// the discretionary call to the trader. Per the TP + PDF doctrine
        /// "trend is broken when the control point is broken" — but this
        /// manager remains passive unless the user explicitly opts in.
        /// </summary>
        public bool CloseOnTrendBroken = false;

        /// <summary>
        /// 2026-05-13: when false, PASS C will NOT arm a zone whose direction
        /// conflicts with the active trend (long zone in bear trend, short
        /// zone in bull trend). FLAT trend permits both directions either way.
        /// Default true (preserves legacy behavior where every eligible zone
        /// is arm-considered regardless of trend).
        /// </summary>
        public bool AllowCounterTrend = true;

        /// <summary>
        /// 2026-05-13: when true, PASS C arms at most ONE demand zone (the
        /// closest by distance from price) AND ONE supply zone (also closest).
        /// Default true — user requested this as the operational arming rule
        /// to avoid arming a stack of overlapping zones simultaneously. Set
        /// false to restore the legacy "arm every eligible zone" behavior.
        /// </summary>
        public bool OnlyArmClosestPerDirection = true;

        /// <summary>
        /// Currently selected OF entry depth (0/25/50/75%). PASS C reads this
        /// to compute entry price for newly armed trades. Defaults to OF 0%
        /// (the lip), matching the Pine v1.4 default behavior. Wired through
        /// TradePhantomsIOF.EntryTPMath.ComputeEntry — see Fix L1.
        /// </summary>
        public OFEntryLevel ActiveOFEntry = OFEntryLevel.Of0;

        // 2026-05-18: sl_updated event hook for replay-DB rebuild.
        // Operator-approved per docs/REPLAY_DB_REBUILD_PLAN.md §3.2.a.
        //
        // The host (TradePhantoms_IOF_v2) wires SlUpdatedCallback in OnInit
        // to forward into EmitObserverEvent("sl_updated", payload). The
        // delegate signature carries everything the replay DB needs to
        // reconstruct trail state — prev/new SL, trigger taxonomy string,
        // the trade reference, and the bar-loop barTime/barIndex.
        //
        // Fired in PASS A at each `trade.CurSL = ...` assignment site (3
        // sites: long clamp, short clamp, BE-on-TP1 promote). Skipped when
        // newSL == prevSL (the clamp is a no-op) so the wire isn't flooded
        // with idempotent updates.
        //
        // Trigger strings (see EmitSlUpdated below):
        //   trail_<strategy>            — long/short clamp accepted a forward move
        //   be_on_tp1_<strategy>        — BUG C BE-on-TP1 promote fired
        // (full operator-spec taxonomy — cascade_to_entry, cascade_to_tp1,
        //  be_only_to_entry, runner_swing_trail, etc. — is derived from
        //  the strategy enum + HighestTpHit on the consumer side.)
        public Action<SlUpdateContext> SlUpdatedCallback;

        // 2026-05-18: helper to emit sl_updated. Called from PASS A at each
        // assignment site. Captures prev/new + assembles trigger string.
        // No-op when the callback is unwired or when prevSL == newSL.
        private void EmitSlUpdated(
            TradeRecord trade,
            double prevSL,
            double newSL,
            string trigger,
            DateTime barTime,
            int barIndex)
        {
            if (SlUpdatedCallback == null) return;
            if (prevSL == newSL) return;
            try
            {
                SlUpdatedCallback(new SlUpdateContext
                {
                    Trade = trade,
                    PrevSL = prevSL,
                    NewSL = newSL,
                    Trigger = trigger,
                    BarTime = barTime,
                    BarIndex = barIndex,
                    Strategy = this.ActiveStrategy,
                });
            }
            catch { /* never throw from the lifecycle */ }
        }

        // 2026-05-18: trigger-label builders for sl_updated.
        // Maps (strategy, newHighest) → operator-spec trigger string. Used by
        // the trail-clamp assignment sites. The consumer can read the strategy
        // enum out of payload.strategy and the newHighest out of payload.
        // highest_tp_hit if it needs to re-derive; the label is provided as a
        // convenience for replay-DB row labeling.
        private static string BuildTrailTrigger(TrailStrategy strategy, int newHighest)
        {
            switch (strategy)
            {
                case TrailStrategy.Off:
                    return "trail_off_noop";  // shouldn't fire (clamp gates Off)
                case TrailStrategy.FixedR:
                    return newHighest >= 1 ? "fixed_r_plus_1r" : "fixed_r_pre_tp1";
                case TrailStrategy.Structure:
                    return newHighest >= 1 ? "structure_be_then_swing" : "structure_pre_tp1";
                case TrailStrategy.ATR:
                    return newHighest >= 1 ? "atr_be_then_atr_trail" : "atr_pre_tp1";
                case TrailStrategy.TimeDecay:
                    return newHighest >= 1 ? "time_decay_post_tp1" : "time_decay_decay_back";
                case TrailStrategy.Runner:
                    return newHighest >= 1 ? "runner_swing_trail" : "runner_pre_tp1";
                case TrailStrategy.DelayedBE:
                    return newHighest >= 2 ? "delayed_be_to_entry" : "delayed_be_pre_tp2";
                case TrailStrategy.Cascade:
                    if (newHighest <= 0) return "cascade_pre_tp1";
                    if (newHighest == 1) return "cascade_to_entry";
                    if (newHighest == 2) return "cascade_to_tp1";
                    if (newHighest == 3) return "cascade_to_tp2";
                    return "cascade_to_tp" + (newHighest - 1).ToString();
                default:
                    return "trail_" + strategy.ToString().ToLowerInvariant();
            }
        }

        // 2026-05-18: trigger label for the BUG-C BE-on-TP1 promote site.
        // Per-strategy because BeOnly + Aggressive are wired through the
        // same site but have distinct operator-spec labels.
        private static string BuildBeOnTp1Trigger(TrailStrategy strategy)
        {
            switch (strategy)
            {
                case TrailStrategy.FixedR:    return "fixed_r_be";
                case TrailStrategy.Structure: return "structure_be_then_swing";
                case TrailStrategy.ATR:       return "atr_be_then_atr_trail";
                case TrailStrategy.TimeDecay: return "time_decay_to_entry";
                case TrailStrategy.Runner:    return "runner_to_entry";
                case TrailStrategy.Cascade:   return "cascade_to_entry";
                default:                      return "be_on_tp1_" + strategy.ToString().ToLowerInvariant();
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // PUBLIC PER-BAR ENTRYPOINT
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Run the four-pass lifecycle loop for the supplied confirmed bar.
        /// Caller invokes this once per bar close. Order is fixed: A → B → C → D.
        /// Merging passes was tried in Pine and produced order-of-operations
        /// bugs (see audit "Why separate PASS A/B/C/D"); don't refactor.
        /// </summary>
        /// <param name="high">Bar high.</param>
        /// <param name="low">Bar low.</param>
        /// <param name="close">Bar close — used for live R + proximity.</param>
        /// <param name="atr">ATR snapshot for trail strategies that use it.</param>
        /// <param name="barTime">Bar start time (used for FillTime/CloseTime).</param>
        /// <param name="barIndex">Monotonic bar index (ARMED → ACTIVE recording).</param>
        /// <param name="activeZones">Current snapshot of all zones (active or not).</param>
        /// <param name="swingLowAt">Lookup: (barIndex, lookback) → swing low price (NaN if none).</param>
        /// <param name="swingHighAt">Lookup: (barIndex, lookback) → swing high price (NaN if none).</param>
        /// <param name="tickSize">Symbol tick size (e.g., 0.25 for ES).</param>
        /// <param name="pointValue">$ per point (e.g., $50 for ES). With tickSize = $/tick.</param>
        /// <param name="contracts">Default contracts for new arms (PASS C may override based on risk math; see comment in PASS C).</param>
        /// <param name="dollarRisk">Per-trade $ risk used for sizing & dollar P&L.</param>
        public void RunPerBar(
            double high,
            double low,
            double close,
            double atr,
            DateTime barTime,
            int barIndex,
            IEnumerable<ZoneInfo> activeZones,
            Func<int, int, double> swingLowAt,
            Func<int, int, double> swingHighAt,
            double tickSize,
            double pointValue,
            int contracts,
            double dollarRisk)
        {
            // Materialize zones once — callers may pass a single-pass enumerator.
            var zoneSnapshot = activeZones != null
                ? new List<ZoneInfo>(activeZones)
                : new List<ZoneInfo>();

            PassA_AdvanceState(high, low, close, atr, barTime, barIndex,
                               zoneSnapshot, swingLowAt, swingHighAt,
                               tickSize, pointValue);
            PassB_DropSentinels();
            PassC_ArmNew(close, barTime, zoneSnapshot, tickSize, pointValue,
                         contracts, dollarRisk);
            PassD_FifoCapClosed();
        }

        // ────────────────────────────────────────────────────────────────────
        // PASS A — Advance state on each existing trade
        // New in this version: optional CloseOnTrendBroken check. When enabled,
        // active trades whose direction conflicts with the current trend state
        // are force-closed at the trailed SL (matches Bug D pattern).
        // Default OFF — leave the discretionary call to the trader unless
        // they specifically want auto-management on trend reversal.
        //
        // Order of subchecks per trade is critical (each subchecked bug
        // surfaced as a real production issue in the Pine version):
        //
        //   1. ARMED branch:
        //      (a) BUG E — proximity-drift drop FIRST (before entry check)
        //      (b) zone-invalidation drop
        //      (c) entry-reached → ACTIVE (gated by sequential gate)
        //   2. BUG A — re-read state immediately so a same-bar fill+SL
        //      is caught on the SAME bar (audit bug #15)
        //   3. ACTIVE branch:
        //      (a) detect TP hits (do NOT yet write HighestTpHit — BUG B)
        //      (b) all TPs hit → close as Win at last TP price
        //      (c) zone invalidation → force-close at curSL (BUG D)
        //      (c.5) BUG H — optional trend-broken close (new; gated)
        //      (d) SL hit → close (compute exit reason via BUG G logic)
        //      (e) apply trail strategy (reads OLD HighestTpHit so
        //          tpJustHit detection works)
        //      (f) clamp curSL to never go backwards
        //      (g) BUG C — directional BE-on-TP1 (or TP2 for DelayedBE)
        //      (h) NOW write HighestTpHit (BUG B fix)
        //      (i) refresh live R / dollar P&L
        // ────────────────────────────────────────────────────────────────────

        private void PassA_AdvanceState(
            double high,
            double low,
            double close,
            double atr,
            DateTime barTime,
            int barIndex,
            List<ZoneInfo> zones,
            Func<int, int, double> swingLowAt,
            Func<int, int, double> swingHighAt,
            double tickSize,
            double pointValue)
        {
            // Determine if any ACTIVE exists (sequential gate). Computed
            // once here; if a fill happens during PASS A we manually update
            // the flag so subsequent ARMED trades on the SAME bar don't
            // also fill (which would violate the gate). Pine had a known
            // 1-bar lag here — we close it by tracking anyActive locally.
            bool anyActive = false;
            for (int i = 0; i < Trades.Count; i++)
            {
                if (Trades[i].State == TradeState.Active) { anyActive = true; break; }
            }

            for (int i = 0; i < Trades.Count; i++)
            {
                var trade = Trades[i];
                var state = trade.State;

                // Skip closed/sentinel trades — nothing to advance.
                if (state == TradeState.Sentinel || trade.IsClosed)
                    continue;

                // ── ARMED branch ────────────────────────────────────────────
                if (state == TradeState.Armed)
                {
                    // BUG E (audit #16): proximity-drift drop. Must fire BEFORE
                    // the entry-reached check, otherwise stale-far ARMED trades
                    // can cling forever if the wick happens to revisit briefly.
                    double zH = Math.Abs(trade.ZoneTop - trade.ZoneBottom);
                    double dist = Math.Abs(close - trade.Entry);
                    if (zH > 0 && dist > ArmProx * zH)
                    {
                        trade.State = TradeState.Sentinel;
                        continue;
                    }

                    // Zone-invalidation drop (the original ARMED→sentinel path).
                    if (!trade.ZoneStillActive(zones))
                    {
                        trade.State = TradeState.Sentinel;
                        continue;
                    }

                    // ARMED → ACTIVE — entry price reached on this bar?
                    bool reached = trade.IsLong
                        ? (low <= trade.Entry)
                        : (high >= trade.Entry);

                    if (reached)
                    {
                        // Sequential gate: only one ACTIVE at a time.
                        if (UseSequentialGate && anyActive)
                        {
                            // Stay ARMED — do NOT promote. Pine just left it
                            // ARMED; we do the same. Could optionally drop to
                            // sentinel for "missed opportunity," but the
                            // documented behavior is to wait for the active
                            // trade to clear, then re-check next bar.
                        }
                        else
                        {
                            trade.State = TradeState.Active;
                            trade.FillTime = barTime;
                            trade.FillBarIndex = barIndex;
                            trade.FillPrice = trade.Entry;
                            trade.AtrAtFill = atr;
                            anyActive = true;  // close the 1-bar gate gap
                        }
                    }

                    // BUG A (audit #15): re-read state. If ARMED→ACTIVE just
                    // happened, the next branch (SL/TP check) MUST fire on
                    // the SAME bar so a fill+SL on one bar isn't lost. Without
                    // this re-read, the cached `state` would still say ARMED
                    // and we'd skip the SL/TP block until the next bar.
                    state = trade.State;
                }

                // ── ACTIVE branch ───────────────────────────────────────────
                // 2026-05-12 Phase-0 audit reorder:
                //   • SL check now fires FIRST (Inv 4 — conservative within-bar
                //     SL+TP collision rule. Was buried after the all-TPs-hit
                //     close, which let a wide-range bar that crossed entry→
                //     TP1→TP2→TP3→SL claim a +3R win instead of the honest
                //     -1R / +1R outcome).
                //   • Zone-invalidation (FRC) and trend-broken (TBR) now exit
                //     at bar.Close, not trade.CurSL (Invs 6 + 7 — scaffold
                //     doctrine: "exit price = where the market was when the
                //     auto-close fired"). Pine's OrigSL phantom-loss bug
                //     (original BUG D) is still avoided.
                //   • Runner TP1 scale-out (Inv 3) gate widened from
                //     `newHighest == 1` to `prev==0 && new>=1`, so a single
                //     bar covering TP1+TP2 still triggers the 75% partial.
                if (state == TradeState.Active)
                {
                    // 2026-05-11: bump bars-since-fill once per bar tick. Used
                    // for `duration_bars` in the closed-trade payload and for
                    // time-decay strategies (Strat 4).
                    trade.BarsSinceFill++;

                    // (a) SL hit FIRST (Inv 4 — conservative). Uses the trade's
                    // STORED HighestTpHit (end-of-prev-bar value), not a fresh
                    // DetectTpHits result — otherwise a bar that wicked through
                    // a TP would relabel an SL exit as TRL{n}. The trail step
                    // for this bar has not run yet, so trade.CurSL is the value
                    // locked in at end-of-prev-bar; that is the conservative
                    // stop price the SL order would actually rest at intra-bar.
                    bool slHit = trade.IsLong
                        ? (low <= trade.CurSL)
                        : (high >= trade.CurSL);
                    if (slHit)
                    {
                        ExitReason reason = ComputeExitReason(trade, trade.HighestTpHit);
                        CloseTrade(trade, trade.CurSL, barTime, reason,
                                   tickSize, pointValue);
                        if (trade.State != TradeState.Active) anyActive = AnyActiveExcept(trade);
                        continue;
                    }

                    // (b) Zone invalidation → close at bar.Close (Inv 6).
                    // Was trade.CurSL in v2.0.5 (preserved the Pine BUG D
                    // fix against phantom -1R losses). Switched to bar.Close
                    // for the v2.1.0 audit cohort — closing at the actual
                    // market price on the invalidation bar is more honest
                    // for both in-profit and in-loss scenarios. The phantom-
                    // loss case is still avoided because bar.Close on the
                    // invalidating bar is by definition past the far wick,
                    // not behind the original SL.
                    if (!trade.ZoneStillActive(zones))
                    {
                        CloseTrade(trade, close, barTime, ExitReason.Force,
                                   tickSize, pointValue);
                        if (trade.State != TradeState.Active) anyActive = AnyActiveExcept(trade);
                        continue;
                    }

                    // (c) Trend-broken close (Fix L5 — opt-in via
                    // CloseOnTrendBroken). Same Inv 7 doctrine: close at
                    // bar.Close, not trade.CurSL.
                    if (this.CloseOnTrendBroken)
                    {
                        int trendInt = TrendStateFallback.Query();
                        bool trendIsBull = trendInt == 1;
                        bool trendIsBear = trendInt == 2;
                        bool trendBrokenAgainstUs =
                            (trade.IsLong && trendIsBear)
                            || (!trade.IsLong && trendIsBull);
                        if (trendBrokenAgainstUs)
                        {
                            CloseTrade(trade, close, barTime,
                                       ExitReason.TrendBroken,
                                       tickSize, pointValue);
                            if (trade.State != TradeState.Active) anyActive = AnyActiveExcept(trade);
                            continue;
                        }
                    }

                    // (d) Now safe to detect TP hits — SL has been ruled out
                    // for this bar (any SL-touch already short-circuited via
                    // the SL-first check above). BUG B still applies: we
                    // compute newHighest but DON'T write trade.HighestTpHit
                    // until after the trail strategy has consumed the OLD
                    // value via tpJustHit detection (line below + step (i)).
                    int newHighest = DetectTpHits(trade, high, low);

                    // (e) All TPs hit? Close as Win at the last TP price.
                    int n = CountValidTPs(trade);
                    if (n > 0 && newHighest >= n)
                    {
                        double lastTp = trade.TPs[n - 1];
                        trade.HighestTpHit = newHighest;
                        CloseTrade(trade, lastTp, barTime, MapTpExitReason(n),
                                   tickSize, pointValue);
                        if (trade.State != TradeState.Active) anyActive = AnyActiveExcept(trade);
                        continue;
                    }

                    // (f) Fix L2 — Strat 5 Asymmetric Runner partial close.
                    // 2026-05-12 (Inv 3 fix): gate widened from
                    // `newHighest == 1` to `prev==0 && new>=1`. The old gate
                    // skipped the scale-out when a single bar covered TP1+TP2
                    // (newHighest jumped 0→2). The new gate fires whenever
                    // TP1 is first crossed, regardless of how far past TP1
                    // the bar reached. The partial close still uses TPs[0]
                    // (the TP1 price) as the slice price — the bar may have
                    // gone higher but the discipline is "scale 75% at TP1."
                    bool tpJustHit = newHighest > trade.HighestTpHit;
                    bool tp1FirstHit = trade.HighestTpHit == 0 && newHighest >= 1;
                    if (tpJustHit
                        && tp1FirstHit
                        && this.ActiveStrategy == TrailStrategy.Runner
                        && trade.Contracts > 1
                        && this.RunnerScaleOutPct > 0.0
                        && this.RunnerScaleOutPct < 1.0
                        && trade.TPs != null
                        && trade.TPs.Length > 0
                        && trade.TPs[0] != 0.0)
                    {
                        // Round to nearest int. Cap at Contracts - 1 so a
                        // runner contract always survives — never fully
                        // close, that would defeat the strategy.
                        int closeQty = (int)Math.Round(
                            trade.Contracts * this.RunnerScaleOutPct);
                        if (closeQty < 1) closeQty = 1;
                        if (closeQty > trade.Contracts - 1)
                            closeQty = trade.Contracts - 1;

                        if (closeQty > 0)
                        {
                            double tp1Price = trade.TPs[0];
                            // R at the TP1 price — signed using direction.
                            double diffPartial = trade.IsLong
                                ? (tp1Price - trade.Entry)
                                : (trade.Entry - tp1Price);
                            double partialR = trade.SLDist > 0
                                ? diffPartial / trade.SLDist
                                : 0.0;

                            // Per-contract dollar risk so the slice's $ P&L
                            // is proportional to the closed quantity, not
                            // the whole trade's risk envelope.
                            double riskPerCt = trade.Contracts > 0
                                ? trade.DollarRisk / trade.Contracts
                                : 0.0;
                            double partialDollar = partialR * riskPerCt * closeQty;

                            trade.PartialCloses.Add(new PartialClose
                            {
                                Time = barTime,
                                Quantity = closeQty,
                                Price = tp1Price,
                                Reason = "Runner-TP1",
                                R = partialR,
                                DollarPnL = partialDollar
                            });

                            // Downsize remaining contracts and proportionally
                            // shrink DollarRisk so subsequent R/$ math on
                            // the runner reflects only the surviving size.
                            int remaining = trade.Contracts - closeQty;
                            if (trade.Contracts > 0)
                            {
                                trade.DollarRisk =
                                    trade.DollarRisk
                                    * ((double)remaining / trade.Contracts);
                            }
                            trade.Contracts = remaining;
                        }
                    }

                    // (e) Apply trail strategy. Receives `newHighest` so the
                    // strategy can see "tpJustHit" by comparing against the
                    // trade's stored HighestTpHit (still the OLD value — BUG B
                    // ensures we haven't written it yet).
                    double newSL = TrailStrategies.Apply(
                        ActiveStrategy,
                        trade,
                        newHighest,
                        high, low,
                        atr,
                        swingLowAt, swingHighAt,
                        tickSize,
                        TrailFromEntry,
                        MaxBarsToTp1,
                        RunnerScaleOutPct,
                        barIndex);

                    // (f) Clamp: never move SL backwards. Long → SL only
                    // moves up; short → SL only moves down. The trail
                    // strategy SHOULD respect this internally, but we clamp
                    // here as a defense in depth.
                    //
                    // 2026-05-18: sl_updated event added for replay-DB rebuild.
                    // Operator-approved per docs/REPLAY_DB_REBUILD_PLAN.md.
                    // Trigger label encodes (strategy + post-TP context) so
                    // the replay DB can taxonomize trail moves (cascade_to_entry,
                    // cascade_to_tp1, runner_swing_trail, etc.).
                    if (trade.IsLong)
                    {
                        if (newSL > trade.CurSL)
                        {
                            double _prevSL = trade.CurSL;
                            trade.CurSL = newSL;
                            EmitSlUpdated(trade, _prevSL, newSL,
                                BuildTrailTrigger(ActiveStrategy, newHighest),
                                barTime, barIndex);
                        }
                    }
                    else
                    {
                        if (newSL < trade.CurSL)
                        {
                            double _prevSL = trade.CurSL;
                            trade.CurSL = newSL;
                            EmitSlUpdated(trade, _prevSL, newSL,
                                BuildTrailTrigger(ActiveStrategy, newHighest),
                                barTime, barIndex);
                        }
                    }

                    // (g) BUG C (audit #14): BE-on-TP1 must be a directional
                    // "still in loss territory" check, NOT `curSL == origSL`.
                    // The Pine version had `if curSL == origSL` which broke
                    // when TrailFromEntry = ON (the trail had nudged curSL
                    // before TP1 hit, so the BE jump skipped). Fix is to use:
                    //   long  : curSL < entry  → still working against us
                    //   short : curSL > entry  → still working against us
                    // Strats that respect BE-on-TP1: Cascade (7), FixedR (1),
                    // Structure (2), ATR (3), TimeDecay (4), Runner (5). The
                    // Off (0) strategy never moves SL. DelayedBE (6) skips
                    // BE on TP1 entirely (BE only on TP2).
                    if (newHighest >= 1)
                    {
                        bool stillInLoss = trade.IsLong
                            ? (trade.CurSL < trade.Entry)
                            : (trade.CurSL > trade.Entry);
                        if (stillInLoss)
                        {
                            bool strategyAllowsBeOnTp1 =
                                ActiveStrategy != TrailStrategy.Off
                                && !(ActiveStrategy == TrailStrategy.DelayedBE
                                     && newHighest == 1);
                            if (strategyAllowsBeOnTp1)
                            {
                                // Move SL to entry. This still respects the
                                // "no backwards" rule because we already
                                // determined curSL is on the wrong side of
                                // entry (in loss territory) — moving to
                                // entry is forward.
                                //
                                // 2026-05-18: sl_updated event added for
                                // replay-DB rebuild. Operator-approved per
                                // docs/REPLAY_DB_REBUILD_PLAN.md.
                                double _prevSL = trade.CurSL;
                                trade.CurSL = trade.Entry;
                                EmitSlUpdated(trade, _prevSL, trade.Entry,
                                    BuildBeOnTp1Trigger(ActiveStrategy),
                                    barTime, barIndex);
                            }
                        }
                    }

                    // (h) NOW write HighestTpHit (BUG B fix). Trail logic has
                    // already consumed the OLD value to detect tpJustHit.
                    trade.HighestTpHit = newHighest;

                    // (i) Refresh live R + dollar P&L for the trade strip
                    // (uses bar close — mark-to-market value).
                    UpdateLiveR(trade, close);

                    // (j) 2026-05-12 (Inv 5 fix): MFE / MAE excursion update
                    // uses bar wick extremes (true peak excursion per
                    // López de Prado triple-barrier), not bar close.
                    UpdateBarExcursion(trade, high, low);
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // PASS B — Drop sentinels.
        // Iterate Trades backwards so List.RemoveAt doesn't shift indices
        // we haven't yet visited.
        // ────────────────────────────────────────────────────────────────────

        private void PassB_DropSentinels()
        {
            for (int i = Trades.Count - 1; i >= 0; i--)
            {
                if (Trades[i].State == TradeState.Sentinel)
                    Trades.RemoveAt(i);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // PASS C — Arm new trades.
        // For each active zone with NO existing live trade for that zone,
        // if price is within ArmProx × zoneHeight of the entry, push a new
        // ARMED record. Sizing math (slDist, contracts, dollar risk) is done
        // here once at arm-time.
        // ────────────────────────────────────────────────────────────────────

        // Internal struct used during PASS C candidate collection.
        private struct ArmCandidate
        {
            public ZoneInfo Zone;
            public double Entry;
            public double Sl;
            public double ZH;
            public double Dist;
            public double ZTop;
            public double ZBot;
            public bool IsCounterTrend;
        }

        private void PassC_ArmNew(
            double close,
            DateTime barTime,
            List<ZoneInfo> zones,
            double tickSize,
            double pointValue,
            int contracts,
            double dollarRisk)
        {
            if (zones == null || zones.Count == 0) return;

            // 2026-05-13: query trend state once for the counter-trend gate
            // + IsCounterTrend tracking. 0=Flat, 1=Bull, 2=Bear (matches
            // TrendStateFallback's int contract). FLAT trend produces
            // IsCounterTrend=false for any zone (no trend bias to violate).
            int trendInt = TrendStateFallback.Query();
            bool trendBull = trendInt == 1;
            bool trendBear = trendInt == 2;

            // Phase 1 — collect every zone that passes the existing gates
            // (proximity, price-side, dedupe), and tag with counter-trend.
            var candidates = new List<ArmCandidate>();
            foreach (var z in zones)
            {
                if (!z.Active) continue;

                // Compute entry / SL using the zone geometry. Fix L1: entry
                // now honors ActiveOFEntry (OF 0/25/50/75%) by delegating to
                // the shared TradePhantomsIOF.EntryTPMath helper instead of
                // hard-coding OF 0%. SL still sits past the far wick + buffer.
                double entry = TradePhantomsIOF.EntryTPMath.ComputeEntry(
                    this.ActiveOFEntry,
                    z.IsLong,
                    z.BodyHi, z.BodyLo, z.WickHi, z.WickLo);
                double sl = ComputeSL(z, tickSize);
                double zTop = z.IsLong ? z.BodyHi : z.WickHi;
                double zBot = z.IsLong ? z.WickLo : z.BodyLo;
                double zH = Math.Abs(zTop - zBot);
                if (zH <= 0) continue;

                // Proximity gate: arm only when price is close.
                double dist = Math.Abs(close - entry);
                if (dist > ArmProx * zH) continue;

                // Price-side check: don't arm a long if we've already
                // blasted through the entry to the upside (price > entry
                // for a long means we're past entry — wait for retrace).
                bool priceOk = z.IsLong ? (close > entry) : (close < entry);
                if (!priceOk) continue;

                // Dedupe: skip if a live trade for this zone already exists.
                bool already = false;
                for (int t = 0; t < Trades.Count; t++)
                {
                    var tr = Trades[t];
                    if (tr.IsLive && tr.ZoneId == z.Id)
                    {
                        already = true;
                        break;
                    }
                }
                if (already) continue;

                // Counter-trend tag at the moment of candidacy. Long zone
                // conflicts with bear; short zone conflicts with bull.
                bool isCounter =
                    ( z.IsLong && trendBear) ||
                    (!z.IsLong && trendBull);

                // 2026-05-13: AllowCounterTrend gate. When false, skip
                // counter-trend zones outright (no arm).
                if (!this.AllowCounterTrend && isCounter) continue;

                candidates.Add(new ArmCandidate
                {
                    Zone = z, Entry = entry, Sl = sl,
                    ZH = zH, Dist = dist,
                    ZTop = zTop, ZBot = zBot,
                    IsCounterTrend = isCounter,
                });
            }
            if (candidates.Count == 0) return;

            // Phase 2 — if OnlyArmClosestPerDirection, keep ONLY the closest
            // demand and the closest supply by distance from price. User-
            // requested operational rule to avoid arming a stack of
            // overlapping zones simultaneously.
            if (this.OnlyArmClosestPerDirection)
            {
                ArmCandidate? bestLong = null;
                ArmCandidate? bestShort = null;
                foreach (var c in candidates)
                {
                    if (c.Zone.IsLong)
                    {
                        if (!bestLong.HasValue || c.Dist < bestLong.Value.Dist)
                            bestLong = c;
                    }
                    else
                    {
                        if (!bestShort.HasValue || c.Dist < bestShort.Value.Dist)
                            bestShort = c;
                    }
                }
                candidates.Clear();
                if (bestLong.HasValue)  candidates.Add(bestLong.Value);
                if (bestShort.HasValue) candidates.Add(bestShort.Value);
            }

            // Phase 3 — size + create TradeRecord for each survivor.
            foreach (var c in candidates)
            {
                // Sizing math: contracts = floor(dollarRisk / (slTicks × tickVal)).
                // If caller passed a fixed `contracts`, we honor it AS A CAP and
                // recompute risk against the actual SL distance (so the dollar
                // risk reflects what'll actually be on the line). Pine version
                // had a `maxContracts` input; we expose `contracts` as that cap.
                double slDist = Math.Abs(c.Entry - c.Sl);
                double tickVal = pointValue * tickSize;
                int cts = 0;
                double actualRisk = 0.0;
                if (tickSize > 0 && tickVal > 0 && slDist > 0)
                {
                    double slTicks = slDist / tickSize;
                    int sized = (int)Math.Floor(dollarRisk / (slTicks * tickVal));
                    cts = Math.Min(sized, Math.Max(contracts, 1));
                    if (cts < 0) cts = 0;
                    actualRisk = slTicks * tickVal * cts;
                }
                if (cts <= 0) continue;

                // Build TPs. Default 3 TPs at 1×, 2×, 3× zoneHeight past
                // entry. Up to 5 supported by the array shape.
                int n = Math.Max(1, Math.Min(5, NumTPs));
                var tps = new double[5];
                var tpHit = new bool[5];
                for (int k = 0; k < n; k++)
                {
                    double mult = TpMult * (k + 1);
                    tps[k] = c.Zone.IsLong ? c.Entry + c.ZH * mult
                                           : c.Entry - c.ZH * mult;
                }

                var trade = new TradeRecord
                {
                    Id = NextTradeId++,
                    State = TradeState.Armed,
                    ZoneId = c.Zone.Id,
                    Tier = c.Zone.Tier,
                    Direction = c.Zone.Dir,
                    IsLong = c.Zone.IsLong,
                    ArmedTime = barTime,
                    Entry = c.Entry,
                    OrigSL = c.Sl,
                    CurSL = c.Sl,
                    SLDist = slDist,
                    TPs = tps,
                    TPHit = tpHit,
                    HighestTpHit = 0,
                    Contracts = cts,
                    DollarRisk = actualRisk,
                    R = 0.0,
                    DollarPnL = 0.0,
                    AtrAtFill = 0.0,
                    ExitReason = ExitReason.None,
                    ZoneTop = c.ZTop,
                    ZoneBottom = c.ZBot,
                    ZoneBaseTime = c.Zone.BaseTime,
                    IsCounterTrend = c.IsCounterTrend,
                };
                Trades.Add(trade);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // PASS D — FIFO cap on closed trades.
        // While Trades.Count > MaxRetainedClosed (50 default), evict the
        // OLDEST CLOSED trade (by CloseTime). Live trades are never evicted.
        //
        // Note: the cap is on TOTAL trades, but only closed ones are eligible
        // for eviction. If the user has 100 ARMED trades they'll all stay.
        // (In practice ARMED count is tiny because of the proximity drop.)
        // ────────────────────────────────────────────────────────────────────

        private void PassD_FifoCapClosed()
        {
            // Quick exit if under cap
            if (Trades.Count <= MaxRetainedClosed) return;

            // Loop until we're under the cap, or there are no more closed
            // trades to evict (degenerate case).
            while (Trades.Count > MaxRetainedClosed)
            {
                int oldestIdx = -1;
                DateTime oldestTime = DateTime.MaxValue;
                for (int i = 0; i < Trades.Count; i++)
                {
                    var tr = Trades[i];
                    if (!tr.IsClosed) continue;
                    if (tr.CloseTime < oldestTime)
                    {
                        oldestTime = tr.CloseTime;
                        oldestIdx = i;
                    }
                }
                if (oldestIdx < 0) break;  // no closed trades to evict
                Trades.RemoveAt(oldestIdx);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // HELPERS
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Detect highest TP reached on this bar, cumulative with prior.
        /// Returns the new HighestTpHit COUNT (0..5). Does NOT write to the
        /// trade — caller (PASS A) is responsible for ordering vs. trail
        /// logic (BUG B).
        /// </summary>
        private int DetectTpHits(TradeRecord trade, double high, double low)
        {
            int prev = trade.HighestTpHit;
            int n = CountValidTPs(trade);
            int highest = -1;
            for (int k = 0; k < n; k++)
            {
                double tp = trade.TPs[k];
                if (tp == 0.0) continue;  // skip uninitialized slots
                bool reached = trade.IsLong
                    ? (high >= tp)
                    : (low <= tp);
                if (reached)
                {
                    highest = k;
                    trade.TPHit[k] = true;
                }
            }
            int hitCount = highest + 1;
            return Math.Max(prev, hitCount);
        }

        /// <summary>
        /// Count how many TP slots are populated. Trades with NumTPs = 3
        /// have slots 0..2 used and 3..4 = 0. Used by all-TPs-hit logic.
        /// </summary>
        private int CountValidTPs(TradeRecord trade)
        {
            int n = 0;
            for (int k = 0; k < trade.TPs.Length; k++)
            {
                if (trade.TPs[k] != 0.0) n++;
            }
            return n;
        }

        /// <summary>
        /// Map a "all TPs hit" close to its ExitReason. n is the index of
        /// the last TP (1-based count), so n=3 → TP3, n=5 → TP5.
        /// </summary>
        private ExitReason MapTpExitReason(int n)
        {
            switch (n)
            {
                case 1: return ExitReason.TP1;
                case 2: return ExitReason.TP2;
                case 3: return ExitReason.TP3;
                case 4: return ExitReason.TP4;
                case 5: return ExitReason.TP5;
                default: return ExitReason.TP1;
            }
        }

        /// <summary>
        /// Map a TRL count to its ExitReason. count = 0 → TRL0 (the BUG G
        /// case — pre-TP1 trail-out, must NOT be mislabeled "SL").
        /// </summary>
        private ExitReason MapTrlExitReason(int count)
        {
            switch (count)
            {
                case 0: return ExitReason.TRL0;
                case 1: return ExitReason.TRL1;
                case 2: return ExitReason.TRL2;
                case 3: return ExitReason.TRL3;
                case 4: return ExitReason.TRL4;
                case 5: return ExitReason.TRL5;
                default: return ExitReason.TRL0;
            }
        }

        /// <summary>
        /// BUG G logic — compute exit reason for an SL-hit close.
        ///   • If hitTps == 0 AND curSL == origSL → SL (full original stop).
        ///   • If hitTps == 0 AND curSL has moved → TRL0 (pre-TP1 trail-out;
        ///     would have been mislabeled SL by the Pine pre-fix code path).
        ///   • If hitTps > 0 → TRL{hitTps}, regardless of R-sign. The R sign
        ///     determines Win/Loss/BE classification (see CloseTrade).
        /// </summary>
        private ExitReason ComputeExitReason(TradeRecord trade, int hitTps)
        {
            if (hitTps == 0)
            {
                // Pure-SL hit: was the trail moved? If not, it's a real SL.
                bool slMoved = trade.IsLong
                    ? (trade.CurSL > trade.OrigSL)
                    : (trade.CurSL < trade.OrigSL);
                return slMoved ? ExitReason.TRL0 : ExitReason.SL;
            }
            return MapTrlExitReason(hitTps);
        }

        /// <summary>
        /// Final-state classification by R-sign. R > 0 → Win, R < 0 → Loss,
        /// R == 0 → BreakEven. Used by CloseTrade.
        /// </summary>
        private TradeState ClassifyByRSign(double r)
        {
            if (r > 0) return TradeState.Win;
            if (r < 0) return TradeState.Loss;
            return TradeState.BreakEven;
        }

        /// <summary>
        /// Close a trade at the supplied price + reason. Computes signed R,
        /// dollar P&L, and writes State based on R-sign. Also writes
        /// CloseTime + ExitReason. Idempotent — a no-op on already-closed
        /// trades.
        /// </summary>
        private void CloseTrade(
            TradeRecord trade,
            double closePrice,
            DateTime barTime,
            ExitReason reason,
            double tickSize,
            double pointValue)
        {
            if (trade.IsClosed) return;

            double diff = trade.IsLong
                ? (closePrice - trade.Entry)
                : (trade.Entry - closePrice);
            double r = trade.SLDist > 0 ? diff / trade.SLDist : 0.0;

            // Dollar P&L: r × DollarRisk gives the signed $ outcome. (Equiv:
            // diff × tickVal × contracts / tickSize, but the R-form keeps
            // the math symmetric with the strip's LOCK/UNRL/FLOAT cells.)
            double dollarPnL = r * trade.DollarRisk;

            trade.R = r;
            trade.DollarPnL = dollarPnL;
            trade.CloseTime = barTime;
            trade.ExitReason = reason;
            // 2026-05-12: BE exit-reason override removed (Inv 2 audit fix).
            // The override re-labeled any exact-zero-R close as ExitReason.BE,
            // which made it impossible to distinguish "trailed to BE and got
            // stopped at entry" (TRL1 with r=0) from a deliberate scratch.
            // The State is still classified by R-sign below (r==0 → BreakEven
            // outcome), so the closed-trade summary still shows W/L/BE
            // correctly. ExitReason now preserves the mechanism label
            // (TRL1/TRL2/etc.) for forensic clarity in the forward-test
            // journal.
            trade.State = ClassifyByRSign(r);
            // Suppress unused-parameter warnings (kept for future symbol-
            // dependent close-price normalization).
            _ = tickSize;
            _ = pointValue;
        }

        /// <summary>
        /// Refresh live R and dollar P&L for an active trade based on close.
        /// Called once per bar in PASS A; callers can also invoke this at
        /// tick rate via RefreshLiveR() if intra-bar updates are desired
        /// (e.g., for the LOCK / UNRL / FLOAT strip).
        ///
        /// 2026-05-12: MFE / MAE moved out of this method into
        /// UpdateBarExcursion() so peak excursion uses bar wick extremes
        /// (high/low) rather than bar close. RefreshLiveR therefore no longer
        /// pollutes MFE/MAE with intra-bar close ticks.
        /// </summary>
        private void UpdateLiveR(TradeRecord trade, double close)
        {
            if (trade.SLDist <= 0) return;
            double diff = trade.IsLong
                ? (close - trade.Entry)
                : (trade.Entry - close);
            trade.R = diff / trade.SLDist;
            trade.DollarPnL = trade.R * trade.DollarRisk;
        }

        /// <summary>
        /// Update MFE / MAE from this bar's wick extremes — TRUE peak
        /// excursion (per López de Prado triple-barrier exit analysis).
        /// For a long: MFE candidate = (bar.High − Entry)/SLDist,
        ///             MAE candidate = (bar.Low  − Entry)/SLDist.
        /// For a short: mirrored. Called once per closed bar from PASS A,
        /// AFTER UpdateLiveR. Intentionally NOT called from RefreshLiveR
        /// because intra-bar ticks can't distinguish "real new extreme" from
        /// "noisy revisit of an already-extended wick."
        /// </summary>
        private void UpdateBarExcursion(TradeRecord trade, double high, double low)
        {
            if (trade.SLDist <= 0) return;
            double favR, adverseR;
            if (trade.IsLong)
            {
                favR     = (high - trade.Entry) / trade.SLDist;
                adverseR = (low  - trade.Entry) / trade.SLDist;
            }
            else
            {
                favR     = (trade.Entry - low)  / trade.SLDist;
                adverseR = (trade.Entry - high) / trade.SLDist;
            }
            if (favR     > trade.MfeR) trade.MfeR = favR;
            if (adverseR < trade.MaeR) trade.MaeR = adverseR;
        }

        /// <summary>
        /// Public hook for callers that want to refresh live R intra-bar
        /// (e.g., for the LOCK/UNRL/FLOAT strip). Updates only ACTIVE trades.
        /// </summary>
        public void RefreshLiveR(double close)
        {
            for (int i = 0; i < Trades.Count; i++)
            {
                var tr = Trades[i];
                if (tr.State == TradeState.Active)
                    UpdateLiveR(tr, close);
            }
        }

        /// <summary>
        /// Compute entry price for a zone — DEPRECATED stub. Always returned
        /// OF 0% (the lip) regardless of user input, which silently broke the
        /// OF 25/50/75 selector for armed trades. Callers must use
        /// TradePhantomsIOF.EntryTPMath.ComputeEntry(ActiveOFEntry, ...) which
        /// honors the discrete OF depth enum (see Fix L1). Kept here for
        /// reference but no longer invoked from PASS C.
        /// </summary>
        [Obsolete("Use TradePhantomsIOF.EntryTPMath.ComputeEntry with ActiveOFEntry. See Fix L1.")]
        private double ComputeEntry(ZoneInfo z)
        {
            // OF 0% — at the body/wick junction (the "lip"). Demand → bodyLo,
            // supply → bodyHi. Retained only to avoid breaking any external
            // caller that referenced this private method via reflection.
            return z.IsLong ? z.BodyLo : z.BodyHi;
        }

        /// <summary>
        /// Compute SL price for a zone — past the far wick + buffer. The far
        /// wick for demand is wickLo; for supply, wickHi. Buffer is
        /// StopBufferTicks × tickSize.
        /// </summary>
        private double ComputeSL(ZoneInfo z, double tickSize)
        {
            double buffer = StopBufferTicks * Math.Max(tickSize, 0);
            return z.IsLong
                ? z.WickLo - buffer
                : z.WickHi + buffer;
        }

        /// <summary>
        /// Local helper for the sequential-gate flag refresh after a close.
        /// True if ANY trade other than the supplied one is still active.
        /// </summary>
        private bool AnyActiveExcept(TradeRecord excluded)
        {
            for (int i = 0; i < Trades.Count; i++)
            {
                var tr = Trades[i];
                if (tr == excluded) continue;
                if (tr.State == TradeState.Active) return true;
            }
            return false;
        }

        // ────────────────────────────────────────────────────────────────────
        // PUBLIC QUERIES
        // ────────────────────────────────────────────────────────────────────

        /// <summary>Yields ARMED + ACTIVE trades (live).</summary>
        public IEnumerable<TradeRecord> GetActive()
        {
            for (int i = 0; i < Trades.Count; i++)
            {
                var tr = Trades[i];
                if (tr.IsLive) yield return tr;
            }
        }

        /// <summary>Yields ACTIVE-only trades (excludes ARMED).</summary>
        public IEnumerable<TradeRecord> GetActiveOnly()
        {
            for (int i = 0; i < Trades.Count; i++)
            {
                var tr = Trades[i];
                if (tr.State == TradeState.Active) yield return tr;
            }
        }

        /// <summary>
        /// Yields closed trades, NEWEST FIRST. BUG F (audit #17): the visibility
        /// cap on the chart should always be the most recent trades, so we
        /// expose them in newest-first order to make the consumer's render
        /// loop trivial — it just takes the first N.
        /// </summary>
        /// <param name="max">
        /// Maximum number of closed trades to yield. Pass -1 (default) for all.
        /// </param>
        public IEnumerable<TradeRecord> GetClosed(int max = -1)
        {
            // Collect, sort by CloseTime descending, optionally cap.
            var closed = new List<TradeRecord>();
            for (int i = 0; i < Trades.Count; i++)
            {
                if (Trades[i].IsClosed) closed.Add(Trades[i]);
            }
            closed.Sort(delegate (TradeRecord a, TradeRecord b)
            {
                return b.CloseTime.CompareTo(a.CloseTime);
            });
            int count = (max < 0) ? closed.Count : Math.Min(max, closed.Count);
            for (int i = 0; i < count; i++)
                yield return closed[i];
        }

        /// <summary>True if any trade is currently ACTIVE.</summary>
        public bool AnyActive()
        {
            for (int i = 0; i < Trades.Count; i++)
            {
                if (Trades[i].State == TradeState.Active) return true;
            }
            return false;
        }

        /// <summary>True if any trade is ARMED or ACTIVE.</summary>
        public bool AnyLive()
        {
            for (int i = 0; i < Trades.Count; i++)
            {
                if (Trades[i].IsLive) return true;
            }
            return false;
        }

        /// <summary>
        /// Reset all state — drops every trade and resets the ID counter.
        /// Useful for chart-reload scenarios or strategy switches where the
        /// user wants a clean slate.
        /// </summary>
        public void Reset()
        {
            Trades.Clear();
            NextTradeId = 1;
        }

        /// <summary>
        /// Quick stats summary for the dashboard: counts of W/L/BE and total R.
        /// </summary>
        public LifecycleStats ComputeStats()
        {
            var s = new LifecycleStats();
            for (int i = 0; i < Trades.Count; i++)
            {
                var tr = Trades[i];
                if (!tr.IsClosed) continue;
                s.Closed++;
                s.TotalR += tr.R;
                s.TotalDollar += tr.DollarPnL;
                if (tr.State == TradeState.Win)
                {
                    s.Wins++;
                    s.TotalWinR += tr.R;
                }
                else if (tr.State == TradeState.Loss)
                {
                    s.Losses++;
                    s.TotalLossR += tr.R;
                }
                else if (tr.State == TradeState.BreakEven)
                {
                    s.BreakEvens++;
                }
            }
            if (s.Closed > 0)
                s.WinRate = (double)s.Wins / s.Closed;
            // Profit factor: gross wins / |gross losses|. Undefined if no losses.
            if (s.TotalLossR < 0)
                s.ProfitFactor = s.TotalWinR / Math.Abs(s.TotalLossR);
            else
                s.ProfitFactor = double.PositiveInfinity;
            if (s.Wins > 0) s.AvgWinR = s.TotalWinR / s.Wins;
            if (s.Losses > 0) s.AvgLossR = s.TotalLossR / s.Losses;
            return s;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // STATS BUNDLE
    // Returned by TradeLifecycleManager.ComputeStats(). Mirrors the dashboard
    // STATS row: W / L / BE counts, WR, PF, total R, total $, AvgW, AvgL.
    // ────────────────────────────────────────────────────────────────────────

    public class LifecycleStats
    {
        public int Closed;
        public int Wins;
        public int Losses;
        public int BreakEvens;
        public double WinRate;          // 0..1
        public double ProfitFactor;     // gross W / |gross L|; +inf if no losses
        public double TotalR;
        public double TotalDollar;
        public double TotalWinR;
        public double TotalLossR;       // negative
        public double AvgWinR;
        public double AvgLossR;         // negative
    }
}
