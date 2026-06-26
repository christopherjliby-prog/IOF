// RiskGovernor.cs — the survival layer.
//
// The backtest can say "0 blows / 100% pass" and still be a fantasy, because it
// takes every signal with no human circuit breakers. The Risk Governor applies the
// SAME hard rules the senior-trader-master-reference screams about — and applies them
// identically in backtest and live:
//
//   • max N losing trades per day  -> hard lockout for the rest of the day
//   • daily loss cap ($)           -> hard lockout
//   • daily profit lock ($)        -> stop giving back a green day
//   • per-trade risk cap ($)       -> skip a trade whose stop is too wide (can't size
//                                     down on 1 contract)
//   • one position at a time
//   • optional session window      -> no lunch chop / after-hours shenanigans
//   • prop trailing drawdown (EOD) -> the account-killer we must never hit
//
// Run over the MFE-based SimTrade book so any take-profit R:R stays evaluable.
// A trade "wins at rr" if its max-favorable-excursion reached rr before the stop.

using System;
using System.Collections.Generic;

namespace TradePhantomsIOF.Trend
{
    public class RiskRules
    {
        public double PointValue          = 20;     // NQ=20, MNQ=2, ES=50, MES=1.25
        public int    MaxContracts        = 1;       // micros (MNQ) size up to here to keep risk at cap
        public double MaxRiskPerTradeDollars = 300;  // per-trade risk budget ($) — sizing targets this
        public int    MaxLossesPerDay     = 2;       // 0 = off
        public double DailyLossCapDollars = 600;     // 0 = off
        public double DailyProfitLockDollars = 0;    // 0 = off
        public double AccountTrailingDD   = 2000;    // prop trailing drawdown (eval phase)
        public double ProfitTarget        = 3000;    // profit to PASS the eval ($53k)
        public double StartBalance        = 50000;   // account start ($50k)
        public double FundedFloor         = 52000;   // funded phase: floor LOCKS here (blow if hit)
        public double CostPerTrade        = 15;      // commission round-turn ($)
        public double TpRR                = 2.0;     // take-profit R:R

        // ── Realistic fills (a bot takes every trade; price them honestly) ──
        public double TickSize            = 0.25;    // instrument tick
        public double SlipTicksEntry      = 1;       // adverse fill getting IN (every trade)
        public double SlipTicksStop       = 2;       // extra slip blowing THROUGH a stop (losses)

        // Breakeven: once a trade reaches this fraction of the way to target, the stop moves
        // to entry — a winner that reverses scratches at $0 instead of paying a full -1R.
        // 0 = off. 0.5 = move to BE at 50% to target (the IOF doc's rule).
        public double BreakevenAtPct      = 0.5;

        // Session filter (minutes from UTC midnight). EnforceSession=false => trade all hours.
        public bool   EnforceSession      = false;
        public int    SessionStartMinUtc  = 0;
        public int    SessionEndMinUtc    = 1440;

        // Account starts here. Trades before this are ignored for balance/days/pass.
        // DateTime.MinValue = count all history (pure backtest).
        public DateTime EvalStartUtc      = DateTime.MinValue;

        // Futures session/day boundary (the eval "day" rolls here, NOT calendar midnight).
        // 22 = 3pm PT / 6pm ET open during PDT. Lockouts reset + EOD blow checked at this roll.
        public int    SessionRolloverHourUtc = 22;
    }

    // Live, right-now status for the dashboard / a future execution layer.
    public class GovernorLiveState
    {
        public string  Status      = "ARMED";   // ARMED | LOCKED_LOSSES | LOCKED_LOSS_CAP | LOCKED_PROFIT | OUT_OF_SESSION | BLOWN | PASSED
        public bool    InSession   = true;
        public bool    LockedToday = false;
        public int     LossesToday;
        public double  PnlToday;
        public double  Equity;                   // running PROFIT $ (balance - start) at end of replay
        public bool    CanTrade => Status == "ARMED";

        // ── Prop account state (the live dashboard core) ──
        public double  Balance;                  // start + equity
        public double  Peak;                     // highest balance reached
        public double  Floor;                    // trailing-locked blow level = min(lock, peak - DD)
        public double  RoomToFloor;              // balance - floor  (how much you can lose before blow)
        public double  ToTarget;                 // target balance - balance (eval), 0 once passed
        public string  Phase       = "EVAL";     // EVAL | FUNDED | BLOWN
        public bool    Passed       = false;     // reached the eval target
    }

    public class GovernedResult
    {
        public double FinalDollars;
        public bool   Passed;             // hit profit target
        public bool   Blown;              // hit prop EOD trailing drawdown
        public int    Trades;
        public int    Skipped;            // risk-cap skips (stop too wide)
        public int    BlockedByGuard;     // signals blocked by lockout / session
        public int    LockoutDays;        // days a hard stop fired
        public double WorstEodDD;
        public int    LongestLossStreak;
        public int    Wins, Losses, Scratches;   // Scratches = breakeven exits (0R)
        public double WinRate => (Wins + Losses) > 0 ? (double)Wins / (Wins + Losses) : 0;
        public double AvgR;               // realized expectancy per taken trade (R)
        public List<double> EquityCurve = new List<double>();   // running balance after each taken trade
        public int    EvalPasses;         // total evals that hit the target across the run (spin-up pool)
        public int    EvalsRun = 1;       // how many fresh evals were started (1 + lockouts)
        // ── Phase 2 (live funded lifecycle): payouts → live graduation ──
        public int    LivePayouts;        // $1,500 payouts banked on this account across its funded cycles
        public double LiveWithdrawn;      // total $ withdrawn
        public int    LiveGraduated;      // funded accounts that hit 5 payouts → moved to a live account

        // ── Throughput: how fast does this actually pass / earn? ──
        public int    DaysToPass;         // trading days from start until the eval passed (0 = not yet)
        public int    TradesToPass;       // trades taken before passing
        public int    TradingDays;        // distinct days with activity over the whole run
        public double AvgPerDay;          // FinalDollars / TradingDays

        public GovernorLiveState Live = new GovernorLiveState();
    }

    // One account inside the pool — runs eval → funded → payouts → live.
    public class PoolAccount
    {
        public double Equity;        // profit from 0 (balance = start + equity)
        public int    LossesToday;
        public double PnlToday;
        public bool   LockedToday;
        public bool   Done;          // retired: blown, or graduated to live after 5 payouts
        public bool   Passed;        // cleared the eval (now a funded account)
        public int    LastDay = -1;
        public DateTime FreeAt = DateTime.MinValue;
        public int    Phase;         // 0 = eval, 1 = funded
        public double EodHigh;       // highest END-OF-DAY equity — funded trailing drawdown
        public int    Payouts;       // $1,500 payouts taken (5 → live)
    }

    // Result of the account-pool projection: run the strategy across a POOL of evals where a
    // fresh account spins up whenever every active one is locked, so trading never sits idle.
    public class PoolResult
    {
        public int    Passes;        // accounts that cleared the eval ($53k) → funded
        public int    Blows;         // accounts that hit the floor — ANY blow = goal failed
        public int    AccountsUsed;  // total evals spun up
        public int    InProgress;    // still climbing at the end
        public int    TradingDays;
        public bool   GoalMet;       // hit the pass target with zero blows
        public int    GoalTarget = 30;
        // ── Phase 2: funded → payouts → live ──
        public int    Payouts;       // total $1,500 payouts banked
        public double Withdrawn;     // total $ withdrawn
        public int    LiveAccounts;  // funded accounts that hit 5 payouts → graduated to live
    }

    public static class RiskGovernor
    {
        private static readonly double[] RrLadder = { 1.0, 1.5, 2.0, 2.5, 3.0, 4.0 };

        private static double OutcomeAt(SimTrade st, double rr, double beR)
        {
            // 3-LEG SCALE-OUT with trailing (BLENDED R per contract — chart and account agree).
            // leg1 banks +1 third, leg2 +2 thirds, leg3 (RUNNER) the zone target (capped 6R). Stop
            // on the open legs trails up: → breakeven after the 1st third, → +1 third after the 2nd.
            double third  = rr / 3.0;
            double runner = st.TargetR > 0 ? Math.Min(st.TargetR, 6.0) : rr;
            if (runner < 2 * third) runner = 2 * third;
            double m = st.MfeR;
            if (m >= runner) return (third + 2 * third + runner) / 3.0;
            if (st.Stopped)
            {
                double l1 = m >= third     ? third     : -1.0;
                double l2 = m >= 2 * third ? 2 * third : (m >= third ? 0.0 : -1.0);
                double l3 = m >= 2 * third ? third     : (m >= third ? 0.0 : -1.0);
                return (l1 + l2 + l3) / 3.0;
            }
            return double.NaN;                                    // still open — exclude
        }

        private static DateTime ExitTime(SimTrade st, double rr)
        {
            if (st.MfeR >= rr)
            {
                for (int k = 0; k < RrLadder.Length; k++)
                    if (RrLadder[k] >= rr - 1e-9 && st.RrHit[k] != DateTime.MinValue) return st.RrHit[k];
                if (st.StopTime != DateTime.MinValue) return st.StopTime;
            }
            if (st.Stopped) return st.StopTime;
            return DateTime.MaxValue;
        }

        // Session-aware day: the futures eval "day" rolls at the session open (default 22:00
        // UTC = 3pm PT), not calendar midnight. Shift back by the rollover hour, then bucket.
        private static int DayKey(DateTime t, int rollHourUtc)
        {
            var s = t.AddHours(-rollHourUtc);
            return s.Year * 1000 + s.DayOfYear;
        }
        private static int MinOfDay(DateTime t) => t.Hour * 60 + t.Minute;

        // Replay the trade book under the governor. nowUtc = current clock for the live
        // session check (pass DateTime.UtcNow live; the last day's counters become Live state).
        public static GovernedResult RunGoverned(IReadOnlyList<SimTrade> trades, RiskRules r, DateTime nowUtc)
        {
            var res = new GovernedResult();
            if (trades == null || trades.Count == 0) return res;

            // chronological by entry
            var book = new List<SimTrade>(trades);
            book.Sort((a, b) => a.EntryTime.CompareTo(b.EntryTime));

            double equity = 0, eodHWM = 0, maxEquity = 0;
            int curDay = -1, streak = 0, dayCount = 0;
            int lossesToday = 0; double pnlToday = 0; bool lockedToday = false;
            bool passed = false;
            double sumR = 0;
            DateTime freeAt = DateTime.MinValue;
            double fundedEodHigh = 0;   // highest EOD equity since funding — funded trailing drawdown
            int    payouts = 0;          // payouts on the CURRENT funded account (5 → graduate to live)

            double evalFloorEq   = -r.AccountTrailingDD;           // eval floor ($48k), in equity terms
            double fundedFloorEq = r.FundedFloor - r.StartBalance; // funded floor ($52k)
            double passEq        = r.ProfitTarget;                 // pass at +$3k ($53k)

            double tickVal   = r.TickSize * r.PointValue;          // $ per tick
            double slipEntry = r.SlipTicksEntry * tickVal;         // adverse fill getting in
            double slipStop  = r.SlipTicksStop  * tickVal;         // extra slip through a stop
            double beR       = r.BreakevenAtPct > 0 ? r.BreakevenAtPct * r.TpRR : 0;  // BE trigger (R)

            // EOD account: blow is checked on END-OF-DAY balance against a FIXED floor
            // (eval floor until passed, then the locked funded floor). NO trailing.
            void CloseDay()
            {
                if (equity > eodHWM) eodHWM = equity;
                double give = eodHWM - equity;
                if (give > res.WorstEodDD) res.WorstEodDD = give;   // informational give-back
                if (passed)   // FUNDED: EOD trailing $2k that locks at the $50k start (equity 0)
                {
                    double floorEq = Math.Min(fundedEodHigh - 2000.0, 0.0);   // floor from prior EOD highs
                    if (equity <= floorEq) res.Blown = true;
                    else if (equity > fundedEodHigh) fundedEodHigh = equity;   // ratchet up for next day
                }
                else          // EVAL: static $2k floor ($48k)
                {
                    if (equity <= evalFloorEq) res.Blown = true;
                }
            }

            foreach (var st in book)
            {
                if (res.Blown) break;
                if (st.EntryTime < r.EvalStartUtc) continue;     // account starts at the eval date
                if (st.EntryTime < freeAt) continue;             // one position at a time

                int day = DayKey(st.EntryTime, r.SessionRolloverHourUtc);
                if (curDay < 0) { curDay = day; dayCount = 1; }
                if (day != curDay)                               // new day boundary → close prior day
                {
                    CloseDay();
                    if (res.Blown) break;
                    curDay = day; dayCount++; lossesToday = 0; pnlToday = 0; lockedToday = false;
                }

                if (r.EnforceSession)
                {
                    int m = MinOfDay(st.EntryTime);
                    bool inSess = r.SessionStartMinUtc <= r.SessionEndMinUtc
                        ? (m >= r.SessionStartMinUtc && m < r.SessionEndMinUtc)
                        : (m >= r.SessionStartMinUtc || m < r.SessionEndMinUtc);   // wraps midnight
                    if (!inSess) { res.BlockedByGuard++; continue; }
                }

                if (lockedToday) { res.BlockedByGuard++; continue; }

                double riskPer1 = st.RiskPts * r.PointValue;     // risk of ONE contract
                if (riskPer1 <= 0) continue;

                // Per-trade risk budget — SIZE DOWN within $150 of passing so a small win locks the
                // pass while a loss barely dents the lead (de-risks the final push, and fewer
                // contracts = less commission drag on the tiny pass-securing gain).
                double gapToPass  = passEq - equity;
                bool   nearPass   = !passed && gapToPass > 0 && gapToPass < 150;
                double riskBudget = nearPass
                    ? Math.Min(r.MaxRiskPerTradeDollars, Math.Max(25.0, gapToPass * 2.0))
                    : r.MaxRiskPerTradeDollars;

                int maxC = Math.Max(1, r.MaxContracts);
                int contracts = riskPer1 > 0 ? (int)Math.Floor(riskBudget / riskPer1) : maxC;
                contracts = Math.Min(contracts, maxC);
                if (contracts < 1) { res.Skipped++; continue; } // even 1 contract risks over the cap → skip
                double riskD = riskPer1 * contracts;
                double winCosts = (r.CostPerTrade + slipEntry) * contracts;   // commission + entry slip on a win

                // Pass-securing exit: within $150 of passing, close the open trade exactly where it
                // NETS the pass — cover the gap PLUS costs, or commission eats the tiny gain and the
                // account stalls forever just under $53k. Otherwise resolve at the scale-out exit.
                double oc;
                if (nearPass && riskD > 0)
                {
                    double rrNeeded = (gapToPass + winCosts) / riskD;   // gross R that NETS the pass
                    if (rrNeeded > 0 && st.MfeR >= rrNeeded) oc = rrNeeded;
                    else { oc = OutcomeAt(st, r.TpRR, beR); if (double.IsNaN(oc)) continue; }
                }
                else { oc = OutcomeAt(st, r.TpRR, beR); if (double.IsNaN(oc)) continue; }

                // Realistic fill + per-contract costs, all scaled by the sized contract count.
                double pnl = oc * riskD - (r.CostPerTrade + slipEntry + (oc < 0 ? slipStop : 0)) * contracts;
                equity   += pnl;
                pnlToday += pnl;
                sumR     += oc;
                res.Trades++;
                res.EquityCurve.Add(r.StartBalance + equity);    // running balance for the equity chart
                if (equity > maxEquity) maxEquity = equity;
                freeAt = ExitTime(st, r.TpRR);

                if (oc < 0)      { res.Losses++; lossesToday++; streak++; if (streak > res.LongestLossStreak) res.LongestLossStreak = streak; }
                else if (oc > 0) { res.Wins++; streak = 0; }
                else             { res.Scratches++; streak = 0; }   // breakeven scratch — neither win nor loss

                // Pass the eval the first time realized profit hits the target → become a FUNDED
                // account, FRESH $50k (the eval profit doesn't carry), funded rules now apply.
                if (!passed && r.ProfitTarget > 0 && equity >= passEq)
                {
                    passed = true; res.Passed = true; res.EvalPasses++;
                    res.DaysToPass = dayCount; res.TradesToPass = res.Trades;
                    equity = 0; eodHWM = 0; fundedEodHigh = 0; payouts = 0;   // fresh $50k funded
                    res.EquityCurve.Add(r.StartBalance);                       // mark the funding reset
                }
                // FUNDED PAYOUT: reach $53.5k (equity +3,500) → withdraw $1,500 → $52k. 5 → live, then
                // a fresh eval spins up (you'd start a new challenge after graduating one to live).
                else if (passed && equity >= 3500.0)
                {
                    equity -= 1500.0; payouts++;
                    res.LivePayouts++; res.LiveWithdrawn += 1500.0;
                    if (equity > fundedEodHigh) fundedEodHigh = equity;
                    res.EquityCurve.Add(r.StartBalance + equity);
                    if (payouts >= 5)   // graduated → live account; spin a fresh eval to keep going
                    {
                        res.LiveGraduated++; res.EvalsRun++;
                        equity = 0; eodHWM = 0; maxEquity = 0; fundedEodHigh = 0; payouts = 0;
                        lossesToday = 0; pnlToday = 0; streak = 0; passed = false; lockedToday = false;
                        res.Wins = 0; res.Losses = 0; res.Scratches = 0; res.Trades = 0; sumR = 0;
                        res.DaysToPass = 0; res.TradesToPass = 0; res.EquityCurve.Clear();
                    }
                }

                // hard intraday circuit breakers — checked AFTER the trade resolves
                bool lockNow = false;
                if (r.MaxLossesPerDay > 0 && lossesToday >= r.MaxLossesPerDay)          lockNow = true;
                if (r.DailyLossCapDollars > 0 && pnlToday <= -r.DailyLossCapDollars)     lockNow = true;
                if (r.DailyProfitLockDollars > 0 && pnlToday >= r.DailyProfitLockDollars) lockNow = true;
                // AUTO-POPULATE A FRESH EVAL on lockout (user rule). Each eval is its OWN $50k
                // account, so it resets FULLY — balance AND win/loss AND equity curve — so a fresh
                // eval reads cleanly ($50k, 0-0), never the old "12-6 but $50k" mismatch. Only the
                // cross-eval tallies persist: EvalsRun (attempts), EvalPasses (passes), lockouts.
                if (lockNow)
                {
                    res.LockoutDays++;
                    res.EvalsRun++;
                    equity = 0; eodHWM = 0; maxEquity = 0;
                    lossesToday = 0; pnlToday = 0; streak = 0;
                    passed = false; lockedToday = false;
                    res.Wins = 0; res.Losses = 0; res.Scratches = 0; res.Trades = 0; sumR = 0;
                    res.DaysToPass = 0; res.TradesToPass = 0;
                    res.EquityCurve.Clear();
                }
            }

            if (!res.Blown) CloseDay();                          // finalize last day

            res.FinalDollars = equity;
            res.AvgR = res.Trades > 0 ? sumR / res.Trades : 0;
            res.TradingDays = dayCount;
            res.AvgPerDay = dayCount > 0 ? equity / dayCount : 0;

            // ── Live prop-account state (the dashboard core) ──
            var live = res.Live;
            double start = r.StartBalance;
            double curFloorEq = passed ? Math.Min(fundedEodHigh - 2000.0, 0.0) : evalFloorEq;
            live.Equity      = equity;
            live.Balance     = start + equity;
            live.Peak        = start + maxEquity;
            live.Floor       = start + curFloorEq;
            live.RoomToFloor = live.Balance - live.Floor;
            live.ToTarget    = passed ? 0 : Math.Max(0, (start + passEq) - live.Balance);
            live.Passed      = passed;
            live.Phase       = res.Blown ? "BLOWN" : passed ? "FUNDED" : "EVAL";
            live.LossesToday = lossesToday;
            live.PnlToday    = pnlToday;
            live.LockedToday = lockedToday;
            live.InSession   = true;
            if (r.EnforceSession)
            {
                int m = MinOfDay(nowUtc);
                live.InSession = r.SessionStartMinUtc <= r.SessionEndMinUtc
                    ? (m >= r.SessionStartMinUtc && m < r.SessionEndMinUtc)
                    : (m >= r.SessionStartMinUtc || m < r.SessionEndMinUtc);
            }

            if      (res.Blown)                                   live.Status = "BLOWN";
            else if (!live.InSession)                             live.Status = "OUT_OF_SESSION";
            else if (lockedToday && r.MaxLossesPerDay > 0 && lossesToday >= r.MaxLossesPerDay) live.Status = "LOCKED_LOSSES";
            else if (lockedToday && r.DailyLossCapDollars > 0 && pnlToday <= -r.DailyLossCapDollars) live.Status = "LOCKED_LOSS_CAP";
            else if (lockedToday)                                 live.Status = "LOCKED_PROFIT";
            else                                                  live.Status = "ARMED";

            return res;
        }

        // ── Account-pool projection ───────────────────────────────────────────
        // Run the strategy across a POOL of $50k evals. Each trade goes to the best available
        // (un-locked, un-done, free) account — when EVERY account is locked, a fresh one spins
        // up so trading never sits idle. Counts passes toward the goal; ANY blow fails it.
        // Runs over the FULL trade history (a projection of how the goal would play out).
        // fromUtc = DateTime.MinValue → full-history projection; pass the pinned eval start
        // for the LIVE pool (spin-up-on-lockout from today forward).
        public static PoolResult RunEvalPool(IReadOnlyList<SimTrade> trades, RiskRules r, int goal, DateTime fromUtc)
        {
            var res = new PoolResult { GoalTarget = goal };
            if (trades == null || trades.Count == 0) return res;

            var book = new List<SimTrade>(trades);
            book.Sort((a, b) => a.EntryTime.CompareTo(b.EntryTime));

            double tickVal   = r.TickSize * r.PointValue;
            double slipEntry = r.SlipTicksEntry * tickVal;
            double slipStop  = r.SlipTicksStop  * tickVal;
            double beR       = r.BreakevenAtPct > 0 ? r.BreakevenAtPct * r.TpRR : 0;
            double evalFloorEq   = -r.AccountTrailingDD;
            double fundedFloorEq = r.FundedFloor - r.StartBalance;
            double passEq        = r.ProfitTarget;
            int    maxC          = Math.Max(1, r.MaxContracts);

            var accounts = new List<PoolAccount>();
            var days = new HashSet<int>();

            void RollDay(PoolAccount a, int day)
            {
                if (a.LastDay == day) return;
                if (a.LastDay >= 0 && !a.Done)                       // EOD check for the day that ended
                {
                    if (a.Phase == 1)   // FUNDED: EOD trailing $2k that locks at the $50k start (equity 0)
                    {
                        double floorEq = Math.Min(a.EodHigh - 2000.0, 0.0);
                        if (a.Equity <= floorEq) { a.Done = true; res.Blows++; }
                        else if (a.Equity > a.EodHigh) a.EodHigh = a.Equity;   // ratchet the EOD high up
                    }
                    else                 // EVAL: static $2k floor ($48k)
                    {
                        if (a.Equity <= evalFloorEq) { a.Done = true; res.Blows++; }
                    }
                }
                a.LastDay = day; a.LossesToday = 0; a.PnlToday = 0; a.LockedToday = false;
            }

            foreach (var st in book)
            {
                if (st.EntryTime < fromUtc) continue;            // account-pool starts here
                int day = DayKey(st.EntryTime, r.SessionRolloverHourUtc);
                days.Add(day);
                foreach (var a in accounts) RollDay(a, day);

                double oc = OutcomeAt(st, r.TpRR, beR);
                if (double.IsNaN(oc)) continue;
                double riskPer1 = st.RiskPts * r.PointValue;
                if (riskPer1 <= 0) continue;
                int contracts = r.MaxRiskPerTradeDollars > 0 ? (int)Math.Floor(r.MaxRiskPerTradeDollars / riskPer1) : maxC;
                contracts = Math.Min(contracts, maxC);
                if (contracts < 1) continue;                        // too wide → every account skips
                double riskD = riskPer1 * contracts;
                double pnl   = oc * riskD - (r.CostPerTrade + slipEntry + (oc < 0 ? slipStop : 0)) * contracts;
                DateTime exit = ExitTime(st, r.TpRR);

                // Best available account (highest equity → closest to passing); else spin up.
                PoolAccount pick = null;
                foreach (var a in accounts)
                {
                    if (a.Done || a.LockedToday || st.EntryTime < a.FreeAt) continue;
                    if (pick == null || a.Equity > pick.Equity) pick = a;
                }
                if (pick == null)
                {
                    if (accounts.Count >= 2000) continue;           // safety cap
                    pick = new PoolAccount { LastDay = day };
                    accounts.Add(pick);
                    res.AccountsUsed++;
                }

                // Pass-securing exit (pool): close exactly where the account NETS the pass —
                // cover the gap PLUS costs, or commission eats the tiny gain and it stalls under $53k.
                if (!pick.Passed && passEq > pick.Equity && riskD > 0)
                {
                    double winCosts = (r.CostPerTrade + slipEntry) * contracts;
                    double rrNeeded = (passEq - pick.Equity + winCosts) / riskD;   // gross R that nets the pass
                    if (rrNeeded > 0 && st.MfeR >= rrNeeded)
                    {
                        oc  = rrNeeded;
                        pnl = oc * riskD - winCosts;   // = exactly (passEq − equity) net
                    }
                }

                pick.Equity   += pnl;
                pick.PnlToday += pnl;
                pick.FreeAt    = exit;
                if (oc < 0) pick.LossesToday++;
                if ((r.MaxLossesPerDay > 0 && pick.LossesToday >= r.MaxLossesPerDay) ||
                    (r.DailyLossCapDollars > 0 && pick.PnlToday <= -r.DailyLossCapDollars))
                    pick.LockedToday = true;

                // EVAL CLEARED → becomes a FUNDED account, fresh $50k (the eval profit doesn't carry).
                if (!pick.Passed && pick.Equity >= passEq)
                {
                    pick.Passed = true; res.Passes++;
                    pick.Phase = 1; pick.Equity = 0; pick.EodHigh = 0;
                    pick.LossesToday = 0; pick.PnlToday = 0;
                }
                // FUNDED PAYOUT: hit $53.5k (equity +3,500) → withdraw $1,500 → back to $52k. 5 → live.
                else if (pick.Phase == 1 && !pick.Done && pick.Equity >= 3500.0)
                {
                    pick.Equity -= 1500.0;
                    pick.Payouts++; res.Payouts++; res.Withdrawn += 1500.0;
                    if (pick.Equity > pick.EodHigh) pick.EodHigh = pick.Equity;
                    if (pick.Payouts >= 5) { pick.Done = true; res.LiveAccounts++; }
                }
            }

            foreach (var a in accounts)
            {
                if (a.Done) continue;
                double floorEq = a.Phase == 1 ? Math.Min(a.EodHigh - 2000.0, 0.0) : evalFloorEq;
                if (a.Equity <= floorEq) { a.Done = true; res.Blows++; }
                else res.InProgress++;
            }
            res.TradingDays = days.Count;
            res.GoalMet = res.Passes >= goal && res.Blows == 0;
            return res;
        }
    }
}
