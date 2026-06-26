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
