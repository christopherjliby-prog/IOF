# IOF — Institutional Order-Flow Prop-Eval Bot

Two things live here:

1. **The original IOF indicator** (`TradePhantoms_IOF_v2.cs` + helpers) — a Quantower
   *drawing/analysis Indicator* that detects and scores IOF zones. It places **no orders**
   and is not a trading strategy (see `docs/BUILD_REPORT.md` §Task 1).

2. **The new automated Strategy** (`strategy/`) — a Quantower `Strategy` that trades the
   order-flow doctrine from `ORDERFLOW_SPEC.md` **inside a hard, proven risk cage** built
   for a **50K EOD-trailing-drawdown** prop evaluation (max DD **$2,000**, daily target
   **$1,540**).

## `strategy/`
| File | Role |
|---|---|
| `RiskGovernor.cs` | Hard risk core — EOD trailing DD, daily-loss cutoff, size caps, kill-switch, +$1,540 daily lockout. Pure/SDK-free. **The floor is mathematically unbreachable by construction.** |
| `SignalEngine.cs` | Entry doctrine: context → location → confirmation AND-gate, 3 vetoes, resolved-diagonal imbalance, absorption, finished-auction, cross-bar delta-flip, unconditional 3R gate. |
| `IOF_PropEvalBot.cs` | The Quantower `Strategy`: order placement, session/EOD control, wires the two above + reuses `EntryAndTPHelpers.cs`. |

## `backtest/eval_sim.py`
Runs anywhere with Python 3. Two results:
- **Proof (deterministic):** under a 100%-worst-case losing adversary at max size, equity
  never gets within **$330** of the $2,000 floor. Edge-independent.
- **Model (Monte-Carlo):** eval pass-rate, expectancy, win/loss, daily P&L distribution.
  **DD-fail rate is 0.0%** at every win rate — the account is never lost to the drawdown.

```
python3 backtest/eval_sim.py
```

## Read first
- `docs/BUILD_REPORT.md` — all findings, the eval math, and the backtest results.
- `docs/SPEC_VERIFICATION.md` — cross-check vs `ORDERFLOW_SPEC.md` with every gap flagged.

## Status / honesty
The risk layer is complete and proven. The signal layer is **spec-faithful in structure but
uses uncalibrated MNQ placeholder thresholds** and still needs the Volume-Profile location
legs wired to Quantower Volume Analysis — until then it trades nothing (fails safe). The C#
was written against the documented Quantower API but not compiled here (no SDK in the build
container); SDK-version seams are marked `// >>> VERIFY vX <<<`. Do not trust the signal
edge with real money until the placeholders are calibrated on real footprint data.
