# Orderflow Calibration Scrub — External Reference Anchors

**Prepared:** 2026-07-06
**Purpose:** Gather *externally published* reference ranges, formulas, and methodology for orderflow calibration, to serve as **sanity-check anchors** against Chris's own live footprint calibration.

> ⚠️ **Read this first.** Nothing below is a recommended threshold for the IOF system. These are numbers other people published, in *their* instruments and *their* normalization conventions. They are anchors to compare against — not a substitute for calibration from Chris's own MNQ/MES recordings. Where a number's units are ambiguous, that is flagged; an unlabeled number is not usable for comparison and is treated as such.

---

## Channel coverage for this scrub

The task brief assumed the local `agent-reach` stack (Exa, yt-dlp, RSS, Bilibili, Jina). **That stack is not installed in this remote execution environment.** The scrub was run on the functional equivalents available here:

| Brief's channel | Status in this env | Substitute used |
|---|---|---|
| Exa web search | not installed | **WebSearch** (US-only web index) ✅ |
| Jina reader | blocked (per brief) / not installed | **WebFetch** (page → markdown) ✅ |
| GitHub | `gh` CLI absent; MCP present | **GitHub MCP `search_code`** ✅ + **WebFetch on `raw.githubusercontent.com`** for full files ✅ |
| YouTube (yt-dlp) | **not installed** — no transcript extraction | ❌ blocked (see "could not source") |
| Bilibili | **not installed** | ❌ blocked |
| RSS | no reader installed | ❌ not run |

One GitHub caveat worth noting: the MCP `get_file_contents` tool is scoped to `christopherjliby-prog/iof` only, so full files from *other* repos were pulled via WebFetch on their public raw URLs instead. Code-search fragments across all of GitHub were unrestricted.

---

## Master table — source → channel → number/formula → rationale → link

Every row flags **units/normalization** explicitly. `[LOW-CONF]` marks forum/blog/unverified sources.

### 1. Delta / CVD exhaustion thresholds

| Source | Channel | Number / formula | Units & normalization | Author's stated rationale | Link |
|---|---|---|---|---|---|
| BackQuant "CVD Z-Score" (TradingView) | WebSearch | Exhaustion threshold **σ = 1.75** default, range **1.0–4.0** | **Z-score of CVD *change*** (std-dev normalized), *not* raw contracts | "Exhaustion detection is based on standard-deviation of CVD change, not price — this catches reversals that price-only divergence misses" | [tradingview.com](https://www.tradingview.com/script/2eSOXI90-Cumulative-Volume-Delta-Z-Score-BackQuant/) |
| BackQuant "CVD Z-Score" (TradingView) | WebSearch | General z-score guidance: **1.0 flags ~half of bars; 4.0 almost never fires; default band 2.5–3.5** | **Z-score** (dimensionless, rolling-std normalized) | "calibrated for typical market data" — outlier sensitivity vs firing rate | [tradingview.com](https://www.tradingview.com/script/2eSOXI90-Cumulative-Volume-Delta-Z-Score-BackQuant/) |
| `prodbym1k3y/mes-trading-intel` → `delta_flow.py` | GitHub (raw fetch) | `ABSORPTION_DELTA_THRESHOLD = 500` | **Raw delta contracts** (avg abs delta over 5 bars) | "min delta magnitude for absorption" | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/strategies/delta_flow.py) |
| `prodbym1k3y/mes-trading-intel` → `delta_flow.py` | GitHub (raw fetch) | `EXHAUSTION_DECEL_THRESHOLD = 0.4`; trigger `1.0 - (recent_avg/prior_avg) > 0.4` | **Ratio** (dimensionless deceleration of delta) | "deceleration ratio to flag exhaustion" | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/strategies/delta_flow.py) |
| `prodbym1k3y/mes-trading-intel` → `delta_flow.py` | GitHub (raw fetch) | Delta momentum: `avg_ratio * 3.0`, trigger `abs(momentum) > 0.3` | **Delta/volume ratio** scaled to [-1,1] (% of volume that is directional) | "How much of volume is directional" | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/strategies/delta_flow.py) |
| `prodbym1k3y/mes-trading-intel` → `advanced_orderflow.py` | GitHub (raw fetch) | `exhaustion_vol_multiplier = 2.5`; `exhaustion_price_ticks = 2` | Vol = **ratio of bar volume to moving avg**; price = **ticks** | "Flags volume spikes with minimal price movement as trapped traders" (bar vol ≥ 2.5× avg AND range ≤ 2 ticks) | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/engines/advanced_orderflow.py) |
| `joaoschaun/urionmultisimbol` → `order_flow_analyzer.py` | GitHub (raw fetch) | `delta_threshold = 100` (default) | **Raw volume** (contracts/ticks) — strong-vs-moderate classification | No stated rationale in code | [github.com](https://github.com/joaoschaun/urionmultisimbol/blob/ca5e8bf/src/analysis/order_flow_analyzer.py) |
| MMT / CoinGlass / Bookmap (retail explainers) | WebSearch | *No number* — exhaustion = "price makes HH/LL while CVD fails to confirm" | **Qualitative divergence**, no magnitude | Divergence framing only | [bookmap.com](https://bookmap.com/blog/how-cumulative-volume-delta-transform-your-trading-strategy) `[LOW-CONF]` |

### 2. Absorption detection (quantitative)

| Source | Channel | Number / formula | Units & normalization | Author's stated rationale | Link |
|---|---|---|---|---|---|
| `prodbym1k3y/mes-trading-intel` → `delta_flow.py` | GitHub (raw fetch) | `ABSORPTION_PRICE_THRESHOLD = 0.5` **pts** AND `ABSORPTION_DELTA_THRESHOLD = 500` **delta** | Price = **index points**; delta = **raw contracts** | Big delta (≥500) while price moves <0.5 pt = absorption | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/strategies/delta_flow.py) |
| `prodbym1k3y/mes-trading-intel` → `advanced_orderflow.py` | GitHub (raw fetch) | Absorption footprint: `bar_range ≤ tick_size*4` AND level `bid_vol > ask_vol*2` (or inverse) | Range = **ticks (≤4)**; level = **2:1 volume ratio** | "Price movement must stay tight; one-sided volume absorption at a level" | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/engines/advanced_orderflow.py) |
| `Matesensei/FlowMate` → `orderflow_glm.py` | GitHub (raw fetch) | Absorption must persist: `persist_min = 2` of `persist_window = 3` micro-bars | **Rolling occurrence count** (2-of-3 bars) | "absorption must recur across the last few micro-bars" | [github.com](https://github.com/Matesensei/FlowMate_Trading_Assistant/blob/792ac33/cex_strategies/strategies/orderflow_glm.py) |
| `Matesensei/FlowMate` → `orderflow_glm.py` | GitHub (raw fetch) | CVD/price divergence: `cvd_now ≥ cvd_lo + 0.25*cvd_range` (`divergence_frac=0.25`, `divergence_window=60`) | **% of recent 60-bar CVD range** (normalized, not raw) | "micro-CVD must have recovered a fraction of its recent range off its low" | [github.com](https://github.com/Matesensei/FlowMate_Trading_Assistant/blob/792ac33/cex_strategies/strategies/orderflow_glm.py) |
| `joaoschaun/urionmultisimbol` → `order_flow_analyzer.py` | GitHub (raw fetch) | Absorption logic: `volume > 1000 AND abs(delta) < delta_threshold/2` (i.e. `<50`) | Vol = **raw contracts**; delta = **raw contracts** | High volume + near-zero net delta = absorption | [github.com](https://github.com/joaoschaun/urionmultisimbol/blob/ca5e8bf/src/analysis/order_flow_analyzer.py) |
| SpotGamma support / Bookmap | WebSearch | *No number* — "big limit orders stop price; CVD new high/low but price doesn't" | **Qualitative** | Passive orders absorb aggressive flow | [support.spotgamma.com](https://support.spotgamma.com/hc/en-us/articles/15245728388627-Absorption-and-Exhaustion) `[LOW-CONF]` |

### 3. Footprint imbalance ratios & stacked-run lengths

| Source | Channel | Number / formula | Units & normalization | Author's stated rationale | Link |
|---|---|---|---|---|---|
| GoCharting docs | WebSearch | **Ratio = 3 → 300% (3:1)**; 3:1 or 4:1 common | **Diagonal ratio** (bid vs ask one level higher/lower), dimensionless | "volume on one side must be at least 3× the opposing diagonal level" | [gocharting.com](https://gocharting.com/docs/orderflow/imbalance-charts) |
| Damn Prop Firms / LiteFinance | WebSearch | **3:1 or 4:1** highlights aggressive buying/selling | **Diagonal ratio** | Standard footprint imbalance convention | [damnpropfirms.com](https://damnpropfirms.com/prop-firms/order-flow-imbalances-footprint-charts/) `[LOW-CONF]` |
| Quantower docs | WebSearch | Configurable imbalance ratio; **stacked = multiple consecutive imbalances** on one side | **Diagonal ratio + consecutive-level count** | Stacked zones = aggressive-entry S/R levels | [quantower.com](https://www.quantower.com/blog/imbalance-footprint-chart-and-rithmic-plugin) |
| `nexobanks-prep/OpenSource-Hedge-Terminal` → `footprint.py` | GitHub (raw fetch) | `_imbalance_signal(threshold=3.0)` | **Ratio** (ask/bid or bid/ask ≥ 3×) | "Return an imbalance label when ask/bid or bid/ask exceeds threshold" | [github.com](https://github.com/nexobanks-prep/OpenSource-Hedge-Terminal/blob/6fcaf3b/hedge_terminal/modules/footprint.py) |
| `prodbym1k3y/mes-trading-intel` → `advanced_orderflow.py` | GitHub (raw fetch) | `imbalance_ratio = 3.0`; `stacked_min_count = 3` | Ratio + **count of consecutive levels (≥3)** | "3+ consecutive imbalance levels in same direction" | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/engines/advanced_orderflow.py) |
| `prodbym1k3y/mes-trading-intel` → `delta_flow.py` | GitHub (raw fetch) | Stacked imbalance: `abs(level.delta/level.total_volume) > 0.4` (≈ ">70% one side"), **3+ consecutive** | **% of level volume** (delta/total ratio) | ">70% ask volume = buy imbalance; 3+ consecutive required" | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/strategies/delta_flow.py) |
| `joaoschaun/urionmultisimbol` → `order_flow_analyzer.py` | GitHub (raw fetch) | `imbalance_threshold = 3.0` | **Ratio** (buy_vol/sell_vol) | No stated rationale | [github.com](https://github.com/joaoschaun/urionmultisimbol/blob/ca5e8bf/src/analysis/order_flow_analyzer.py) |

**Consensus on this metric is strong:** imbalance **3:1** (some use 4:1), stacked run length **≥3 consecutive levels**. Corroborated by three independent code repos *and* three retail docs, all in the same **diagonal-ratio** convention.

### 4. Instrument-specific scaling (MNQ / MES)

| Source | Channel | Number / formula | Units & normalization | Rationale | Link |
|---|---|---|---|---|---|
| QuantVPS / CME | WebSearch | **MNQ:** $2/point, **$0.50/tick**, tick size 0.25 | Contract spec (USD) | Micro Nasdaq-100, $2 multiplier | [quantvps.com/mnq](https://www.quantvps.com/blog/mnq-tick-value) · [cmegroup.com](https://www.cmegroup.com/markets/equities/nasdaq/micro-e-mini-nasdaq-100.html) |
| QuantVPS / Schwab | WebSearch | **MES:** $5/point, **$1.25/tick**, tick size 0.25 | Contract spec (USD) | Micro S&P 500, $5 multiplier | [quantvps.com/mes](https://www.quantvps.com/blog/mes-tick-value) · [schwab.com](https://www.schwab.com/learn/story/stock-index-futures-tick-values) |
| CME / Schwab (derived) | WebSearch | **Micro = 1/10 of full-size:** NQ $20/pt vs MNQ $2/pt; ES $50/pt vs MES $5/pt | Multiplier ratio (10×) | 1 full-size contract = 10 micros in $ exposure | [schwab.com](https://www.schwab.com/learn/story/stock-index-futures-tick-values) |
| `prodbym1k3y/mes-trading-intel` (whole engine) | GitHub (raw fetch) | Thresholds are **tick-relative** (`tick_size` param) for price, but **raw-contract** for delta (500) with **"no MES-specific scaling detected"** | Mixed: price normalized to ticks, delta in raw contracts | Delta thresholds are *not* instrument-scaled in this code | [github.com](https://github.com/prodbym1k3y/mes-trading-intel/blob/35f31a7/mes_intel/strategies/delta_flow.py) |
| `lakshmanb4u/TradingBotMiroFish` → `replay_orderflow_jsonl.py` | GitHub (search fragment) | `SWEEP_SIZE_THRESHOLD = 20  # min contracts for sweep flag` | **Raw contracts** | Minimum aggressive size to flag a sweep | [github.com](https://github.com/lakshmanb4u/TradingBotMiroFish/blob/93de1ca/market-swarm-lab/scripts/replay_orderflow_jsonl.py) |

**Scaling implication (derivation, not a published rule):** because 1 full-size contract ≈ 10 micros in dollar terms, a delta threshold quoted in *raw contracts on ES/NQ* would need roughly **10× more micro contracts** to represent the same dollar flow. Delta thresholds quoted in **% of bar volume, ATR-multiples, or z-scores are instrument-agnostic** and port across micro/full-size without rescaling — which is the whole argument for using normalized thresholds over raw counts.

### 5. Open-source implementations — hard-coded constants (extracted)

Full constant dump from the most threshold-dense repo, `prodbym1k3y/mes-trading-intel/advanced_orderflow.py` (all verbatim from source):

| Constant | Default | Units | Stated purpose |
|---|---|---|---|
| `imbalance_ratio` | `3.0` | ratio | bid/ask volume comparison |
| `stacked_min_count` | `3` | count of levels | consecutive imbalances |
| `exhaustion_vol_multiplier` | `2.5` | ratio (bar vol / avg) | volume spike |
| `exhaustion_price_ticks` | `2` | ticks | max range for exhaustion |
| absorption bar range | `4` | ticks | tight range for absorption |
| absorption level ratio | `2` | ratio | one-sided level volume |
| single-print threshold | `0.05` | % of max profile vol | low-volume/fast-traded levels |
| excess tail threshold | `0.3` | % of bar volume | rejection tails |
| pull/stack DOM change | `0.3` | % of prior depth | spoof/stack detection |
| unfinished-auction min vol | `5` / `20` | raw contracts | noise filter / one-sided flag |
| POC migration | `2` | ticks | rising/falling vs stable |
| cluster confidence | `0.5 + 0.15·n` (cap 0.95) | probability | multi-signal convergence |

Additional cross-repo constants: `orderflow_glm.py` (VPIN gate `vpin_toxic_quantile=0.85`, liquidation `liq_z_min=1.5` z-score, ATR stops `0.8–1.5×`, all citing **Easley, López de Prado & O'Hara (2012), "Flow Toxicity and Liquidity in a High-Frequency World"** — [quantresearch.org PDF](https://www.quantresearch.org/From%20PIN%20to%20VPIN.pdf), SSRN 1695596). This is the one **peer-reviewed academic anchor** in the set and the only source that grounds a threshold (VPIN toxicity) in published research rather than convention.

---

## Anchors vs Chris's live calibration

For each metric: the external range found, and a **blank for Chris's own live-derived value** so the two sit side by side. *Fill the right column from MNQ/MES recordings — do not adopt the left column.*

| Metric | External anchor (units) | Chris's live-derived value |
|---|---|---|
| **Footprint imbalance ratio** | **3:1** (some 4:1) — diagonal ratio *[strong consensus]* | `__________` |
| **Stacked imbalance run length** | **≥3 consecutive levels** *[strong consensus]* | `__________` |
| **Per-level absorption ratio** | **2:1** one-sided (advanced_orderflow) | `__________` |
| **CVD exhaustion (normalized)** | **z-score 1.75 default, 1.0–4.0 band** (BackQuant) | `__________` (σ of Chris's CVD-change dist.) |
| **Delta absorption (raw)** | **500 contracts** avg, price move **<0.5 pt** (MES code) — ⚠️ raw, not scaled | `__________` (MNQ) / `__________` (MES) |
| **Exhaustion volume spike** | **2.5× avg bar volume** within **≤2 ticks** range | `__________` |
| **Exhaustion deceleration** | delta decel **>0.4** (ratio) | `__________` |
| **CVD/price divergence window** | **0.25 of recent 60-bar CVD range** (% normalized) | `__________` |
| **Delta momentum (directionality)** | `abs(delta/vol · 3) > 0.3` (% of vol directional) | `__________` |
| **Sweep minimum size** | **20 contracts** (raw) — ⚠️ crypto/ES-scale, rescale for micros | `__________` |
| **VPIN toxicity gate** | **0.85 rolling quantile** (academic-grounded) | `__________` |
| **Instrument scaling** | micro = **1/10** of full-size ($ terms) | `__________` (measured micro/full-size vol ratio) |

**How to read the split:** the normalized anchors (z-score, %-of-range, ATR-multiple, %-of-volume) are directly comparable to Chris's live values *regardless of instrument*. The raw-contract anchors (500 delta, 20 sweep, 100 delta) are **only comparable after rescaling to MNQ/MES volume regimes** — they were set for MES/crypto and carry no automatic validity on MNQ.

---

## Could not source (no credible published number — not fabricated)

- **MNQ-specific raw delta exhaustion magnitude.** No published, citable "MNQ bar delta = X contracts is exhaustion" number was found. The one raw-contract delta anchor (500) is from MES-oriented code that itself states "no MES-specific scaling detected" — it is not an MNQ number and should not be treated as one.
- **Typical MNQ/MES per-bar delta *range*** (e.g. "a 1-min MNQ bar runs ±N delta"). No published distribution found; this can only come from Chris's own recordings.
- **YouTube practitioner transcripts.** yt-dlp is not installed in this environment and transcript extraction was not possible. Named order-flow educators (Trader Dale, TradingRiot, Bookmap webinars) surfaced in web results but their *specific numeric* claims could not be transcript-verified here — only their blog text, which is qualitative.
- **Bilibili / RSS channels.** Not available in this environment; not run.
- **ATR-scaled *delta* exhaustion threshold with a published multiplier.** ATR scaling is widely used for *stops/targets* (found: 0.8–1.5×), and "ATR-scaled projections" are mentioned for CVD frameworks, but no source published a specific "delta exhaustion = k × ATR" constant. Marked absent rather than inferred.
- **Peer-reviewed threshold for footprint imbalance ratio.** The 3:1/4:1 convention is universal in retail/vendor docs and code but was not traced to an academic source; treat it as strong *convention*, not *proof*.

---

### Coverage summary (which channels worked, which were blocked)

**Worked:** WebSearch (Exa-equivalent) ✅, WebFetch on public pages + `raw.githubusercontent.com` ✅, GitHub MCP `search_code` across all repos ✅. **Blocked/unavailable:** yt-dlp/YouTube transcripts ❌, Bilibili ❌, RSS ❌, Jina reader ❌ — the entire `agent-reach` local stack is absent in this remote environment, so YouTube/Bilibili/RSS coverage is zero and all findings above rest on web + GitHub only.
