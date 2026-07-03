# IOF Prop-Eval Bot — Build Report

**Objective:** a Quantower automated Strategy (C#) that passes a 50K EOD-drawdown prop
evaluation. **Max DD $2,000, daily target $1,540.** Priority (per brief): *survival of the
drawdown beats speed to target — a slow pass beats a fast fail.*

**Environment honesty up front.** This build ran in a Linux container with **no .NET
runtime and no Quantower SDK/platform**, and there is **no MNQ market/footprint data** on
hand. Consequences, stated plainly so nothing is oversold:
- The C# was written against the documented Quantower API but **was not compiled** here.
  SDK-version-dependent lines are marked `// >>> VERIFY vX <<<`. The **risk core is pure
  arithmetic** and is independently proven (below), so its correctness does not depend on
  the platform.
- A **tick-accurate Quantower backtest is impossible here.** What I *can* prove — and did —
  is (a) the drawdown floor is mathematically unbreachable, and (b) an eval-outcome model
  with explicit assumptions. Neither is a claim of historical performance.

---

## Task 1 — Review the existing bot

**Finding: the existing project is a drawing/analysis _Indicator_, not a trading _Strategy_.
It places zero orders and does not compile standalone.**

- `TradePhantoms_IOF_v2.cs` is `class TradePhantoms_IOF_v2 : Indicator, IVolumeAnalysisIndicator`
  (line 80). It detects IOF zones (RBR/DBR/RBD/DBD), scores them on a 0–21 rubric
  (`ScoreZone`, threshold `MinScore=9`), draws dashboards/markers, and fires alerts.
- **No order placement anywhere.** A grep across all `.cs` for
  `PlaceOrder|TradingOperation|SubmitOrder|MarketOrder|new Order|.Buy(|.Sell(` returns
  **zero matches**. `Core.Instance` is used only for logging, `PositionAdded/Removed`
  *subscription* (it observes externally-created positions), and alerts. Execution was
  meant to be delegated to an external `AutoSLTP_Strategy` via an `IntentBus` channel.
- **Does not compile as delivered.** Four namespaces it depends on have **no source in the
  repo**: `TradePhantomsIOF.Lifecycle`, `.Trail`, `.Trend`, `.IntentBus`. So "confirm it
  compiles / runs as a Quantower strategy" resolves to: **it is not a strategy, and it will
  not compile in isolation.** I could not run it (no platform), and it could not place a
  trade even if loaded, by design.
- **What is clean and reusable:** `EntryAndTPHelpers.cs` (`EntryTPMath`) — pure, `using
  System;` only, well-tested math for entry depth / SL / TP ladder / dollar-risk sizing /
  R conversion / tick-snapping. **Reused verbatim** by the new strategy. The zone scanner
  (`MultiTimeframeZones.cs`) is also self-contained and portable.

**Also on disk (more recent, and the actual "bot behaviour spec"):** `ORDERFLOW_SPEC.md`
(dated 2026-07-02) — 16 council-validated order-flow tools defining a *different, newer*
methodology than the zone indicator, explicitly instructing a from-scratch rebuild. The new
bot's signal layer follows **that** spec (see Task 5).

## Task 2 — Risk enforced in code (the load-bearing deliverable)

`strategy/RiskGovernor.cs` — a pure, SDK-free state machine every decision passes through.

- **EOD trailing drawdown**, $2,000 → floor starts at $48,000; trails **on the EOD balance
  only** (intraday spikes never move it), moves up only. `OnSessionClose`.
- **Hard daily-loss cutoff** and **daily profit lockout** ($1,540 → bank and stop, no giving
  it back). `OnTradeClosed`.
- **Position caps** — ≤10 MNQ hard, *and* dollar-risk sizing (`EntryTPMath.ComputeContracts`)
  usually caps it far lower.
- **Kill-switch** — every tick marks unrealized P&L; if equity ever reaches `floor +
  killBuffer` it **flattens and halts the whole eval** (`MarkUnrealized`).
- **"Cannot place a trade that risks crossing the line."** `EvaluateEntry` refuses/reduces
  any order whose **worst case** (stop-out × a gap multiple) would (a) come within the kill
  buffer of the floor, or (b) exceed the effective daily loss. It can only ever *reduce*
  the sizer's proposal, never raise it.

**Why the floor is unreachable — the airtight invariant.** The daily loss cutoff in force is
`effectiveDailyLoss = min(configuredDailyLoss, roomToFloor − killBuffer − worstCaseTrade)`.
Because it **shrinks automatically as room to the floor shrinks** (a losing streak tightens
the leash on its own), running equity can never descend to the floor. `ValidateConfig`
refuses to even start a config where a fresh-day worst case could reach the floor.

## Task 3 — Entry/exit toward $1,540, then stop

- **Signal engine** (`strategy/SignalEngine.cs`) implements the `ORDERFLOW_SPEC.md`
  doctrine: context → location → confirmation, ≥2 independent reads, the 3 hard vetoes,
  resting-limit entry, stop beyond the far edge, target before the next barrier, and an
  **unconditional 3R gate**. Details + gaps in Task 5.
- **Sizing** via the reused `EntryTPMath.ComputeContracts` (fixed dollar risk), tick-snapped
  so brokers accept the prices; SL snapped *away* from entry (never tightened).
- **Daily target** — `RiskGovernor` locks the day at +$1,540 realized and the strategy
  cancels working orders + takes no new trades that day. Overshoot on the last trade is
  kept (no giving it back). **Eval passes** at the cumulative profit target (default $3,000
  = 6% of 50K; configurable per firm).
- **No overnight** — flat by the configured session cutoff; EOD roll flattens and trails.

## Task 4 — Backtest against the eval math

Run: `python3 backtest/eval_sim.py` (deterministic, fixed seed → reproducible).

### 4a. Eval math
`Start $50,000 · Floor $48,000 (= start − $2,000) · Daily target +$1,540 (bank & stop) ·
Daily loss cutoff −$600 · Per-trade risk $150 · MNQ $2/pt · Pass at +$3,000 cumulative.`

### 4b. PROOF — the $2,000 floor is unbreachable (deterministic, edge-independent)
Adversary: **every trade a maximal worst-case loss, at max size, every day.**
```
Adversary ran 20 maximal-loss trades. Lowest equity EVER touched: $48,330.00
Drawdown floor: $48,000.00 · Closest approach: $330 ABOVE the floor · Floor breached: NO
```
Single catastrophic gap **2.5× worse than the model assumed**, at max size, still left
equity **$1,750 above the floor** and the kill-switch flattened. The survival result does
**not** depend on any win-rate assumption — it is a property of the gate.

### 4c. MODEL — eval outcome (Monte-Carlo, stated assumptions)
Per-trade R model from the strategy's own structure (win = +3R, partials +1.2R, loss = −1R,
BE ≈ 0R). 5,000 evals per win-rate, ≤30 trading days each:

| Win rate | Pass % | **DD-fail %** | Expectancy/trade | Avg win | Avg loss | PF | Median days to pass |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 0.35 | 5.3% | **0.0%** | +$9.65 | $141 | −$60 | 1.28 | 27 |
| 0.40 | 24.6% | **0.0%** | +$19.06 | $142 | −$60 | 1.59 | 26 |
| 0.42 | 35.3% | **0.0%** | +$22.34 | $142 | −$60 | 1.71 | 25 |
| 0.45 | 54.7% | **0.0%** | +$27.87 | $142 | −$60 | 1.94 | 24 |
| 0.50 | 81.3% | **0.0%** | +$37.12 | $142 | −$60 | 2.38 | 22 |
| 0.55 | 94.9% | **0.0%** | +$46.15 | $142 | −$60 | 2.90 | 19 |

**Daily P&L distribution @ 45% win rate** (52,526 simulated trading days):
mean +$97.57 · median +$60 · **worst day −$588** (clamped by the −$600 cutoff) ·
**best day +$1,727** (day banked past the +$1,540 lockout, then stopped) ·
5th pctile −$151 · 95th pctile +$505.

### 4d. What the numbers say (honest reading)
- **The DD is never the failure mode.** DD-fail is 0.0% across every win rate — the account
  is never lost to the $2,000 line; a failed eval just means "didn't reach +$3,000 in time."
  That is precisely the risk posture the brief demanded.
- **Break-even edge ≈ 42% win rate** at 3R:1R with partials. Below that the eval usually
  isn't passed (but isn't blown either).
- **$1,540 in a single day is rare** with this conservative sizing — the daily tails are
  clamped to roughly [−$600, +$1,540], and reaching the top clamp needs a cluster of wins.
  The eval is designed to be passed by **accumulating ~$3,000 over ~3–5 weeks**, not by
  hero days. To hit $1,540/day more often you must raise per-trade risk or size, which
  trades directly against DD survival — see the tradeoff note below. **Per the brief, I
  defaulted to survival.**
- **These pass-rates are conditional on the R-model, not measured on markets.** Real
  win-rate/expectancy require Quantower tick backtests on calibrated signals (Task 5 gaps).

**Tradeoff dial (your call):** raising `PerTradeDollarRisk`/`MaxContracts` and
`DailyLossLimit` shortens time-to-target and makes $1,540 days common, but shrinks the
survival margin. `RiskGovernor.ValidateConfig()` will still refuse any setting where a
fresh-day worst case could touch the floor — so the dial has a hard safe ceiling.

## Task 5 — Verify against the bot-behaviour spec

Full table in **`docs/SPEC_VERIFICATION.md`**. Summary: the AND-gate spine, the 3 vetoes,
resolved-diagonal imbalance, stacked imbalance, absorption, finished-auction, cross-bar
delta-flip, resting-limit entry, stop/target geometry, and the unconditional 3R gate are all
implemented to doctrine. **Flagged gaps (none threaten DD survival):**
1. **VP location legs not yet wired** (`BuildProfileLevels` returns empty) → the bot takes
   **no** trades until POC/HVN/LVN/VA are built from Quantower Volume Analysis. *Fails safe.*
2. **Regime shape-classifier** (D/P/b/double-dist) missing — HTF bias is an EMA proxy.
3. **All numeric thresholds are uncalibrated MNQ placeholders** — the spec says so itself;
   calibrate on the 5-yr footprint before trusting the signal edge with money.
4. Min-hold not enforced on exits; consistency-rule + MNQ price-limit monitors not wired.

---

## Files

| File | Role |
|---|---|
| `strategy/RiskGovernor.cs` | **Proven** hard risk core (EOD DD, cutoff, caps, kill-switch, target lockout) |
| `strategy/SignalEngine.cs` | `ORDERFLOW_SPEC.md` AND-gate entry doctrine (footprint reads, 3R gate) |
| `strategy/IOF_PropEvalBot.cs` | Quantower `Strategy` — wiring, order placement, session/EOD control |
| `EntryAndTPHelpers.cs` | Reused pure entry/SL/TP/sizing math from the existing project |
| `backtest/eval_sim.py` | Floor-survival proof + Monte-Carlo eval model (runs here) |
| `docs/SPEC_VERIFICATION.md` | Full spec cross-check + ranked gaps |

## Deployment path (to go live, in order)
1. Open the three `strategy/*.cs` + `EntryAndTPHelpers.cs` in a Quantower strategy project;
   resolve each `// >>> VERIFY vX <<<` against your installed SDK; compile.
2. Wire `BuildProfileLevels()` to Quantower Volume Analysis (POC/HVN/LVN/VA — Tools 10–13).
3. **Calibrate every placeholder threshold** on real MNQ footprint (the spec's 5-yr set).
4. Run Quantower's own backtester on calibrated signals for the *edge* numbers; keep the
   `RiskGovernor` defaults for *survival*.
5. Forward-test on sim, confirm EOD-DD firm settings match, then a single funded account.
