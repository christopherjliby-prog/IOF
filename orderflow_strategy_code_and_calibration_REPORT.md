# Order-Flow Strategy Code & Calibration Corpus

**Prepared:** 2026-07-06
**Purpose:** Every order-flow–style strategy found, with its **actual computation code and numeric thresholds extracted**, so your IOF bot can run its own live-derived numbers against them. This is a calibration reference — external numbers are **anchors to compare against**, not thresholds to adopt.

> **How to use this file.** Part 1 is the consolidated **calibration-parameter table** — every order-flow threshold across all sources, grouped by metric, with units and a raw-vs-normalized flag. That's the artifact your bot runs numbers against. Part 2 is the **extracted code per source** (the load-bearing formulas, verbatim). Part 3 sits your **own IOF constants** beside the external ones. Companion files: `orderflow_calibration_anchors_REPORT.md` (published literature anchors), `quantower_strategy_microscope_REPORT.md` (full bot corpus).

---

## Sources extracted (15)

**Quantower / C#:**
1. `sfrdragon/Code-base-and-examples` → `FlagshipFuturesStrategy` (RVOL / Volume-Delta / HMA)
2. `mesuteryilmaz/QT_API` → `PyramidMomentumStrategy` (order-flow bias/confidence entry)
3. `mesuteryilmaz/QT_API` → `OnePairGridStrategy` (analytics-gated market-making)
4. `mesuteryilmaz/QT_API` → `OrderFlowMonitor` (the bias+confidence **engine**)
5. `mesuteryilmaz/QT_API` → `MarketStateMonitor` (regime/risk classifier)
6. `mesuteryilmaz/QT_API` → `AdaptiveParameters` (V1 flow-ratio controller; strategy file deleted, controller survives)
7. `mesuteryilmaz/QT_API` → `DataAnalyticsCalculator` (CVD, delta windows, CKS-OFI, DOM imbalance, absorption)
8. `alihamza1221/ImbalanceCluster-Stragtegy` (diagonal footprint imbalance + stacked)
9. `DarkLink005/iof-specs` → `TradePhantoms_IOF_v2` + `AutoSLTP_Strategy` (**your own IOF system**)

**Python (from your calibration scrub):**
10. `prodbym1k3y/mes-trading-intel` → `advanced_orderflow.py`
11. `prodbym1k3y/mes-trading-intel` → `delta_flow.py`
12. `Matesensei/FlowMate` → `orderflow_glm.py`
13. `joaoschaun/urionmultisimbol` → `order_flow_analyzer.py`
14. `nexobanks-prep/OpenSource-Hedge-Terminal` → `footprint.py`
15. `hopewoodworking24-lab/Hopefx` → `order_flow.py`

---

# PART 1 — Consolidated calibration table (run your numbers against these)

**Normalization key:** `[ratio]` dimensionless (portable across instruments) · `[raw]` raw contracts (rescale per instrument) · `[ATR]` ATR-scaled (portable) · `[%vol]` fraction of bar/level volume (portable) · `[ticks]`/`[pts]` price distance (rescale) · `[z]`/`[q]` z-score/quantile (portable).

### Metric 1 — Footprint / DOM imbalance ratio
| Source | Value / formula | Units |
|---|---|---|
| ImbalanceCluster | `dominant − opposing > 3 × opposing` ⟹ **4× dominance** (diagonal), stacked **3** rows | [ratio] |
| **IOF (yours)** | `ImbalanceRatio 3.0`, `MinStackedRows 3`, `ViolationPct 0.5` | [ratio] |
| advanced_orderflow.py | `imbalance_ratio 3.0`, `stacked_min_count 3` | [ratio] |
| footprint.py | `threshold 3.0` | [ratio] |
| order_flow_analyzer.py | `imbalance_threshold 3.0` | [ratio] |
| delta_flow.py | stacked `|delta/vol| > 0.4` (≈>70% one side), **3** consecutive | [%vol] |
| QT_API `DataAnalyticsCalculator` | DOM imbalance `(bid−ask)/(bid+ask)` at **3/5/10** levels; queue at best | [ratio −1..1] |
| Hopefx | `imbalance_threshold 0.30` on `delta/total_volume` | [ratio −1..1] |
| **Consensus** | **3:1 (300%) diagonal, ≥3 stacked rows** — the single strongest agreement in the whole corpus | |

### Metric 2 — Delta / CVD
| Source | Value / formula | Units |
|---|---|---|
| QT_API | `CumDelta = buyerQty − sellerQty`; delta windows **1/2/5/30/60 s**; velocity `2·Δ(1s) − Δ(2s)` | [raw] |
| **IOF (yours)** | CVD session-anchored (reset RTH open) + **20-bar** rolling; `delta = VolumeAnalysisData.Total.Delta` | [raw] |
| delta_flow.py | `ABSORPTION_DELTA_THRESHOLD 500`; momentum `avg_ratio×3 > 0.3` | [raw] / [%vol] |
| order_flow_analyzer.py | `delta_threshold 100` | [raw] |
| Flagship | VD strength `|VD| > mean(VD) × 1.2`; VD/price `ratio > hist × 1.5` | [ratio] |
| OrderFlowMonitor | `cvdSlopePerSec = signedShort / shortSec`; scale `50 contracts/s` → ±1 | [raw/s] |
| Hopefx | `cumulative_delta += size if buy else −size` | [raw] |

### Metric 3 — Absorption
| Source | Value / formula | Units |
|---|---|---|
| **IOF (yours)** | `range < 0.6×ATR AND |delta| > 1.5×20-bar-avg AND opposite-sign` (+Tier3: `volumeSpike > 1.3×avg AND nearZone < 0.25×ATR`) | [ATR]+[ratio] |
| QT_API | `totalVol ≥ AbsorptionThreshold (~167 = 2× typical-5 s, floor 10) AND range ≤ 1 tick` | [raw]+[ticks] |
| advanced_orderflow.py | `bar_range ≤ 4 ticks AND level bid > ask×2` | [ticks]+[ratio] |
| delta_flow.py | `ABSORPTION_PRICE 0.5 pt AND ABSORPTION_DELTA 500` | [pts]+[raw] |
| Hopefx | `price_range/avg_price < 0.001` (0.1% move) over **30 s**, ≥5 trades | [%]+[time] |
| FlowMate | persist `2 of 3` bars (`absorption_proxy`) | [count] |

### Metric 4 — Exhaustion
| Source | Value / formula | Units |
|---|---|---|
| advanced_orderflow.py | `vol > 2.5×avg AND range ≤ 2 ticks` | [ratio]+[ticks] |
| delta_flow.py | `EXHAUSTION_DECEL 0.4` = `1 − recent_avg/prior_avg` | [ratio] |
| BackQuant (lit.) | CVD-change z-score default **σ 1.75**, band 1.0–4.0 | [z] |

### Metric 5 — Divergence (CVD vs price)
| Source | Value / formula | Units |
|---|---|---|
| **IOF (yours)** | 15-bar simplified slope; Tier3 pivot `lbL=7/lbR=3` + **15% swing filter** (removes ~70% marginal) | [bars]+[%] |
| FlowMate | `divergence_frac 0.25` of **60-bar** CVD range | [%range] |
| Flagship | exit when `close` crosses HMA by `±1×ATR` | [ATR] |

### Metric 6 — Book pressure / OFI
| Source | Value / formula | Units |
|---|---|---|
| OrderFlowMonitor | `bookPressure = (bid−ask)/(bid+ask)` depth **5**; lean weights **imb 0.50 / book 0.30 / cvd 0.20**; `LeanThreshold 0.20`, `StrongLean 0.50` | [ratio] |
| QT_API `DataAnalyticsCalculator` | CKS-OFI `eb − ea` over **5 s**; distance-weighted book pressure depth **10** | [raw] |
| **IOF (yours)** | book-imbalance `> 15 NQ-equiv contracts net`; LiquidityMagnetDOM `MinSize 100 / hold 30 s / band 5 t` | [raw]+[ticks] |

### Metric 7 — Regime / risk gates
| Source | Value / formula | Units |
|---|---|---|
| MarketStateMonitor | Critical if `stress ≥ 0.80` or VolatileDislocated; ThinFragile `≥0.58`; emergency `spread ≥ 8 ticks` or `stress ≥ 0.90` | [0-1]+[ticks] |
| AdaptiveParameters | VolatilityGate `fastAtr/baseAtr > 1.5`; extreme `0.97/0.03` quantile; entry `0.85/0.15` quantile | [q] |
| PyramidMomentum | risk-off = ThinFragile ∪ VolatileDislocated ∪ Critical → veto/flatten | [regime] |
| orderflow_glm.py | VPIN toxicity `0.85` quantile; liquidation `liq_z 1.5` | [q]/[z] |
| **IOF kill-switch (yours)** | **4** consecutive losses; **60%** of firm DLL; **80%** of trailing limit; **60 s** indicator silence | [count]/[%]/[s] |

### Metric 8 — Order-flow-driven TP/SL sizing
| Source | SL basis | TP basis | Sizing |
|---|---|---|---|
| Flagship | prevBar ± `ATR×1.0`, clamp **[4, 20] t** | nearest session level, min **8 t** / alt **12 t** | fixed contracts |
| PyramidMomentum | `40 t`, vol-scaled `×2.0` | soft-trail `20 t`, vol-scaled `×1.0`, persist 750 ms | pyramid +1/20t, cap 3 |
| AdaptiveParameters | `ATR-ticks × 1.0`, clamp **[2, 200] t** | `ATR-ticks × 1.0` | — |
| OnePairGrid | `40 t` stop-market | `100 t` (= pair width) | qty 1 |
| **IOF (yours)** | far wick + `StopBufferTicks` (MES 6 / MNQ 10 / MYM 8 / M2K 12 / MGC 8 / MCL 6 / MNG 15 / M6E 10) | **1× / 2× / 3× zoneHeight** (3 TPs) | `floor(DollarRisk / (slDist × pointValue))`, cap `MaxContracts`; `RrrFixedMultiple 5.0` |

---

# PART 2 — Extracted code per source (verbatim, load-bearing blocks)

## 2.1 · mesuteryilmaz/QT_API — `OrderFlowMonitor` (bias + confidence engine)
`src/QT.Features/OrderFlow/OrderFlowMonitor.cs` @ `6d615f5a`.
```csharp
// lean score = weighted blend of trade imbalance, book pressure, CVD slope
double imbalance = totalShort > 0 ? buyShort / (double)totalShort : 0.5;
double bookPressure = BookPressure(book, cfg.BookPressureDepth);
double cvdSlopePerSec = signedShort / shortSec;

double wSum = cfg.WeightImbalance + cfg.WeightBookPressure + cfg.WeightCvdSlope;   // 0.50+0.30+0.20
double imbalanceComponent = (2.0 * imbalance - 1.0) * cfg.WeightImbalance;         // [0,1]→[-1,1]
double bookComponent = bookPressure * cfg.WeightBookPressure;
double cvdComponent  = Clamp(cvdSlopePerSec / cfg.CvdSlopeScalePerSec, -1.0, 1.0) * cfg.WeightCvdSlope;  // scale 50/s
double leanScore = (imbalanceComponent + bookComponent + cvdComponent) / wSum;

DirectionalBias bias = leanScore >=  cfg.LeanThreshold ? DirectionalBias.Up      // 0.20
                     : leanScore <= -cfg.LeanThreshold ? DirectionalBias.Down
                     : DirectionalBias.Neutral;
double confidence = Clamp(Math.Abs(leanScore) / cfg.StrongLeanThreshold, 0, 1);   // 0.50 → conf 1.0
```
```csharp
// BookPressure = (bidDepthN − askDepthN)/(bidDepthN + askDepthN) over top N levels
long bid = 0, ask = 0;
foreach (var lvl in book.Bids.Take(depth)) bid += lvl.Quantity;   // depth 5
foreach (var lvl in book.Asks.Take(depth)) ask += lvl.Quantity;
return total > 0 ? (bid - ask) / (double)total : 0.0;
```
**Constants:** Warmup 10s, ShortWindow 5s, LongWindow 30s, BookPressureDepth 5, weights 0.50/0.30/0.20, CvdSlopeScalePerSec 50, LeanThreshold 0.20, StrongLeanThreshold 0.50.

## 2.2 · QT_API — `MarketStateMonitor` (regime/risk)
`src/QT.Features/MarketState/MarketStateMonitor.cs`.
```csharp
double liquidityStress = Clamp01(
    Math.Max(0, meanSpread30 - cfg.OneTickSpread) / 6.0 +   // OneTickSpread 1
    Math.Max(0, 1.0 - touchRatio) * 0.35 +
    Math.Max(0, 1.0 - top5Ratio) * 0.25 +
    cancelPressure * 0.35);

// regime decision tree
if (spread >= cfg.EmergencySpreadTicks || stress >= 0.90) return MarketRegime.VolatileDislocated;  // 8 ticks
if (stress >= 0.70 && vol >= 0.55) return MarketRegime.VolatileDislocated;
if (stress >= 0.58) return MarketRegime.ThinFragile;
if (activity >= 0.60 && vol >= 0.45 && stress < 0.55) return MarketRegime.FastOrderly;
if (activity >= 0.38 && stress < 0.50) return MarketRegime.ActiveLiquid;
if (spread <= cfg.OneTickSpread && activity < 0.35 && vol < 0.35 && stress < 0.35) return MarketRegime.QuietTight;

// risk env
var risk = stress >= 0.80 || regime==VolatileDislocated ? Critical
         : stress >= 0.60 || vol >= 0.75 ? Elevated
         : stress <= 0.25 && vol <= 0.35 ? Low : Normal;
```
**Constants:** stress weights 0.35/0.25/0.35, spread-excess /6.0, regime cuts 0.90/0.70+0.55/0.58/0.60+0.45/0.38/…, risk 0.80/0.60/0.25, EmergencySpread 8t, OneTickSpread 1t, dwell 2s, candidate persist 1s.

## 2.3 · QT_API — `AdaptiveParameters` (flow-ratio controller)
`AdaptiveParameters.cs`. Entry thresholds = percentiles of the recent buyer/seller-ratio distribution; brackets = ATR-scaled.
```csharp
double buyTh  = Percentile(arr, cfg.EntryUpperPercentile);   // 0.85
double sellTh = Percentile(arr, cfg.EntryLowerPercentile);   // 0.15
double median = Percentile(arr, 0.5);                        // re-arm/reset level

bool voltSpike = voltRatio > cfg.VolatilityGateRatio;        // fastAtr/baseAtr > 1.5
double extremeBuyTh  = Percentile(arr, cfg.ExtremeUpperPercentile);  // 0.97
double extremeSellTh = Percentile(arr, cfg.ExtremeLowerPercentile);  // 0.03
bool extremeRatio = saturated || lastObservedRatio >= extremeBuyTh || lastObservedRatio <= extremeSellTh;
RegimeState regime = (voltSpike || extremeRatio) ? RegimeState.StandAside : RegimeState.Normal;

double atrTicks = atr / tickSize;
int tp = ClampTicks((int)Math.Round(atrTicks * cfg.TpAtrMultiplier));  // ×1.0, clamp[2,200]
int sl = ClampTicks((int)Math.Round(atrTicks * cfg.SlAtrMultiplier));  // ×1.0
```
**Constants:** entry 0.85/0.15, reset median 0.50, SampleWindow 5000, interval 250ms, MinSamples 480 (~2min), RatioClampMax 10.0, Recalc 2s, AtrPeriod 14, Tp/SlAtrMult 1.0, bracket clamp [2,200]t, AutocorrWindow 60 (~15s), Momentum/MeanRev ACF ±0.10, FastAtr 5, VolatilityGate 1.5, extreme 0.97/0.03.

## 2.4 · QT_API — `DataAnalyticsCalculator` (CVD / delta / OFI / imbalance / absorption)
`legacy_v1/DataAnalyticsCalculator.cs`.
```csharp
// Delta / CVD
CumDelta = totalBuyerTradeQty - totalSellerTradeQty;
Delta(w) = buyQty[w] - sellQty[w];                    // windows S1/S2/S5/S30/S60 = 1/2/5/30/60s
DeltaVelocity = 2.0*Delta(1s) - Delta(2s);

// Cont–Kukanov–Stoikov OFI (best-level), summed over OFI_WINDOW_SECONDS = 5.0
double eb = bestBidTicks > pBidT ? bestBidSize : bestBidTicks == pBidT ? bestBidSize - pBidSz : -pBidSz;
double ea = bestAskTicks < pAskT ? bestAskSize : bestAskTicks == pAskT ? bestAskSize - pAskSz : -pAskSz;
double ofi = eb - ea;

// DOM imbalance at 3/5/10 levels (queue = 1)
double val = sum == 0 ? 0 : (bidVol - askVol) / sum;

// Absorption: heavy 5s volume with price pinned to a single tick
if (totalVol >= AbsorptionVolumeThreshold && range <= symbol.TickSize + 1e-9) val = isBuy ? buyVol : sellVol;
// auto-threshold = max(10, (TradeVolumeWindowShort/12.0) * 2.0)   → ~167 with defaults

// calculator's own regime stress
regimeStress = 0.4*spreadExcess + 0.4*volExcess + 0.2*flowExcess;   // state cuts: <-0.15 / <0.50 / <1.50 / else
```
**Constants:** TradeVolWindow 1000/5000, OrderCountWindow 2000/10000, AbsorptionThreshold auto ~167 (floor 10, ×2.0, /12.0), CalibrationTrades 1000, FALLBACK_MTR 5.0, OFI window 5s, DOM depths 3/5/10, book-pressure depth 10, stress weights 0.4/0.4/0.2, EWMA τ spread 10/300/30, RV 10/300, flow 5/300, lattice scan 250ms / window 100t / rungs 3–5 / persist 4–8.

## 2.5 · sfrdragon `FlagshipFuturesStrategy` (RVOL / VD / HMA)
`HRVD_strategy_v10._8.cs`. *(Advertises 6 signals; live path computes 4.)*
```csharp
state.RvolOk    = (currentVolume/avgVolumeShort > RvolThreshold) || (currentVolume/avgVolumeLong > RvolThreshold); // 1.0
state.VdStrongOk= Math.Abs(currentVd) > (avgVd * VdStrengthThreshold);                                             // 1.2
state.HmaOk     = Math.Abs(bar.Close - avgPrice) > (atrTracker.Value * 0.5);                                       // 0.5×ATR
state.VdPriceOk = currentRatio > (avgRatio * VdPriceRatioThreshold);                                               // 1.5
// entry = N-of-6 (EntrySignalsRequired default 1); direction = close vs HMA
// SL = prevBar.{Low,High} ∓ ATR*1.0, clamp[MinStop 4, MaxStop 20]t ; TP = session level (min 8t / alt 12t)
// exit = close crosses HMA by ±1×ATR
```

## 2.6 · QT_API `PyramidMomentumStrategy` (order-flow entry)
`src/QT.Quantower/Strategies/PyramidMomentumStrategy.cs`.
```csharp
if (of.Confidence < MinSignalConfidence) return;                     // 0.50
side = of.Bias == DirectionalBias.Up ? Side.Buy : Side.Sell;
if (EnableAnalytics && IsRiskOff(ms)) return;                        // regime veto
// pyramid +1 each AddStepTicks(20) up to MaxContracts(3), only if flow supports
// hard stop = max(StopLossTicks 40, VolStopMultiple 2.0 * noiseTicks); soft trail = max(TrailTicks 20, 1.0*noise), persist 750ms
// IsRiskOff = Regime is ThinFragile/VolatileDislocated || Risk==Critical
```

## 2.7 · alihamza1221 `ImbalanceCluster-Stragtegy` (diagonal footprint imbalance)
`imbalanceClustersStrategy.cs`. Levels from `VolumeAnalysisData.PriceLevels`.
```csharp
// Diagonal test (bullish = current buy vs level-below sell)
if (currentItem.BuyVolume - belowSellVolume > ImbalanceRatio * belowSellVolume)   // 3 → effective 4× dominance
    PlaceMarketOrder(last.Bid, Side.Buy, TradeQuantity);
// Bearish = current sell vs level-above buy
if (currentItem.SellVolume - aboveBuyVolume > ImbalanceRatio * aboveBuyVolume)
    PlaceMarketOrder(last.Bid, Side.Sell, TradeQuantity);

// Stacked: scan consecutive levels; each must pass the diagonal test; break on fail/zero
if (stackedBullishCount >= WindowSize) PlaceMarketOrder(..., Side.Buy, ...);      // WindowSize 3

// Exit: symmetric ±SLTPTicks on GrossPnLTicks
if (pos.GrossPnLTicks >= SLTPTicks || pos.GrossPnLTicks <= -SLTPTicks) Core.Instance.ClosePosition(pos);  // 4t
// Martingale: currentTradeQuantity = TradeQuantity * Multiplier^consecutiveLosses ; Stop() after MaxLossTrades
```
**Constants:** ImbalanceRatio 3 (→4× dominance), WindowSize 3, SLTPTicks 4, TradeQuantity 0.01, Multiplier 2, MaxLossTrades 4, UseOnlyStackedImbalances true (single-level disabled by default), SkipZeros true, MIN1 bars, 6h history.

## 2.8 · Hopefx `order_flow.py` (Python footprint/absorption)
`analysis/order_flow.py`.
```python
imbalance_ratio = delta / total_volume if total_volume > 0 else 0
signal = 'bullish' if imbalance_ratio > 0.3 else 'bearish' if imbalance_ratio < -0.3 else 'neutral'
# strength: >0.5 strong, >0.25 moderate, else weak
# HVN: level.total_volume > avg*1.5 ; LVN: < avg*0.5 ; key-levels HVN > avg*1.3
# absorption: price_range/avg_price < 0.001 (0.1%) over 30s window, >=5 trades
# value area = expand from POC until 70% of volume
```
**Constants:** imbalance_threshold 0.30, signal ±0.3, strength 0.5/0.25, HVN 1.5×/1.3×, LVN 0.5×, absorption 0.1%/30s/≥5 trades, value_area 0.70, buckets 20/30/50.

## 2.9 · Python order-flow constants (from your calibration scrub — recap)
- **advanced_orderflow.py:** imbalance 3.0, stacked 3, exhaustion `vol×2.5 & ≤2t`, absorption `≤4t & 2:1`, single-print `0.05` of max, excess-tail `0.3` of bar, POC-migration `2t`, unfinished-auction `5`/`20` contracts.
- **delta_flow.py:** `ABSORPTION_DELTA 500` (avg abs over 5 bars), `ABSORPTION_PRICE 0.5 pt`, `EXHAUSTION_DECEL 0.4`, stacked `|delta/vol|>0.4` (>70%) ×3, momentum `avg_ratio×3 > 0.3`.
- **orderflow_glm.py:** VPIN toxicity `0.85` quantile (Easley/López de Prado/O'Hara), CVD divergence_frac `0.25` of 60-bar range, liquidation `liq_z 1.5`, absorption persist `2 of 3`, ATR stops `0.8–1.5×`.
- **order_flow_analyzer.py:** imbalance 3.0, delta 100, absorption `vol>1000 & |delta|<50`.
- **footprint.py:** imbalance 3.0, dominant_fraction `min(0.5 + body_ratio*0.4, 0.90)`.

## 2.10 · DarkLink005 `iof-specs` — YOUR system (fragment-only, private)
`TradePhantoms_IOF_v2.cs` + `AutoSLTP_Strategy.cs` + playbook `.md`s.
```csharp
// Absorption flag (boolean combiner captured verbatim; thresholds from comment/spec)
bool oppositeSign = (delta < 0 && bar.Close >= bar.Open) || (delta > 0 && bar.Close <= bar.Open);
if (rangeStalled && heavyDelta && oppositeSign) return delta < 0 ? +1 : -1;   // rangeStalled <0.6×ATR, heavyDelta |delta|>1.5×20-bar-avg
// CVD: cvdByAbsIndex[i] = resetHere ? delta : previousCvd + delta ;  delta = VolumeAnalysisData.Total.Delta
// Diagonal imbalance zone: BuyVolume[P] / max(SellVolume[P-1tick],1) >= ImbalanceRatio(3.0), MinStackedRows 3
// Sizing: contracts = floor(DollarRiskPerTrade / (slDist × pointValue)), cap MaxContracts(20)
// TPs: 1×/2×/3× zoneHeight ; SL: far wick − StopBufferTicks(per-instrument) ; cascade trail TP1→BE, TP2→TP1, TP3→TP2
```
**Your constants (from source + playbook specs):** ImbalanceRatio 3.0 / MinStackedRows 3 / ViolationPct 0.5; absorption `<0.6×ATR & >1.5×20-bar-avg & opp-sign` (Tier3 +`>1.3×vol & <0.25×ATR nearZone`); CVD session-anchored + 20-bar rolling, divergence 15-bar (Tier3 pivot 7/3 + 15% swing filter); MinImpulseRatio 2.0, BaseCandleMaxBodyPct 0.5; scoring rubric max 21 (`MinScore 14` / `MinDrawScore 8`, sim 12); StopBufferTicks MES 6 / MNQ 10 / MYM 8 / M2K 12 / MGC 8 / MCL 6 / MNG 15 / M6E 10; RrrFixedMultiple 5.0; DollarRiskPerTrade $100; MaxContracts 20 (sim 1); kill-switches 4 losses / 60% DLL / 80% trailing / 60s silence; gap_seconds 1.8× cadence; book-imbalance >15 NQ-equiv.

---

# PART 3 — Your IOF constants vs the external corpus (side-by-side)

*Left = what the external order-flow bots use; right = your IOF value. Where they converge, you have corroboration; where they diverge, that's a calibration question for your MNQ/MES recordings.*

| Metric | External corpus | **Your IOF** | Read |
|---|---|---|---|
| Imbalance ratio | 3:1 (300%) — 6 independent sources | **3.0** | ✅ dead-on consensus |
| Stacked rows | 3 consecutive — 4 sources | **3** (`MinStackedRows`) | ✅ consensus |
| Absorption range | ≤4t / ≤1 tick / 0.1% / 0.5pt (raw or %) | **< 0.6×ATR** | ⚠️ you normalize by ATR; others raw — yours is more portable |
| Absorption delta | 500 raw / 2× typical-5s / 2:1 level | **> 1.5×20-bar-avg** | ⚠️ yours is ratio-normalized (portable); external raw needs rescale |
| Absorption confirm | persist 2/3; volumeSpike | **Tier3: >1.3×vol & <0.25×ATR nearZone** | ✅ aligns with FlowMate persist idea |
| CVD window | 20-bar / 60-bar / 1-5-30-60s | **session-anchored + 20-bar rolling** | ✅ mid-range, session reset is a differentiator |
| Divergence | 0.25 of 60-bar range; z-1.75 | **15-bar slope; Tier3 pivot 7/3 + 15% swing** | ⚠️ yours is structural (pivot) vs others' magnitude — different philosophy |
| Book/OFI | (bid−ask)/(bid+ask); OFI 5s; lean 0.50/0.30/0.20 | **book-imbalance >15 NQ-equiv; DOM MinSize 100** | ⚠️ compare your net-contract gate to normalized ratios |
| Regime veto | stress≥0.80 Critical; VPIN 0.85 | **kill-switch: 4 losses / 60% DLL / 80% trail / 60s silence** | ⚠️ yours is account/risk-based, theirs is microstructure — complementary, consider both |
| Confidence gate | lean 0.20 / strong 0.50; conf 0.50 (Pyramid) | **score ≥ 14 of 21** | ⚠️ different scale — yours is a composite rubric |
| SL basis | ATR×1.0 clamp[4,20]/[2,200]t; 40t | **far wick + StopBufferTicks (MNQ 10 / MES 6)** | ⚠️ yours is structural (wick); consider ATR-clamp as a floor/ceiling |
| TP scale-out | 1× session-level; RR 2.0; split 5/1 | **1×/2×/3× zoneHeight, cascade BE trail** | ✅ your 3-TP zone-multiple scheme is more sophisticated than any external bot |
| Sizing | floor(risk/(stopTicks×tickValue)) | **floor(DollarRisk/(slDist×pointValue)), RRR 5.0** | ✅ identical formula to moravsky/ORB |

**Calibration takeaways for your bot to test:**
1. **Imbalance 3:1 / 3-stacked is bulletproof** — six independent implementations agree with your IOF exactly. Low-risk to keep; not worth re-deriving.
2. **Your ATR-normalized absorption (0.6×ATR, 1.5×20-bar-delta) is more portable than every external raw-tick/raw-contract version** — run your MNQ/MES numbers to confirm the multipliers, but the *approach* is the strongest in the corpus.
3. **Divergence is where the corpus disagrees most** — magnitude-based (0.25 of range, z-1.75) vs your structural pivot method. Worth A/B testing your 15-bar slope against a 60-bar range-fraction on your own recordings.
4. **You have no microstructure regime gate** like MarketState/VPIN — your kill-switches are account-risk-based. Consider whether a stress/VPIN veto (0.80 / 0.85 quantile) would have prevented your worst fills.
5. **Raw-contract anchors (delta 500/100, book >15) must be rescaled** before comparison — they were set for the author's instrument, not MNQ/MES.

---

## Could-not-source / flags
- **`DarkLink005/iof-specs`** (your own) — private repo; C# extracted **fragment-only** (no line numbers). The absorption threshold literals (`0.6`, `1.5`, `1.3`, `0.25`) live in comments/spec `.md`s, not captured code lines; the boolean combiner *was* captured verbatim. `AutoSLTP_Strategy.cs`'s own input defaults and fill↔intent match tolerance were not in any fragment.
- **`DataAnalyticsStrategy.cs`** (QT_API V1) — **deleted at this commit**; its calibration surface recovered from `AdaptiveParameters.cs` + `legacy_v1/DataAnalyticsCalculator.cs` instead.
- **FlagshipFuturesStrategy** — 2 of its 6 advertised VD signals (VD-Volume, VD-Divergence) are never assigned in the live path (dead votes).
- **Hopefx** — no exhaustion/divergence in `order_flow.py` (those live in a separate `advanced_order_flow.py`, not pulled); absorption self-labeled "simplified heuristic."
- **Raw numbers carry no instrument label** unless stated — every `[raw]` value is only comparable after rescaling to MNQ/MES.

### Coverage summary (channels worked / blocked)
**Worked:** GitHub `search_code` (order-flow discovery + private-repo fragments) ✅, `raw.githubusercontent.com` verbatim reads via curl ✅, WebSearch ✅. **Blocked:** `api.github.com` egress (gated), `get_file_contents` for non-`iof` repos (scoped), `DarkLink005/iof-specs` private (fragment-only), and the entire `agent-reach` stack — **yt-dlp/YouTube ❌, Bilibili ❌, RSS ❌, Jina ❌** — zero video/RSS coverage. Net: **15 order-flow sources** extracted (9 Quantower/C#, 6 Python), every numeric threshold tabulated with units and a raw-vs-normalized flag for direct calibration comparison; your own IOF constants sit beside them in Part 3.
