// =====================================================================================
// IOF_PropEvalBot.cs — Quantower automated Strategy for a 50K EOD-drawdown prop eval.
// =====================================================================================
// This is the EXECUTION layer. It is the ONLY file that touches the Quantower SDK
// (TradingPlatform.BusinessLayer). It composes three audited, SDK-free components:
//
//   • RiskGovernor  (strategy/RiskGovernor.cs) — hard risk enforcement. Every entry
//     passes through it; it marks unrealized P&L every tick (kill-switch); it trails
//     the EOD drawdown floor; it locks the day out at +$1,540 or the daily loss cutoff.
//   • SignalEngine  (strategy/SignalEngine.cs) — the ORDERFLOW_SPEC.md entry doctrine
//     (context -> location -> confirmation AND-gate). Returns proposals, never trades.
//   • EntryTPMath   (EntryAndTPHelpers.cs) — pure sizing / tick-snapping / R math,
//     reused verbatim from the existing IOF project.
//
// DESIGN CONTRACT (matches the brief):
//   - Risk is enforced in CODE, not discipline. The bot CANNOT place a trade whose
//     worst case could cross the $2,000 EOD drawdown line (RiskGovernor.EvaluateEntry).
//   - It banks +$1,540 and STOPS for the day (no giving it back).
//   - It flattens flat at session close (no overnight — every firm bans it).
//   - A kill-switch flattens and halts the whole eval if equity ever approaches the floor.
//
// COMPILE / DEPLOY NOTES (honest):
//   - This was authored against the documented Quantower Strategy API. It was NOT
//     compiled here (no .NET / no Quantower SDK in the build container). Seams that
//     depend on the exact installed SDK version are marked  // >>> VERIFY vX  <<<.
//     The RiskGovernor wiring — the load-bearing safety — uses only arithmetic and is
//     independently proven in backtest/eval_sim.py.
//   - Footprint (per-price bid/ask volume) is required by the SignalEngine. It is
//     sourced from Quantower's Volume Analysis; if that feed is unavailable the engine
//     simply produces no signals (safe: the bot trades nothing rather than blind).
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

using TradePhantomsIOF;                 // EntryTPMath
using TradePhantomsIOF.Risk;            // RiskGovernor
using TradePhantomsIOF.Signals;         // SignalEngine
// `Side` is declared in BOTH TradePhantomsIOF.Signals and TradingPlatform.BusinessLayer.
// Alias the order-flow one so bare `Side` is never ambiguous in this file.
using SSide = TradePhantomsIOF.Signals.Side;
using QSide = TradingPlatform.BusinessLayer.Side;

namespace TradePhantomsIOF.Bot
{
    public class IOF_PropEvalBot : Strategy
    {
        // ---------------- Inputs: instrument / account ----------------
        [InputParameter("Symbol", 0)]
        public Symbol Symbol;

        [InputParameter("Account", 1)]
        public Account Account;

        [InputParameter("Decision timeframe", 2)]
        public Period DecisionPeriod = Period.MIN1;   // spec: read the flip on the finer 1m

        [InputParameter("HTF context timeframe", 3)]
        public Period ContextPeriod = Period.MIN15;   // HTF-is-king context/bias

        // ---------------- Inputs: risk (hard eval parameters) ----------------
        [InputParameter("Start balance ($)", 10)]
        public double StartBalance = 50_000.0;
        [InputParameter("Max trailing DD ($)", 11)]
        public double MaxTrailingDD = 2_000.0;
        [InputParameter("Daily profit target ($)", 12)]
        public double DailyProfitTarget = 1_540.0;
        [InputParameter("Eval profit target to PASS ($)", 13)]
        public double EvalProfitTarget = 3_000.0;
        [InputParameter("Daily loss cutoff ($)", 14)]
        public double DailyLossLimit = 600.0;
        [InputParameter("Per-trade risk ($)", 15)]
        public double PerTradeDollarRisk = 150.0;
        [InputParameter("Worst-case gap multiple", 16)]
        public double WorstCaseGapMultiple = 2.0;
        [InputParameter("Kill-switch buffer ($)", 17)]
        public double KillSwitchBuffer = 300.0;
        [InputParameter("Max contracts", 18)]
        public int MaxContracts = 10;
        [InputParameter("Max trades / day", 19)]
        public int MaxTradesPerDay = 15;
        [InputParameter("Min hold seconds", 20)]
        public int MinHoldSeconds = 120;

        // ---------------- Inputs: session window (exchange time) ----------------
        [InputParameter("Session start hour (local)", 30)]
        public int SessionStartHour = 9;
        [InputParameter("Session start minute", 31)]
        public int SessionStartMinute = 35;   // let the RTH-open whip resolve (spec Tool 13)
        [InputParameter("Session flat-by hour (local)", 32)]
        public int FlatByHour = 15;
        [InputParameter("Session flat-by minute", 33)]
        public int FlatByMinute = 55;         // flat before the 16:00 close, no overnight

        // ---------------- State ----------------
        private RiskGovernor _gov;
        private SignalEngine _engine;
        private SignalConfig _sigCfg;
        private HistoricalData _decisionHd;
        private HistoricalData _contextHd;
        private double _pointValue = 2.0;
        private double _tickSize = 0.25;
        private int _lastProcessedBar = -1;
        private DateTime _currentSessionDay = DateTime.MinValue;
        private DateTime _pendingEntryTime = DateTime.MinValue;
        private bool _started;

        public IOF_PropEvalBot()
        {
            Name = "IOF Prop-Eval Bot (50K EOD-DD)";
            Description = "IOF order-flow entries wrapped in hard prop-eval risk enforcement.";
        }

        // ===============================================================================
        // Lifecycle
        // ===============================================================================
        protected override void OnRun()
        {
            if (Symbol == null || Account == null)
            {
                Log("Symbol/Account not set — refusing to run.", StrategyLoggingLevel.Error);
                Stop();
                return;
            }

            _tickSize = Symbol.TickSize > 0 ? Symbol.TickSize : 0.25;
            _pointValue = ResolvePointValue(Symbol);   // MNQ -> 2.0

            // Build the risk governor from inputs and REFUSE to start if unsafe.
            var rc = new RiskConfig
            {
                StartBalance = StartBalance,
                MaxTrailingDD = MaxTrailingDD,
                DailyProfitTarget = DailyProfitTarget,
                EvalProfitTarget = EvalProfitTarget,
                DailyLossLimit = DailyLossLimit,
                PerTradeDollarRisk = PerTradeDollarRisk,
                WorstCaseGapMultiple = WorstCaseGapMultiple,
                KillSwitchBuffer = KillSwitchBuffer,
                MaxContracts = MaxContracts,
                PointValue = _pointValue,
                TickSize = _tickSize,
                MaxTradesPerDay = MaxTradesPerDay,
                MinHoldSeconds = MinHoldSeconds
            };
            _gov = new RiskGovernor(rc);
            string unsafeReason = _gov.ValidateConfig();
            if (unsafeReason != null)
            {
                Log("UNSAFE CONFIG — refusing to start: " + unsafeReason, StrategyLoggingLevel.Error);
                Stop();
                return;
            }

            _sigCfg = new SignalConfig { TickSize = _tickSize, PointValue = _pointValue, StopBufferTicks = 2 };
            _engine = new SignalEngine(_sigCfg);

            // Seed the governor's realized balance from the actual account, if available.
            // >>> VERIFY vX: Account.Balance vs Account.BalancePlusAllProjectedPnL <<<
            try { if (Account.Balance > 0) { /* keep model start; live balance drives equity via P&L */ } }
            catch { /* older SDKs differ; the governor tracks P&L deltas regardless */ }

            // Historical feeds. Volume analysis is required for the footprint reads.
            _decisionHd = Symbol.GetHistory(DecisionPeriod, Symbol.HistoryType, DateTime.UtcNow.AddDays(-5));
            _contextHd = Symbol.GetHistory(ContextPeriod, Symbol.HistoryType, DateTime.UtcNow.AddDays(-10));
            RequestVolumeAnalysis(_decisionHd);     // >>> VERIFY vX: VolumeAnalysis load API <<<

            _decisionHd.NewHistoryItem += OnNewDecisionBar;
            Symbol.NewLast += OnNewLast;
            Symbol.NewQuote += OnNewQuote;
            Core.Instance.PositionRemoved += OnPositionRemoved;

            _currentSessionDay = ExchangeNow().Date;
            _started = true;
            Log($"Started. Floor ${_gov.Floor:N0}, room to floor ${_gov.RoomToFloor():N0}. "
                + $"Daily target +${DailyProfitTarget:N0}, daily cutoff -${DailyLossLimit:N0}.",
                StrategyLoggingLevel.Trading);
        }

        protected override void OnStop()
        {
            if (!_started) return;
            try
            {
                if (_decisionHd != null) _decisionHd.NewHistoryItem -= OnNewDecisionBar;
                if (Symbol != null) { Symbol.NewLast -= OnNewLast; Symbol.NewQuote -= OnNewQuote; }
                Core.Instance.PositionRemoved -= OnPositionRemoved;
                CancelAllWorkingOrders();
                FlattenAll("strategy stop");
            }
            catch (Exception ex) { Log("OnStop cleanup: " + ex.Message, StrategyLoggingLevel.Error); }
        }

        // ===============================================================================
        // Per-tick: kill-switch + session boundary. This is the safety heartbeat.
        // ===============================================================================
        private void OnNewLast(Symbol s, Last last) => Heartbeat(last.Price);
        private void OnNewQuote(Symbol s, Quote q) => Heartbeat((q.Bid + q.Ask) / 2.0);

        private void Heartbeat(double price)
        {
            if (_gov == null) return;

            // 1) Session roll: if we've crossed into a new session day, EOD-close the prior.
            var now = ExchangeNow();
            if (now.Date != _currentSessionDay && now.Date > _currentSessionDay)
            {
                FlattenAll("session roll");
                _gov.OnSessionClose();
                _currentSessionDay = now.Date;
                Log($"EOD roll. New floor ${_gov.Floor:N0} (hwm ${_gov.HighWaterEOD:N0}).",
                    StrategyLoggingLevel.Trading);
            }

            // 2) Flat-by cutoff (no overnight). Flatten + block new entries near the close.
            if (IsPastFlatBy(now) && HasOpenPosition())
                FlattenAll("flat-by cutoff");

            // 3) Mark unrealized -> kill-switch. If it trips, flatten NOW and halt the eval.
            double unrl = OpenUnrealizedPnL();
            bool tripped = _gov.MarkUnrealized(unrl);
            if (tripped)
            {
                CancelAllWorkingOrders();
                FlattenAll("KILL-SWITCH: equity approached DD floor");
                Log($"*** KILL-SWITCH TRIPPED at equity ${_gov.CurrentEquity:N0} "
                    + $"(floor ${_gov.Floor:N0}). Eval halted. ***", StrategyLoggingLevel.Error);
            }
        }

        // ===============================================================================
        // Per-bar: run the doctrine when flat and the risk gate permits.
        // ===============================================================================
        private void OnNewDecisionBar(HistoricalData hd, HistoryEventArgs args)
        {
            if (_gov == null || _engine == null) return;
            int last = _decisionHd.Count - 1;
            if (last <= _lastProcessedBar) return;
            _lastProcessedBar = last;

            // Only act on CLOSED bars (index last-1). Build its footprint.
            int closedIdx = last - 1;
            if (closedIdx < 1) return;

            if (!TryBuildFootprint(closedIdx, out var fp)) return;  // no footprint -> no trade (safe)
            _engine.PushBar(fp);

            // Never stack positions; manage one at a time.
            if (HasOpenPosition() || HasWorkingEntryOrder()) return;

            // Halt / session gates.
            var now = ExchangeNow();
            bool inSession = IsInSession(now);
            if (_gov.EvalHalted || _gov.DayHalted || !inSession) return;

            // Context (HTF bias) + Location legs (VP levels).
            SSide htfBias = ComputeHtfBias();
            var locations = BuildProfileLevels();
            double lastPrice = _decisionHd[closedIdx][PriceType.Close];

            // ---- The doctrine decides IF and WHERE. ----
            Signal sig = _engine.Evaluate(htfBias, locations, lastPrice);
            if (sig == null) return;

            // ---- Dollar-risk sizing (reused pure math). ----
            double entry = EntryTPMath.SnapToTickNearest(sig.EntryLimit, _tickSize);
            double stop = EntryTPMath.SnapAwayFromReference(sig.StopPrice, entry, _tickSize);
            double slDist = EntryTPMath.ComputeSlDistance(entry, stop);
            int proposed = EntryTPMath.ComputeContracts(PerTradeDollarRisk, slDist, _pointValue, MaxContracts);

            // ---- The RISK GATE decides HOW MUCH — and can veto entirely. ----
            EntryDecision decision = _gov.EvaluateEntry(proposed, slDist, inSession);
            if (!decision.Allowed)
            {
                Log($"Signal {sig.Side} @ {entry} blocked by risk gate: {decision.Verdict} "
                    + $"(room ${decision.RoomToFloor:N0}).", StrategyLoggingLevel.Trading);
                return;
            }

            double target = EntryTPMath.SnapAwayFromReference(sig.TargetPrice, entry, _tickSize);
            PlaceBracket(sig.Side, decision.Contracts, entry, stop, target, sig);
        }

        // ===============================================================================
        // Order placement — resting limit entry + protective stop + target (no-chase).
        // ===============================================================================
        private void PlaceBracket(SSide side, int qty, double entry, double stop, double target, Signal sig)
        {
            try
            {
                var op = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Symbol = Symbol,
                    Account = Account,
                    Side = side == SSide.Long ? QSide.Buy : QSide.Sell,
                    OrderTypeId = OrderType.Limit,          // resting LIMIT at the level (no chase)
                    Quantity = qty,
                    Price = entry,
                    TimeInForce = TimeInForce.Day,
                    // Attach protective bracket. >>> VERIFY vX: bracket param names <<<
                    StopLoss = SlTpHolder.CreateSL(stop, PriceMeasurement.Absolute),
                    TakeProfit = SlTpHolder.CreateTP(target, PriceMeasurement.Absolute),
                });

                if (op == null || op.Status == TradingOperationResultStatus.Failure)
                {
                    Log($"PlaceOrder FAILED: {op?.Message}", StrategyLoggingLevel.Error);
                    return;
                }
                _pendingEntryTime = ExchangeNow();
                Log($"ENTRY {side} x{qty} limit@{entry} stop@{stop} tgt@{target} "
                    + $"R:R {sig.Rrr:F1} loc={sig.LocationDesc} reads=[{string.Join(",", sig.Reads)}]",
                    StrategyLoggingLevel.Trading);
            }
            catch (Exception ex) { Log("PlaceBracket: " + ex.Message, StrategyLoggingLevel.Error); }
        }

        // ===============================================================================
        // Realized P&L intake — the governor learns the outcome and may lock the day out.
        // ===============================================================================
        private void OnPositionRemoved(Position pos)
        {
            if (_gov == null || pos == null) return;
            if (pos.Symbol != Symbol || pos.Account != Account) return;

            double realized = ClosedPositionRealizedPnL(pos);   // >>> VERIFY vX: net vs gross <<<
            _gov.OnTradeClosed(realized);
            Log($"CLOSED pnl ${realized:N2}. Day ${_gov.DayRealizedPnL:N2}, equity ${_gov.RealizedEquity:N0}, "
                + $"floor ${_gov.Floor:N0}.", StrategyLoggingLevel.Trading);

            if (_gov.EvalPassed)
            {
                CancelAllWorkingOrders();
                Log($"*** EVAL PASSED — +${_gov.RealizedEquity - StartBalance:N0}. Stopping. ***",
                    StrategyLoggingLevel.Trading);
                Stop();
                return;
            }
            if (_gov.DayHalted)
            {
                CancelAllWorkingOrders();
                Log($"Day locked out: {_gov.DayHaltReason}. No more trades today.",
                    StrategyLoggingLevel.Trading);
            }
        }

        // ===============================================================================
        // Helpers — SDK-facing. Marked seams need a one-line check per SDK version.
        // ===============================================================================

        private bool HasOpenPosition() => OpenPositions().Any();
        private IEnumerable<Position> OpenPositions() =>
            Core.Instance.Positions.Where(p => p.Symbol == Symbol && p.Account == Account && p.Quantity != 0);

        private bool HasWorkingEntryOrder() =>
            Core.Instance.Orders.Any(o => o.Symbol == Symbol && o.Account == Account &&
                                          o.Status == OrderStatus.Working);

        private double OpenUnrealizedPnL()
        {
            double sum = 0;
            foreach (var p in OpenPositions())
            {
                try { sum += p.GrossPnL.Value; }        // >>> VERIFY vX: GrossPnL vs NetPnL type <<<
                catch { }
            }
            return sum;
        }

        private double ClosedPositionRealizedPnL(Position pos)
        {
            // Prefer the position's realized net P&L; fall back to gross. Both are in
            // account currency. The governor only needs the delta.
            try { return pos.NetPnL.Value; } catch { }
            try { return pos.GrossPnL.Value; } catch { }
            return 0.0;
        }

        private void FlattenAll(string why)
        {
            foreach (var p in OpenPositions().ToList())
            {
                try { p.Close(); }                       // market-close the position
                catch (Exception ex) { Log($"Flatten ({why}) failed: {ex.Message}", StrategyLoggingLevel.Error); }
            }
        }

        private void CancelAllWorkingOrders()
        {
            foreach (var o in Core.Instance.Orders
                         .Where(o => o.Symbol == Symbol && o.Account == Account && o.Status == OrderStatus.Working)
                         .ToList())
            {
                try { o.Cancel(); } catch { }
            }
        }

        // ---- Context / location construction ----

        private SSide ComputeHtfBias()
        {
            // Simple, robust HTF regime proxy: fast vs slow EMA on the context TF.
            // (The spec's full shape-classifier/regime routing is a calibration task;
            //  this is a conservative directional bias that satisfies HTF-is-king gating.)
            if (_contextHd == null || _contextHd.Count < 25) return SSide.None;
            double emaFast = Ema(_contextHd, 9);
            double emaSlow = Ema(_contextHd, 21);
            if (emaFast > emaSlow) return SSide.Long;
            if (emaFast < emaSlow) return SSide.Short;
            return SSide.None;
        }

        private double Ema(HistoricalData hd, int period)
        {
            double k = 2.0 / (period + 1);
            int start = Math.Max(0, hd.Count - period * 3);
            double ema = hd[start][PriceType.Close];
            for (int i = start + 1; i < hd.Count; i++)
                ema = hd[i][PriceType.Close] * k + ema * (1 - k);
            return ema;
        }

        private IReadOnlyList<ProfileLevel> BuildProfileLevels()
        {
            // >>> VERIFY vX: build POC/HVN/LVN/VA from Quantower Volume Analysis. <<<
            // Until wired to the session volume profile, this returns an empty set,
            // which means the SignalEngine's location veto fires and NO trade is taken.
            // That is the SAFE default: the bot does not trade blind to structure.
            // The full VP construction (zero-filled ladder, greedy 70% VA, HVN peak-
            // prominence, LVN valleys) is specified in ORDERFLOW_SPEC.md Tools 10-13 and
            // is the primary remaining wiring task before the signal side goes live.
            return Array.Empty<ProfileLevel>();
        }

        private bool TryBuildFootprint(int barIndex, out FootprintBar fp)
        {
            fp = null;
            try
            {
                var item = _decisionHd[barIndex] as HistoryItemBar;
                if (item == null) return false;

                // >>> VERIFY vX: VolumeAnalysisData per-price levels access. <<<
                // Quantower attaches VolumeAnalysisData to each bar once volume analysis
                // is calculated (RequestVolumeAnalysis). PriceLevels expose BuyVolume /
                // SellVolume per price. If unavailable, return false -> no signal (safe).
                var va = item.VolumeAnalysisData;
                if (va == null || va.PriceLevels == null || va.PriceLevels.Count == 0) return false;

                fp = new FootprintBar
                {
                    Time = item.TimeLeft,
                    Open = item[PriceType.Open], High = item[PriceType.High],
                    Low = item[PriceType.Low], Close = item[PriceType.Close]
                };
                foreach (var kv in va.PriceLevels.OrderBy(p => p.Key))
                {
                    fp.Cells.Add(new FootprintCell
                    {
                        Price = kv.Key,
                        Buy = kv.Value.BuyVolume,
                        Sell = kv.Value.SellVolume
                    });
                }
                return fp.Cells.Count > 0;
            }
            catch { return false; }
        }

        private void RequestVolumeAnalysis(HistoricalData hd)
        {
            try
            {
                // >>> VERIFY vX: exact call to trigger per-bar volume analysis on history. <<<
                Core.Instance.VolumeAnalysis.CalculateProfile(hd);
            }
            catch (Exception ex) { Log("VolumeAnalysis request: " + ex.Message, StrategyLoggingLevel.Error); }
        }

        // ---- Session / time ----

        private DateTime ExchangeNow()
        {
            // Prefer the symbol's exchange/session clock; fall back to platform time.
            try { return Core.Instance.TimeUtc.ToLocalTime(); } catch { return DateTime.Now; }
        }

        private bool IsInSession(DateTime now)
        {
            var start = new TimeSpan(SessionStartHour, SessionStartMinute, 0);
            var flat = new TimeSpan(FlatByHour, FlatByMinute, 0);
            var t = now.TimeOfDay;
            return t >= start && t < flat;
        }

        private bool IsPastFlatBy(DateTime now) =>
            now.TimeOfDay >= new TimeSpan(FlatByHour, FlatByMinute, 0);

        private double ResolvePointValue(Symbol s)
        {
            // MNQ = $2/point. Prefer the SDK's contract economics when present.
            try
            {
                // >>> VERIFY vX: Symbol.TickCost / TickSize gives $/point = TickCost/TickSize <<<
                if (s.TickSize > 0 && s.TickCost > 0) return s.TickCost / s.TickSize;
            }
            catch { }
            return 2.0;
        }

        // NB: Strategy base class already provides Log(string, StrategyLoggingLevel).
    }
}
