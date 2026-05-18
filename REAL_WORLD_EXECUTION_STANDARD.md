# REAL-WORLD EXECUTION STANDARD (RWES)
**Author:** Christopher (Nfifty4) + Christopher's Claude (Sonnet 4.6)
**Date:** 2026-05-18
**Status:** MANDATORY — No bot advances to live execution without passing this standard
**Applies to:** All bots, all variants, all tournament participants

---

## PURPOSE

Backtests run in a clean, idealized environment. Live CME futures trading does not.
The gap between backtest P&L and live P&L is real, documented, and has already cost
Christopher real money on V28801's settings. This document defines the minimum
real-world execution adjustments every bot must survive before being considered
for live deployment on Lucid Trading accounts.

A bot that cannot pass these adjustments with positive expectancy is not ready.
Tournament rank means nothing if the bot bleeds in live conditions.

---

## THE FIVE REAL-WORLD FACTORS (Non-Negotiable)

---

### FACTOR 1 — STOP ORDER SLIPPAGE

**What it is:**
Stop orders on CME Globex convert to market orders on trigger. They do not guarantee
a fill at the stop price. In fast markets, fills occur at whatever the next available
bid/ask is — which is always at or worse than the stop level, never better.

**Mandatory backtest adjustment:**
- Normal market conditions: add 1–2 ticks slippage to every stop exit
- Elevated volatility (VIX 18–25): add 2–3 ticks slippage to every stop exit
- Extreme volatility (VIX > 25): add 3–5 ticks slippage to every stop exit

**Tick values for reference:**
| Contract | Tick Value | 3-tick slippage cost |
|----------|-----------|----------------------|
| MNQ      | $0.50     | $1.50/contract       |
| MES      | $1.25     | $3.75/contract       |
| NQ       | $5.00     | $15.00/contract      |
| ES       | $12.50    | $37.50/contract      |
| CL       | $10.00    | $30.00/contract      |
| GC       | $10.00    | $30.00/contract      |

**Bot requirement:**
Every stop loss simulation must apply the slippage adjustment above.
A win in the backtest that only survives with exact-price stop fill is a phantom win.

---

### FACTOR 2 — CME VELOCITY LOGIC EVENTS

**What it is:**
CME Velocity Logic (VL) is an exchange-level circuit breaker that triggers when price
moves too FAST relative to predefined speed thresholds. When triggered:
- CME enters a "Reserved state" — order matching is paused for seconds
- Stop orders in queue DO NOT execute at their trigger price
- When market reopens, fills occur at the Indicative Opening Price (IOP)
- The IOP can be 5–15+ ticks from the intended stop price
- Standard OHLC bar data makes VL halts invisible — the bar just shows a gap

**Mandatory backtest adjustment:**
- Identify all bars in the test window where high-to-low range exceeds 2× the
  10-bar ATR on the same timeframe — these are VL candidates
- For any trade that has a stop exit on a VL-candidate bar: add 5–10 ticks
  additional slippage to the fill price
- If a bot's win rate collapses when VL adjustments are applied, the bot is
  not viable for live trading during volatile sessions

**Bot requirement:**
VL events cannot be backtested away. Any bot that generates significant wins during
VL-candidate bars — and those wins depend on exact stop fills — is showing phantom P&L.

---

### FACTOR 3 — NEWS EVENT CONTAMINATION

**What it is:**
Lucid Trading permits news trading. However, Tier-1 news releases (CPI, FOMC, NFP,
PPI, PCE, GDP) cause price to move 5–20+ ticks in under one second. Stop orders
near a news event can fill dramatically worse than intended. OCO brackets can fire
both legs before cancellation propagates.

**Mandatory backtest adjustment:**
- Flag every trade with entry or stop exit within ±5 minutes of a Tier-1 release
- Apply 3–8 tick additional slippage to stop exits on flagged trades
- If a flagged trade shows a "win" that survives only with exact stop fill: mark it
  as a likely real-world loss and exclude from win rate calculation

**Tier-1 news events (mandatory filter):**
CPI, Core CPI, FOMC Rate Decision, FOMC Minutes, NFP (Non-Farm Payrolls),
PPI, Core PCE, GDP (advance/preliminary), ISM Manufacturing, JOLTS

**Bot requirement:**
Every bot must have a news-adjacent trade filter applied before win rate is reported.
The "raw" and "news-filtered" win rates must both be disclosed.

---

### FACTOR 4 — MINIMUM STOP BUFFER REQUIREMENT

**What it is:**
A stop buffer (in ticks) beyond the far wick provides two functions:
1. Structural: ensures SL is beyond the zone's natural price range
2. Execution: absorbs real-world stop fill slippage so the fill still lands
   outside the zone even after slippage degrades the price

Christopher's live results confirm: 2-tick buffer = profitable. V28801 settings
(unknown buffer, suspected 0–1 tick) = all 4 trades blown through.

**Mandatory parameter floor:**
- Minimum stop buffer: 2 ticks — hard floor, no exceptions
- No bot advances to live deployment with a stop buffer of 0 or 1 tick
- Recommended range: 2–4 ticks depending on contract and volatility regime

**Bot requirement:**
Stop buffer must be reported as a parameter for every bot variant.
Any variant with buffer < 2 ticks is disqualified from live deployment regardless
of backtest win rate.

---

### FACTOR 5 — LUCID DRAWDOWN INTERACTION

**What it is:**
Lucid Trading enforces real-time drawdown limits that interact with slippage in
a compounding way:

- **LucidPro:** Fixed Daily Loss Limit (DLL). When breached, force-liquidation
  fires as a market order. If the market is fast at that moment, the fill can be
  $100–$200 worse than the DLL threshold. The trader's account reflects the
  worse fill, not the DLL trigger price.
- **LucidFlex:** End-of-Day Maximum Loss Limit (MLL). Thin close-of-session
  liquidity (4:30–4:45 PM EST) means wider spreads and worse fills on any
  forced close near the MLL.

**Mandatory simulation requirement:**
- All bot P&L simulations must be run against Christopher's actual Lucid account
  parameters: $2k daily drawdown, $3k profit target, 90/10 split
- A bot that shows profitable backtest P&L but routinely touches within $200
  of the DLL intraday is a real-world risk — a single slippage event can
  breach the account
- Bots must maintain an intraday drawdown buffer of at least $300 below DLL
  to absorb realistic stop slippage on the worst-case trade

---

### FACTOR 6 — INSTRUMENT LIQUIDITY: MANDATORY MES PARALLEL TEST

**What it is:**
The bot tournament runs on MNQ. MNQ is the thinnest book of the primary test
instruments. ES trades 1–1.5 million contracts per day. NQ trades 300–500k.
MNQ is a fraction of that. Thinner books mean:
- More noise-driven wick penetrations on lower timeframes
- Stops fill further past trigger price during fast moves
- IOF base formations less reliably represent genuine institutional orders

ES/MES has 3–4× more liquidity depth. IOF zones on ES/MES are backed by deeper
institutional order flow — which is the entire premise of the strategy.

**Mandatory parallel test:**
Every bot that passes all other RWES criteria must also be run on MES for the
same date range and compared against its MNQ results.

| Metric                        | MNQ | MES |
|-------------------------------|-----|-----|
| Raw win rate                  | ?   | ?   |
| RWES-adjusted win rate        | ?   | ?   |
| Zone hold rate (no stop-out)  | ?   | ?   |
| Average stop fill slippage    | ?   | ?   |
| Profit factor (slippage adj.) | ?   | ?   |
| Worst intraday drawdown       | ?   | ?   |

**Deployment decision:**
- If MES win rate and zone hold rate are meaningfully higher → primary live
  instrument is MES, MNQ is secondary/stress-test only
- If results are equivalent → trader's discretion
- MNQ-only results are never sufficient for live deployment approval

**Volume reference:**
| Contract | Daily Volume    | Tick Value | Liquidity Profile |
|----------|----------------|-----------|-------------------|
| ES       | 1–1.5M contracts | $12.50   | Deepest — institutional standard |
| MES      | 300–500k       | $1.25     | Mirrors ES, same zone reliability |
| NQ       | 300–500k       | $5.00     | Moderate |
| MNQ      | 100–200k       | $0.50     | Thinnest — highest noise risk |

---

## PASS/FAIL CRITERIA

A bot PASSES the Real-World Execution Standard if:

| Test                                   | Requirement                                        |
|----------------------------------------|----------------------------------------------------|
| Stop slippage adjusted win rate        | ≥ 60% after 1–2 tick slippage applied              |
| News-filtered win rate                 | ≥ 55% after news trades adjusted                   |
| VL-adjusted win rate                   | Does not collapse vs. raw win rate                 |
| Stop buffer                            | ≥ 2 ticks — hard requirement                      |
| SL placement                           | Far wick of base candle cluster, always            |
| Wick SL hit = loss                     | Enforced in code — no re-entry after wick stop-out |
| One touch only                         | Enforced in code — zone burned after first touch   |
| Counter-trend target                   | 1:1 R:R ONLY — no TP2/TP3 on CT trades            |
| Intraday DLL buffer                    | ≥ $300 below DLL on worst-case day                |
| Profit factor (slippage adjusted)      | ≥ 1.5                                              |
| MES parallel test                      | Completed and results documented                   |

A bot FAILS if any single criterion is not met. There is no partial pass.

---

## WHAT THIS MEANS FOR V28801

V28801 reported: 186 trades, 163 wins, 8 losses, 15 drift — 87.6% raw win rate.

Before V28801 can be reconsidered for live use, it must pass:
1. Stop slippage adjustment — recalculate every trade with 1–3 tick stop fill cost
2. News contamination filter — flag and adjust trades near Tier-1 releases
3. VL event check — identify VL-candidate bars and apply additional slippage
4. Wick SL audit — confirm no wick stop-outs were misclassified as drift or re-entries
5. Counter-trend cap — cap all CT trades at 1:1, recalculate P&L
6. Stop buffer confirmation — what was V28801's buffer in ticks?

The 87.6% raw win rate is a starting point for the audit, not a verdict.

---

## EFFECTIVE DATE

This standard is effective immediately — 2026-05-18.
It applies retroactively to all existing bot variants including V28801.
No bot result from before this date is considered valid for live deployment
without passing this standard.

All new bots developed post-2026-05-18 must be built to pass this standard
from day one — it is not an afterthought.

---

**Signed:**
Christopher (Nfifty4) — Lucid Trading, 5×$50k accounts
Christopher's Claude (Sonnet 4.6) — system architect

Brandon's Claude (Opus 4.7) — acknowledgment required before tournament kickoff
Brandon — acknowledgment required before tournament kickoff
