# IOF TrendLab — Mr. Black Trend Methodology

## Core Rules (Mr. Black / TradePhantoms)

### What is a Leg?
A leg is a **continuous run of same-direction candle closes** until the opposite direction breaks it.
- Bull leg: consecutive bullish closes (close > open) — ends when a bearish candle closes BELOW the prior close
- Bear leg: consecutive bearish closes (close < open) — ends when a bullish candle closes ABOVE the prior close
- Doji (close = open): continues the current leg
- **Body closes ONLY. Wicks never establish or break a leg.**

### Three-Segment Structure
Three alternating legs = one complete structure:
- **Bull structure**: bull-leg → bear-leg → bull-leg, where leg 3 extreme (HH) > leg 1 extreme
- **Bear structure**: bear-leg → bull-leg → bear-leg, where leg 3 extreme (LL) < leg 1 extreme

Labels per leg:
- Bull leg extreme > prior bull leg extreme → **HH** (Higher High)
- Bull leg extreme < prior bull leg extreme → **LH** (Lower High)
- Bear leg extreme < prior bear leg extreme → **LL** (Lower Low)
- Bear leg extreme > prior bear leg extreme → **HL** (Higher Low)

### Dual Control Points
Both are active simultaneously:
- **Controlling High** = HH of the most recent completed bull structure (only ratchets UP)
- **Controlling Low** = LL of the most recent completed bear structure (only ratchets DOWN)

### Trend State
- **BULL** — close > Controlling High
- **BEAR** — close < Controlling Low
- **FLAT** — price between both control points (non-directional market)
- **No direct Bull→Bear flip.** Must pass through FLAT.

### Key Insight
Multiple control points can be active simultaneously. Neither needs to be broken for the market to be non-directional. When price is ranging between the Controlling High and Controlling Low, the correct state is FLAT regardless of what segment structure is forming inside the range.

---

## Implementation Notes

### Leg Size Filter (MinLegTicks)
A leg only seals if its extreme moved at least `MinLegTicks` ticks from its start close.
This prevents single-candle noise from creating false structures on higher timeframes.

**Per-timeframe defaults (MES/ES, body-close based):**
| TF   | MinLegTicks | Points (MES @ 0.25/tick) |
|------|-------------|--------------------------|
| 4H   | 40          | 10 pts                   |
| 1H   | 20          | 5 pts                    |
| 15M  | 10          | 2.5 pts                  |
| 5M   | 6           | 1.5 pts                  |
| 1M   | 4           | 1 pt                     |

These are starting points — user tunes them live via InputParameters.

### Chart Drawing
- **HH/HL/LH/LL diamond labels** drawn at each completed leg's extreme on the current chart TF
- **Controlling High** = red horizontal zone extending full chart width
- **Controlling Low** = green horizontal zone extending full chart width
- Labels right-aligned on chart: "Controlling High" / "Controlling Low"

### MTF Panel
Top-left panel showing BULL/BEAR/FLAT for: MN, W, D, 4H, 1H, 15M, 5M, 1M
Each TF uses its own TrendStateMachine processing its own HistoricalData feed.
Chart drawing machine processes the current chart's own HistoricalData separately.

---

## What Was Wrong With Brandon's Version
1. Used fractal lookback pivot detection (N bars left/right window) — wrong tool
2. Only tracked ONE control point at a time — missed the dual active CP concept
3. Flipped directly Bull→Bear without passing through FLAT
4. Wicks were involved in structure detection

## What Makes This Unique
No one has implemented Black's methodology correctly because:
- Everyone defaults to fractal/pivot detection instead of leg-based detection
- Nobody tracks BOTH controlling high AND controlling low simultaneously
- The FLAT state between active control points is never implemented
- The dual-CP ranging market concept is Black's core teaching and it has never been coded correctly before this build
