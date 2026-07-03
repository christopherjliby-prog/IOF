// =====================================================================================
// RiskGovernor.cs — Hard, non-negotiable prop-eval risk enforcement for the IOF bot.
// =====================================================================================
// PURPOSE
// -------
// This is the load-bearing safety core for a 50K EOD-trailing-drawdown prop evaluation.
// It enforces risk IN CODE, not discipline. Every trading decision must pass through it.
// The whole design goal: the bot *cannot* place a trade that risks crossing the
// $2,000 EOD trailing drawdown line, and it stops for the day the moment the
// $1,540 daily target is banked (no giving it back).
//
// DELIBERATELY SDK-FREE. This class references only `System`. It takes plain numbers
// (equity, P&L, prices) and returns plain decisions. That is intentional:
//   1. It can be unit-tested and Monte-Carlo'd off-platform (see backtest/eval_sim.py,
//      which mirrors this exact logic line-for-line — keep them in lockstep).
//   2. The Quantower Strategy (IOF_PropEvalBot.cs) is the ONLY place that touches the
//      TradingPlatform SDK; it feeds account numbers into this governor and acts on
//      the returned verdicts. Separation of concerns = the risk math is auditable
//      without a running platform.
//
// EVAL MODEL (confirmed hard parameters — see docs/BUILD_REPORT.md §Eval Math)
// ---------------------------------------------------------------------------
//   • 50,000 starting balance.
//   • EOD trailing drawdown, max $2,000  → initial floor = 48,000.
//   • Daily profit target $1,540 → bank and STOP for the day when reached.
//
// THE AIRTIGHT INVARIANT (why the floor is mathematically unreachable)
// -------------------------------------------------------------------
// At all times the governor computes:
//     roomToFloor          = currentEquity - floor
//     effectiveDailyLoss   = min(configDailyLoss, roomToFloor - killBuffer - worstCaseTrade)
// A new trade is REFUSED unless, after its worst-case outcome (stop-out PLUS a
// slippage/gap allowance), BOTH:
//     (a) equity would still sit >= floor + killBuffer, and
//     (b) the day's realized loss would still sit >  -effectiveDailyLoss.
// Because effectiveDailyLoss SHRINKS as roomToFloor shrinks (losing streaks tighten
// the leash automatically), the running equity can never descend to `floor`. The
// kill-switch (flatten + halt on equity <= floor + killBuffer) is only ever the
// last-resort backstop for a data-feed anomaly; in normal operation the entry gate
// stops trading long before it can fire. This property is demonstrated against
// adversarial worst-case loss streaks in backtest/eval_sim.py.
// =====================================================================================

using System;

namespace TradePhantomsIOF.Risk
{
    /// <summary>Why the governor blocked / halted, for logging + the dashboard.</summary>
    public enum RiskVerdict
    {
        Allow = 0,
        Block_EvalHalted,          // kill-switch already tripped for the whole eval
        Block_DayHalted,           // day is done (target hit or daily loss hit)
        Block_WouldBreachFloor,    // worst-case outcome would come within killBuffer of the DD line
        Block_WouldBreachDaily,    // worst-case outcome would exceed the (effective) daily loss limit
        Block_NoRoomToTrade,       // cushion too thin to size even one safe contract
        Block_MaxTradesReached,    // per-day trade cap (HFT / overtrading guard)
        Block_OutsideSession,      // outside the permitted trading window / news blackout
        Block_ZeroSize             // sizing math produced 0 contracts (slDist too wide for budget)
    }

    /// <summary>Reason a day or the eval was halted (drives flatten + lockout).</summary>
    public enum HaltReason
    {
        None = 0,
        DailyTargetReached,        // +$1,540 banked — stop, do not give it back
        DailyLossLimitReached,     // hit the (effective) daily loss cutoff
        KillSwitchDrawdown,        // equity fell to floor + killBuffer — emergency flatten
        SessionEnd,               // EOD flat (no overnight)
        Manual
    }

    /// <summary>Immutable configuration for one evaluation account.</summary>
    public sealed class RiskConfig
    {
        // --- Eval-defined hard parameters ---
        public double StartBalance      = 50_000.0;
        public double MaxTrailingDD     = 2_000.0;   // EOD trailing drawdown ceiling
        public double DailyProfitTarget = 1_540.0;   // bank-and-stop for the day
        public double EvalProfitTarget  = 3_000.0;   // total profit to PASS (6% of 50K; adjust per firm)

        // --- Self-imposed risk budget (all < the hard parameters, by construction) ---
        // Configured daily loss cutoff. The governor uses min(this, dynamic room-based cap).
        public double DailyLossLimit    = 600.0;
        // Per-trade dollar risk at the stop (used by the sizer in EntryTPMath.ComputeContracts).
        public double PerTradeDollarRisk = 150.0;
        // Slippage / gap multiple applied to per-trade risk to get the WORST-CASE single-trade
        // loss the gate must survive. A stop is not a guarantee; price can gap through it.
        // 2.0 = assume a fill up to 2x the intended stop distance in a fast market.
        public double WorstCaseGapMultiple = 2.0;
        // Kill-switch cushion held ABOVE the floor. The kill-switch fires at floor+this.
        public double KillSwitchBuffer  = 300.0;
        // Absolute contract ceiling (research: <=10 MNQ for Christopher's Lucid accounts).
        public int    MaxContracts      = 10;
        // Instrument economics (MNQ: $2 per index point, 0.25 tick => $0.50/tick).
        public double PointValue        = 2.0;
        public double TickSize          = 0.25;

        // --- Behavioural guards (prop-firm compliance, see docs/SPEC_VERIFICATION.md) ---
        public int    MaxTradesPerDay   = 15;    // well under MFFU's 200 HFT cap; anti-overtrade
        public double MinHoldSeconds     = 120;  // >=2 min: clears every Tier-1/2 microscalp rule
        // Minimum cushion below which we simply do not trade (safety, not opportunity).
        public double MinRoomToTrade     = 350.0;
    }

    /// <summary>
    /// The result of asking "may I take this specific trade?" — carries the safe,
    /// possibly reduced, contract count and the reason if blocked.
    /// </summary>
    public struct EntryDecision
    {
        public bool Allowed;
        public RiskVerdict Verdict;
        public int Contracts;          // 0 when blocked
        public double WorstCaseLoss;   // $ the gate assumed for this trade
        public double EffectiveDailyLoss;
        public double RoomToFloor;
        public string Note;
    }

    /// <summary>
    /// Stateful per-eval risk state machine. One instance per evaluation account.
    /// Thread-affinity: mutate only from the strategy's processing thread (or lock
    /// externally). All money is in account currency (USD).
    /// </summary>
    public sealed class RiskGovernor
    {
        private readonly RiskConfig _cfg;

        // Trailing-DD state (updated at EOD only — intraday spikes never move the floor).
        public double Floor { get; private set; }
        public double HighWaterEOD { get; private set; }

        // Running account state.
        public double RealizedEquity { get; private set; }  // balance from closed trades
        public double UnrealizedPnL { get; private set; }   // live open-position P&L (marked each tick)

        // Per-day state.
        public double DayStartEquity { get; private set; }
        public double DayRealizedPnL { get; private set; }
        public int    TradesToday { get; private set; }
        public bool   DayHalted { get; private set; }
        public HaltReason DayHaltReason { get; private set; }

        // Global eval state.
        public bool   EvalHalted { get; private set; }
        public HaltReason EvalHaltReason { get; private set; }
        public bool   EvalPassed { get; private set; }

        public RiskConfig Config => _cfg;
        public double CurrentEquity => RealizedEquity + UnrealizedPnL;

        public RiskGovernor(RiskConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            RealizedEquity = cfg.StartBalance;
            HighWaterEOD   = cfg.StartBalance;
            Floor          = cfg.StartBalance - cfg.MaxTrailingDD;   // 48,000
            DayStartEquity = cfg.StartBalance;
            DayRealizedPnL = 0.0;
            UnrealizedPnL  = 0.0;
        }

        // -----------------------------------------------------------------------------
        // Cushion math — the heart of the airtight guarantee.
        // -----------------------------------------------------------------------------

        /// <summary>Dollars between current equity and the drawdown floor.</summary>
        public double RoomToFloor() => CurrentEquity - Floor;

        /// <summary>The single-trade worst-case loss the gate must survive.</summary>
        public double WorstCaseTradeLoss(int contracts, double slDistancePrice)
        {
            // slDistance in price * pointValue = $ risk per contract at the stop;
            // multiplied by the gap allowance for fills beyond the stop.
            double perContract = slDistancePrice * _cfg.PointValue * _cfg.WorstCaseGapMultiple;
            return perContract * contracts;
        }

        /// <summary>
        /// The daily loss cutoff actually in force RIGHT NOW. It is the smaller of the
        /// configured limit and the room-based cap, so the leash tightens automatically
        /// as the cushion to the floor shrinks. Never negative.
        /// </summary>
        public double EffectiveDailyLoss(double worstCaseTrade)
        {
            double roomCap = RoomToFloor() - _cfg.KillSwitchBuffer - worstCaseTrade;
            double eff = Math.Min(_cfg.DailyLossLimit, roomCap);
            return Math.Max(0.0, eff);
        }

        // -----------------------------------------------------------------------------
        // Entry gate — the bot asks BEFORE every order whether it is safe.
        // -----------------------------------------------------------------------------

        /// <summary>
        /// Decide whether the proposed trade may be taken, and with how many contracts.
        /// The sizer (EntryTPMath.ComputeContracts) proposes a size from PerTradeDollarRisk;
        /// this gate can only ever REDUCE it, never increase it, and blocks entirely when
        /// any hard constraint would be threatened.
        /// </summary>
        /// <param name="proposedContracts">Size proposed by the dollar-risk sizer.</param>
        /// <param name="slDistancePrice">|entry - stop| in price units.</param>
        /// <param name="inSession">Whether we're inside the permitted trading window.</param>
        public EntryDecision EvaluateEntry(int proposedContracts, double slDistancePrice, bool inSession)
        {
            var d = new EntryDecision
            {
                Allowed = false,
                Contracts = 0,
                RoomToFloor = RoomToFloor()
            };

            if (EvalHalted) { d.Verdict = RiskVerdict.Block_EvalHalted; d.Note = EvalHaltReason.ToString(); return d; }
            if (DayHalted)  { d.Verdict = RiskVerdict.Block_DayHalted;  d.Note = DayHaltReason.ToString();  return d; }
            if (!inSession) { d.Verdict = RiskVerdict.Block_OutsideSession; return d; }
            if (TradesToday >= _cfg.MaxTradesPerDay) { d.Verdict = RiskVerdict.Block_MaxTradesReached; return d; }
            if (proposedContracts <= 0 || slDistancePrice <= 0.0) { d.Verdict = RiskVerdict.Block_ZeroSize; return d; }
            if (RoomToFloor() < _cfg.MinRoomToTrade) { d.Verdict = RiskVerdict.Block_NoRoomToTrade; return d; }

            // Start from the proposed size, hard-cap at MaxContracts, then reduce until
            // BOTH the floor gate and the daily gate pass. If we can't fit even 1, block.
            int contracts = Math.Min(proposedContracts, _cfg.MaxContracts);

            while (contracts > 0)
            {
                double wct = WorstCaseTradeLoss(contracts, slDistancePrice);
                double effDaily = EffectiveDailyLoss(wct);

                // (a) Floor gate: worst case must leave a cushion above the kill line.
                bool floorOk = (CurrentEquity - wct) >= (Floor + _cfg.KillSwitchBuffer);
                // (b) Daily gate: worst case must not exceed the effective daily loss.
                bool dailyOk = (DayRealizedPnL - wct) > -effDaily;

                if (floorOk && dailyOk)
                {
                    d.Allowed = true;
                    d.Verdict = RiskVerdict.Allow;
                    d.Contracts = contracts;
                    d.WorstCaseLoss = wct;
                    d.EffectiveDailyLoss = effDaily;
                    return d;
                }

                // Record the binding reason for when we finally give up.
                d.Verdict = floorOk ? RiskVerdict.Block_WouldBreachDaily
                                    : RiskVerdict.Block_WouldBreachFloor;
                contracts--;   // try a smaller, safer size
            }

            return d; // nothing safe fit
        }

        // -----------------------------------------------------------------------------
        // State transitions — called by the strategy on the matching platform events.
        // -----------------------------------------------------------------------------

        /// <summary>Mark live open-position P&L (call on each update / tick).</summary>
        /// <returns>true if the kill-switch just tripped and the caller must flatten NOW.</returns>
        public bool MarkUnrealized(double unrealizedPnL)
        {
            UnrealizedPnL = unrealizedPnL;
            if (EvalHalted) return false;

            // Last-resort backstop. Should never fire if the entry gate did its job,
            // but a gap / feed glitch could in principle move equity faster than a bar.
            if (CurrentEquity <= Floor + _cfg.KillSwitchBuffer)
            {
                TripKillSwitch();
                return true;
            }
            return false;
        }

        /// <summary>Register a closed trade's realized P&L. Applies target/loss lockouts.</summary>
        public void OnTradeClosed(double realizedPnL)
        {
            RealizedEquity += realizedPnL;
            DayRealizedPnL += realizedPnL;
            UnrealizedPnL   = 0.0;
            TradesToday++;

            // Eval pass check (cumulative profit target).
            if (!EvalPassed && (RealizedEquity - _cfg.StartBalance) >= _cfg.EvalProfitTarget)
            {
                EvalPassed = true;
                HaltEval(HaltReason.DailyTargetReached); // pass = stop trading; reuse flatten path
            }

            // Daily target — bank it and STOP for the day (no giving it back).
            if (!DayHalted && DayRealizedPnL >= _cfg.DailyProfitTarget)
                HaltDay(HaltReason.DailyTargetReached);

            // Daily loss cutoff (against the CONFIGURED limit; the entry gate already
            // enforced the tighter effective limit, so this is a clean secondary stop).
            if (!DayHalted && DayRealizedPnL <= -_cfg.DailyLossLimit)
                HaltDay(HaltReason.DailyLossLimitReached);
        }

        /// <summary>End-of-day roll: trail the floor on the EOD balance, reset the day.</summary>
        public void OnSessionClose()
        {
            // Trailing drawdown updates on the EOD BALANCE only (no unrealized, no
            // intraday spike). Floor only ever moves UP.
            if (RealizedEquity > HighWaterEOD) HighWaterEOD = RealizedEquity;
            double newFloor = HighWaterEOD - _cfg.MaxTrailingDD;
            if (newFloor > Floor) Floor = newFloor;

            // Reset per-day state for the next session.
            DayStartEquity = RealizedEquity;
            DayRealizedPnL = 0.0;
            TradesToday    = 0;
            UnrealizedPnL  = 0.0;
            if (!EvalHalted) { DayHalted = false; DayHaltReason = HaltReason.None; }
        }

        private void HaltDay(HaltReason r)   { DayHalted = true;  DayHaltReason = r; }
        private void HaltEval(HaltReason r)  { EvalHalted = true; EvalHaltReason = r; DayHalted = true; DayHaltReason = r; }
        private void TripKillSwitch()        { HaltEval(HaltReason.KillSwitchDrawdown); }

        /// <summary>Manual / external stop (user hit the panic button, connection lost, etc.).</summary>
        public void Kill(HaltReason r = HaltReason.Manual) => HaltEval(r);

        // -----------------------------------------------------------------------------
        // Config sanity — call once at startup; refuses to run an unsafe configuration.
        // -----------------------------------------------------------------------------

        /// <summary>
        /// Verifies the config can NEVER, by construction, let a single fresh-day worst
        /// case reach the floor. Returns null if safe, else a human-readable reason.
        /// </summary>
        public string ValidateConfig()
        {
            if (_cfg.DailyLossLimit <= 0) return "DailyLossLimit must be > 0";
            if (_cfg.DailyProfitTarget <= 0) return "DailyProfitTarget must be > 0";
            if (_cfg.PerTradeDollarRisk <= 0) return "PerTradeDollarRisk must be > 0";
            if (_cfg.KillSwitchBuffer <= 0) return "KillSwitchBuffer must be > 0";
            if (_cfg.MaxContracts <= 0) return "MaxContracts must be > 0";

            // On a fresh eval the cushion is MaxTrailingDD. The daily loss cutoff plus
            // the kill buffer plus one worst-case trade must fit inside it.
            double freshRoom = _cfg.MaxTrailingDD;
            // Worst-case single trade at the per-trade budget (slDist implied by budget/size).
            // Upper bound: full daily budget consumed by risk; use per-trade * gap as the unit.
            double worstOne = _cfg.PerTradeDollarRisk * _cfg.WorstCaseGapMultiple;
            double need = _cfg.DailyLossLimit + _cfg.KillSwitchBuffer + worstOne;
            if (need > freshRoom)
                return $"Unsafe: DailyLoss({_cfg.DailyLossLimit}) + KillBuffer({_cfg.KillSwitchBuffer}) + "
                     + $"WorstTrade({worstOne:F0}) = {need:F0} exceeds fresh room {freshRoom:F0}. "
                     + "Lower DailyLossLimit / PerTradeDollarRisk.";
            if (_cfg.PerTradeDollarRisk > _cfg.DailyLossLimit)
                return "PerTradeDollarRisk should be <= DailyLossLimit (a single stop shouldn't end the day).";
            return null;
        }
    }
}
