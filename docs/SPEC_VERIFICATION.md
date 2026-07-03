# Spec Verification — IOF Prop-Eval Bot vs. `ORDERFLOW_SPEC.md`

Cross-check of the delivered bot against the on-disk **bot-behaviour spec**
(`ORDERFLOW_SPEC.md`, council-validated 2026-07-02, the most recent methodology on the
Drive) and the prop-firm operating constraints (`Prop Firm Bot Research — MNQ Scalping`).
Every gap is flagged, with severity and where it lives in code.

Legend: ✅ implemented to doctrine · 🟡 partial / placeholder-calibrated · ⛔ not yet wired (gap)

---

## A. The three-legged AND-gate (the spine of the whole spec)

| Doctrine (spec) | Status | Where / note |
|---|---|---|
| CONTEXT → LOCATION → CONFIRMATION order | ✅ | `SignalEngine.Evaluate` runs exactly this order |
| "Without a level, order flow is just noise" — location veto (M1) | ✅ | `Evaluate` returns null if price isn't at a fixed, untested VP level |
| ≥2 **independent** confirmation axes | ✅ | `MinIndependentReads = 2`; reads collected with dedup |
| Dedup {absorption, delta-div, CVD-div}=1 axis; finished-auction independent; delta-flip = trigger | ✅ | absorption/finished/stacked counted separately; flip is a **separate required trigger**, not a vote |
| cbrackn M2 veto — no opposing aggression / no trigger → no trade | ✅ | `DeltaFlip(side)` required before any signal |
| cbrackn M3 veto — insignificant delta/volume → no trade | ✅ | `IsAbsorption` significance floor `|Δ|/vol ≥ 0.30` |
| HTF-is-king (context outranks LTF) | ✅ | counter-HTF trades require an explicit finished+absorption reversal |
| Regime routing (D=fade / P·b·trend=break / ambiguous=NO TRADE) | ⛔ **GAP** | HTF bias is an EMA proxy, not the spec's VP **shape classifier** (D/P/b/double-dist). See §D. |

## B. Location legs (Volume Profile — Tools 10–13)

| Feature | Status | Note |
|---|---|---|
| Entry only at an **untested / naked, fixed** level (first-touch) | ✅ (contract) | `ProfileLevel.Untested && Fixed` gating in `Evaluate` |
| Developing POC/VA gated OUT of entries | ✅ (contract) | `Fixed==false` never passes the location filter |
| POC / HVN near-edge / LVN edge / VA edge construction | ⛔ **GAP (primary)** | `BuildProfileLevels()` returns **empty** until wired to Quantower Volume Analysis. Consequence: **the bot takes no trades** until this is built — a *safe* gap, not a dangerous one. Full algorithm (zero-filled ladder, greedy 70% VA, HVN peak-prominence, LVN valleys) is specified in Tools 10–13 and is the #1 remaining task. |
| Stop behind the **far edge** of the same node; target **before** the next barrier | ✅ | `Evaluate` stop = far edge ± buffer; `NextBarrierTarget` = next level − buffer |

## C. Footprint confirmation reads (Tools 1–9, 14)

| Read | Status | Where |
|---|---|---|
| Imbalance — **resolved diagonal** `BUY=Buy[i]≥r·Sell[i-1]`, `SELL=Sell[i]≥r·Buy[i+1]` | ✅ | `CellBuyImbalance` / `CellSellImbalance` — matches the 2026-07-02 resolution + unit tests |
| Winning-operand floor (not both-operand); divide-by-zero guard | ✅ | `ImbalanceMinFloor` on the dominant side; `<=0` guarded |
| Stacked imbalance — ≥3 consecutive same-side, gap resets, maximal-run dedup | ✅ | `HasStackedImbalance` |
| Absorption — significant one-sided Δ + price fails to progress; **arms, never fires** | ✅ | `IsAbsorption`; entry still needs the flip |
| Finished auction — one side ~0 at the extreme, absolute floor mandatory | ✅ | `FinishedAtExtreme` |
| Delta-flip — **cross-bar** (our footprint history is price-ordered → intrabar path unrecoverable) | ✅ | `DeltaFlip` uses cross-bar sign reversal + z≥1.5 significance, exactly as the spec concedes |
| Never fire on the absorption bar (Mistake #2) | ✅ | flip is a strictly separate, later trigger |
| CVD divergence (slower secondary) | 🟡 | not separately computed; folded into the absorption axis per the dedup rule (acceptable, non-independent) |
| Volume divergence (Tool 4) | ⛔ minor gap | not implemented; optional secondary |

## D. Unconditional gates & risk-shape

| Gate (spec) | Status | Where |
|---|---|---|
| **3R gate — unconditional on every trade** | ✅ | `Evaluate` rejects `rrr < MinRRR (3.0)` |
| Fat-zone reject (>25pt structural stop) | ✅ | `slDist > FatZoneMaxPoints` → reject |
| Resting **limit** entry (no-chase, #19) | ✅ | `PlaceBracket` uses `OrderType.Limit` at the level |
| Min-hold ≥ seconds (microscalp rules) | 🟡 | governor config `MinHoldSeconds=120`; **not yet enforced on exits** — see gaps |

## E. Prop-firm operating constraints (`Prop Firm Bot Research`)

| Constraint | Required | Status | Where |
|---|---|---|---|
| **EOD** trailing drawdown (intraday spikes never move floor) | mandatory | ✅ | `RiskGovernor.OnSessionClose` trails on EOD balance only |
| Max DD $2,000 never breached | mandatory | ✅ **proven** | `RiskGovernor` + `backtest/eval_sim.py` Part A |
| No overnight — flat by session close | every firm | ✅ | `Heartbeat` flat-by cutoff + session roll |
| Position size ≤ 10 MNQ | Lucid hard rule | ✅ | `MaxContracts=10`, plus dollar-risk sizing caps it lower |
| ≤200 trades/day (HFT ban) | MFFU | ✅ | `MaxTradesPerDay=15` (far under) |
| Zone-based entries only (no tick-arb/news-straddle) | all | ✅ | order-flow-at-a-level doctrine, no latency arb |
| Min hold ≥5s (target 2–10 min) | Lucid/TradeDay | 🟡 | configured 120s; **exit-side enforcement is a gap** |
| Consistency-rule monitor (40–50% best-day) | payout | ⛔ gap | not implemented; relevant at payout, not eval survival |
| MNQ 2%/5% price-limit suspension handling | MFFU only | ⛔ gap | firm-specific; add a volatility-halt guard if targeting MFFU |

---

## Flagged gaps, ranked

1. **⛔ VP location legs not wired (`BuildProfileLevels`)** — *primary functional gap.*
   The bot cannot generate a signal until POC/HVN/LVN/VA construction from Quantower
   Volume Analysis is implemented (Tools 10–13). **Fails safe**: no structure → no trade,
   never a blind trade. This is the top of the build queue for the signal side.

2. **⛔ Regime shape-classifier missing** — HTF bias is an EMA proxy, not the spec's
   D/P/b/double-distribution classifier that routes fade-vs-break. Until added, the bot is
   conservative (EMA-aligned continuation + explicit reversal only).

3. **🟡 All numeric thresholds are MNQ placeholders** — imbalance ratio (4.0), absorption
   significance (0.30), finished cutoff (12%), burst size (40), z (1.5), edge tol (4 ticks).
   The spec says this itself, repeatedly: *"validate on the 5-yr footprint before live."*
   None are calibrated. **Do not trust the signal edge with real money until calibrated.**

4. **🟡 Min-hold not enforced on exits** — configured but the stop/target bracket can close
   sub-2-min on a fast stop-out. Add a min-hold guard on discretionary exits (stop-outs are
   exempt at most firms, but confirm per firm).

5. **⛔ Consistency-rule + MNQ price-limit monitors** — payout-time / firm-specific;
   not eval-survival critical, but wire before funded.

6. **🟡 Delta-flip is cross-bar only** — an accepted concession (our footprint history is
   price-ordered). Intrabar flip needs a tick/T&S sub-bar delta series (Tool 16) — a future
   upgrade, not a correctness bug.

**None of the gaps threaten drawdown survival.** They limit signal *quality/frequency*,
which is exactly the axis the brief said to subordinate to survival ("a slow pass beats a
fast fail"). The risk layer is complete and proven; the signal layer is spec-faithful in
structure and honestly incomplete/uncalibrated in its numbers.
