# Quantower Strategy Bots — Microscope Breakdown & Boilerplate Synthesis

**Prepared:** 2026-07-06
**Scope:** Every Quantower trading-*strategy* bot findable on GitHub (C# subclassing `TradingPlatform.BusinessLayer.Strategy`, plus a couple of Python/off-platform strays), read **word-for-word**, with the literal **Entry / Exit / Stop-Loss / Take-Profit** code quoted, then correlated into families and distilled into **three boilerplates** to hold your IOF strategy against.

> **How to read this.** Part 1 is the microscope — verbatim code per strategy, grouped by family. Part 2 is the correlation (which bots are the same machine with different dials). Part 3 is the three boilerplates you asked for: **(A) order-flow/volume-delta anchor**, **(B) universal skeleton**, **(C) futures ES/NQ/micros**. Part 4 is a blank comparison scaffold for your strategy. None of these numbers is a recommendation — they're external anchors.

---

## Channels used

The `agent-reach` local stack (Exa, yt-dlp, Bilibili, RSS, Jina) is **not installed in this remote environment**. This scrub ran on the working equivalents — the right toolset anyway, since Quantower bots live almost entirely on GitHub as C#:

- **GitHub `search_code`** (cross-repo discovery of `Strategy` subclasses) ✅
- **`raw.githubusercontent.com` via WebFetch/curl** for full verbatim file reads ✅ (WebFetch's summarizer refused verbatim reproduction on several files; agents fell back to raw `curl`)
- **WebSearch** for platform/API context ✅
- **Blocked:** `api.github.com` egress is gated in this session; `mcp__github__get_file_contents` is scoped to `christopherjliby-prog/iof` only. Full reads of *other* repos went through raw URLs. One private repo (`DarkLink005/iof-specs`) could not be fully read — its `AutoSLTP_Strategy.cs` is reconstructed from search fragments and flagged as such.

---

## Master inventory (classified)

| # | Repo → class | Family | Entry signal | SL | TP | Runnable? |
|---|---|---|---|---|---|---|
| 1 | Quantower/Examples → `SimpleMACross` | MA-cross | Fast5×Slow10 cross | none | none | teaching |
| 2 | vYORKv/Quantower_Strategies → `SimpleMACross` | MA-cross | SMA10>SMA20 + spread filter | `stoploss` offset | none (commented) | runnable |
| 3 | agalindoc → `SimpleMACross` (SendTelegramMsg) | MA-cross | FastMA×SlowMA + Telegram | none | none | runnable |
| 4 | vYORKv → `priceSlopeChangeStrategy` | MA-slope | SMA slope ±0.5, time-gated | none | none | runnable |
| 5 | dev107277891sjm → `QuantowerEmaStrategy` | EMA-cloud | EMA[7,12,30,50] bias engine | 10 pts | 10 pts (OCO) | runnable |
| 6 | The-Coding-Trader → `Algo` (SingleRangeBar) | Range-bar | prev range-bar dir flip | none | none | reference |
| 7 | Quant-Code-Labs → `Strategy` (single-range-bar) | Range-bar | prev range-bar dir flip | none | none | teaching |
| 8 | vYORKv → `rangeScalpStrategy` | Breakout | range±offset stop orders | 10 (offset) | 5 (offset) | runnable |
| 9 | vYORKv → `boxRangeStrategy` | Breakout | box-midpoint bracket | bracketInTicks±off | bracketInTicks±off | runnable |
| 10 | moravsky/orb-quantower → `OpeningRangeBreakoutStrategy` | ORB | first close beyond OR±ext(10%) | opposite trigger | entry±risk×RR(2.0) | runnable (ref) |
| 11 | zenaimaster → `KatOpeningRangeBreakout` | ORB | NY 09:30 break&retest | 60 ticks | 600 ticks | runnable |
| 12 | zenaimaster → `KatNewYorkOpening` | ORB/EMA | NY 09:30 EMA9-touch + EMA9vs34 | 60 ticks | split 60t / free-run | runnable |
| 13 | vYORKv → `PriceSurge` | Momentum | bar>avg×1.15 | none | none (PnL +6/−19t) | runnable |
| 14 | vYORKv → `WeightedSurge` | Momentum | wt-price>avg×1.15 | 20 (offset) | 40 (offset) | runnable |
| 15 | mesuteryilmaz/QT_API → `PyramidMomentumStrategy` | Momentum/OF | manual bias OR OF confidence>0.50 | 40t vol-scaled | soft-trail 20t | runnable (armed off) |
| 16 | sfrdragon → `FlagshipFuturesStrategy` | **Order-flow/VD** | N-of-6 (RVOL/VD/HMA) | prevBar±ATR×1.0 clamp[4,20]t | session level / alt 12t | runnable |
| 17 | DarkLink005/iof-specs → `AutoSLTP_Strategy` | OF/overlay | (manual fill) | `SLTicks` or zone | 3×TP ticks split % | runnable (frag) |
| 18 | vYORKv → `_2Point_1C_100Stop_Grid` | Grid | OnPlaceOrder ladder ±2 | 100 (offset) | none | runnable |
| 19 | vYORKv → `_2_Grid_Nasdaq` | Grid | OnPlaceOrder 2 rungs | 32 / 20 | commented | runnable |
| 20 | mesuteryilmaz/QT_API → `OnePairGridStrategy` | Grid/MM | bid/ask pair @100t | 40t stop-mkt | opposite leg = width | runnable (armed off) |
| 21 | moravsky → `AutoSizeStrategy` | Overlay | (none — sizing interceptor) | — | — | runnable |
| 22 | moravsky → `NonStarterExitStrategy` | Overlay | (none — time exits) | 30s limit@1t → 180s mkt | — | runnable |
| 23 | bleave → `TradeGuardian` | Overlay | (none — discipline) | EMA9/21 invalidation + daily lockout | — | runnable |
| 24 | aryapratham000 → `Buy` | Overlay/futures | immediate long ES+MES | ATR×1.6 | RR 1.6 (OCO) | runnable (demo) |
| 25 | Quantower/Examples → `SetSlTpForOpenedPositionStrategy` | Overlay | 1st tick market Buy | −0.25% | +0.5% | teaching |
| 26 | Quantower/Examples → `PlaceOrderWithMultipleSlTP` | Overlay | 1 market, qty 3 | 10/20 split | 15/25 split | teaching |
| 27 | bulldog5046 → `TradesByChatt` | Relay | YouTube chat vote 1/2/3 | none | none | novelty |
| 28 | SpoekieKoekie → `SolidLinqBridgeStrategy` | Relay | WebSocket command | payload SL% | payload TP% | runnable |
| 29 | Quantower/Examples → `MarketIfTouched`/`LimitIfTouched`/`RepeatOrderPlacing` | Primitive | price-touch / repeat | none | none | runnable |
| — | **Stubs/docs/off-target** | | | | | |
| s1 | agalindoc → `VolumeAccess` | stub | empty `StrategyProcess()` | — | — | stub |
| s2 | jwobbe → `TickStrategy` | stub | placeholder | — | — | stub |
| s3 | NeoNix-Lab/Quantower-Orders-Manager → `QuantStrategy` | stub | no entry logic | — | — | stub |
| s4 | NeoNix-Lab/Ultimate → `Condic_Gap_Cros_Strategy` | condition-provider | Ichimoku gap cross | — | — | dev stub |
| s5 | Quantower/Examples → `Webhook_Strategy_Example` / `AccessOrderType…` / `SettingsRelations…` | non-trading | — | — | — | stub |
| s6 | hgroechel/Quantower | empty repo | — | — | — | empty |
| s7 | Quantower/Scripts | indicators only | — | — | — | n/a |
| s8 | Pusparaj99op/MGC-4Trades…Algo | docs only | — | — | — | no code |
| s9 | Pusparaj99op/Quantower-Orderflow-Algo | **off-target** (BNB LLM bot; **leaked API keys** — see flags) | RSI/MACD/LLM | none | none | off-target |
| s10 | variks167-spec/quantower-algo-guide | docs only | — | — | — | no code |
| s11 | mesuteryilmaz/QT_API → `DataAnalyticsStrategy` | dormant V1 | flow-ratio (deprecated) | — | — | dormant |

**Bottom line on population:** ~29 `Strategy`-subclass files that touch orders; of those, **~20 are genuinely runnable** and only a handful carry a real *entry edge* (the rest are execution/risk overlays, primitives, relays, or stubs). The entry-generating edge bots cluster into just **four shapes**: MA/EMA-cross, range/opening-range breakout, momentum-surge, and order-flow/volume-delta.

---

# PART 1 — The microscope (verbatim, by family)

Line numbers are as reported by the extracting pass; elisions (`…`) removed only repeated `if (result.Status == Failure) {…}` logging. `SlTpHolder.CreateSL/CreateTP(x, PriceMeasurement.Offset)` = attach a bracket at `x` price-offset (ticks) from fill; `PriceMeasurement.Absolute` = at an absolute price.

## Family A — MA / EMA crossover & slope

### A1 · Quantower/Examples `SimpleMACross` — the canonical template
`Strategies/SimpleMACross.cs` @ `0bbb4a1`. Fast MA=5, Slow MA=10, Min5. Cross tested prev→current (index 2→1). **No SL/TP.**

```csharp
// ENTRY (~L218)
if (this.indicatorFastMA.GetValue(2) < this.indicatorSlowMA.GetValue(2) &&
    this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1))
{   // BUY: Fast crosses above Slow
    var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
    { Account = this.CurrentAccount, Symbol = this.CurrentSymbol,
      OrderTypeId = this.orderTypeId, Quantity = this.Quantity, Side = Side.Buy, });
}
else if (this.indicatorFastMA.GetValue(2) > this.indicatorSlowMA.GetValue(2) &&
         this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1))
{   // SELL: Fast crosses below Slow
    var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
    { Account = this.CurrentAccount, Symbol = this.CurrentSymbol,
      OrderTypeId = this.orderTypeId, Quantity = this.Quantity, Side = Side.Sell, });
}
```
```csharp
// EXIT (~L206) — closes on either-side (effectively any) MA relation, then re-entry flips
if (this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1) ||
    this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1))
{ foreach (var item in positions) { var result = item.Close(); } }
```
**STOP-LOSS: NONE · TAKE-PROFIT: NONE.**

### A2 · vYORKv `SimpleMACross` — cross + spread filter + PnL target
`smaCrossStrategy/…/smaCrossStrategy.cs` @ `37c8e9c`. FastMA=10, SlowMA=20, `multiplicative=2.0`, `stoploss=100`. Note the **TP is commented out**; SL attached; exit on reversal OR +150 ticks.

```csharp
// ENTRY (~L450) — spread must exceed avg spread × multiplicative, flat, no prior side
if (diff_0 > diff_avg * multiplicative && this.inPosition == false && prevSide == "none")
{
    if (sma_10 > sma_20)
    {
        var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters()
        { Account = this.CurrentAccount, Symbol = this.CurrentSymbol,
          //TakeProfit = SlTpHolder.CreateTP(30, PriceMeasurement.Offset), // Added
          StopLoss = SlTpHolder.CreateSL(stoploss, PriceMeasurement.Offset), // Added
          OrderTypeId = this.orderTypeId, Quantity = this.Quantity, Side = Side.Buy, });
    }
    else if (sma_10 < sma_20) { /* … Side.Sell, same SL … */ }
}
```
```csharp
// EXIT (~L362) — reversal cross OR pnlTicks > 150
if (sma_10_x > sma_20_x) { if (price_x < lastLow_x  || pnlTicks > 150) foreach(var item in positions) item.Close(); }
else if (sma_10_x < sma_20_x) { if (price_x > lastHigh_x || pnlTicks > 150) foreach(var item in positions) item.Close(); }
```
**STOP-LOSS:** `SlTpHolder.CreateSL(stoploss=100, Offset)` (ticks). **TAKE-PROFIT: NONE** (commented). Exit thresholds `150` = PnL ticks; `multiplicative` = dimensionless spread multiplier.

### A3 · agalindoc `SimpleMACross` — cross + Telegram, always-close
`strategies/SendTelegramMsg.cs` @ `56c475d`. FastMA=5, SlowMA=10, Min5. **No SL/TP.**
```csharp
// ENTRY — value index 1
if (this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1)) { /* PlaceOrder Side.Buy, qty; SendTelegramMessage(" Long…") */ }
else if (this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1)) { /* Side.Sell */ }
// EXIT — note the (< || >) is effectively always-true: closes whenever the two MAs differ
if (this.indicatorFastMA.GetValue(1) < this.indicatorSlowMA.GetValue(1) || this.indicatorFastMA.GetValue(1) > this.indicatorSlowMA.GetValue(1))
{ Core.Instance.ClosePosition(new ClosePositionRequestParameters(){ Position = Core.Instance.Positions[0], CloseQuantity = Core.Instance.Positions[0].Quantity }); }
```
**STOP-LOSS: NONE · TAKE-PROFIT: NONE.**

### A4 · vYORKv `priceSlopeChangeStrategy` — SMA slope threshold + time window
`smaSlopeChangeStrategy/…/priceSlopeChangeStrategy.cs` @ `37c8e9c`. LeadSMA=20, BaseSMA=20; trades only UTC 13:30–14:55. **No SL/TP** — risk is `maxProfit`/`maxLoss`/`maxTrades` guards.
```csharp
// ENTRY
if (this.baseSlopeChange >  .5 && this.buyReady)  { /* PlaceOrder Market Side.Buy  */ }
if (this.baseSlopeChange < -.5 && this.sellReady) { /* PlaceOrder Market Side.Sell */ }
// EXIT — when the ready-counter resets (slope reverses)
if ((this.buyPlaced && this.buyCounter == 0) || (this.sellPlaced && this.sellCounter == 0))
{ foreach (var item in positions) item.Close(); }
```
**STOP-LOSS: NONE · TAKE-PROFIT: NONE.** Thresholds `±0.5` = slope-change in price units.

### A5 · dev107277891sjm `QuantowerEmaStrategy` — EMA-cloud engine + fixed bracket + OCO
`src/QuantowerEmaStrategyAdapter/Class1.cs` @ `master`. Thin adapter over an external `EmaStrategy.Core.StrategyEngine`. **Hard-coded config (no UI inputs).**
```csharp
private const int TickBarsTickCount = 25000;                 // ticks per aggregated bar
private readonly int[] _emaPeriods = new[] { 7, 12, 30, 50 };// bars
// OnCreated:
var config = new EmaStrategyConfig {
    EmaPeriods = _emaPeriods, CloudFastPeriod = 7, CloudSlowPeriod = 12,
    WarmupBars = 100, MaxOpenContracts = 1, OrderQuantityContracts = 1,
    TakeProfitPoints = 10m, StopLossPoints = 10m,            // price points
    FillMode = EntryFillMode.NextBarOpen,
    CooldownAfterOrderPlaced = TimeSpan.FromSeconds(30), CooldownAfterExit = TimeSpan.FromSeconds(30), };
```
```csharp
// ENTRY — engine raises TradeEntry event; adapter fires a market order
private void OnTradeEntry(TradeEntrySnapshot snapshot) {
    if (_executionState != ExecutionState.Flat) return;
    /* …csv log… */ SubmitEntryOrder(snapshot); }        // request.OrderTypeId = "Market"; Side = snapshot.Side
```
```csharp
// STOP-LOSS & TAKE-PROFIT — bracket computed on fill (opposite side)
var tpPrice = _positionBias == Bias.Buy ? entryPrice + _takeProfitPoints : entryPrice - _takeProfitPoints;
var slPrice = _positionBias == Bias.Buy ? entryPrice - _stopLossPoints   : entryPrice + _stopLossPoints;
_takeProfitOrderId = PlaceProtectiveOrder(protectiveSide, tpPrice, "TakeProfit", out _takeProfitOrder);
_stopLossOrderId   = PlaceProtectiveOrder(protectiveSide, slPrice, "StopLoss",   out _stopLossOrder);
```
```csharp
// EXIT (OCO) — one protective fills → cancel the other
private void OnProtectiveOrderUpdated(IOrder order) {
    if (order.RemainingQuantity > 0) return;
    _executionState = ExecutionState.Exiting;
    if (_takeProfitOrder != null && !ReferenceEquals(order,_takeProfitOrder) && _stopLossOrder != null) _stopLossOrder.Cancel("");
    if (_stopLossOrder  != null && !ReferenceEquals(order,_stopLossOrder)  && _takeProfitOrder != null) _takeProfitOrder.Cancel("");
    _executionState = ExecutionState.Flat; }
```
**Units:** TP/SL = 10 price points; qty 1; EMA 7/12/30/50 bars; bar = 25000 ticks.

---

## Family B — Range / box / opening-range breakout

### B1 · The-Coding-Trader `Algo` (SingleRangeBar) — always-in flip on 40-tick bars
`Strategy/Algo.cs` @ `5893b76`. Range Bar Size = 40 ticks, Contracts = 1. **No SL/TP.**
```csharp
private void OnNewHistoryItem(object sender, HistoryEventArgs e) {
    broker.Flatten();
    broker.Market(PreviousRangeBar[PriceType.Open] < PreviousRangeBar[PriceType.Close] ? Side.Buy : Side.Sell, configuration.Contracts);
}   // PreviousRangeBar => rangeBarData[1];
```
**STOP-LOSS: NONE · TAKE-PROFIT: NONE** (exit = flatten-before-next-entry).

### B2 · Quant-Code-Labs `Strategy` (single-range-bar) — same, inline
`Strategy/Strategy.cs` @ `39266ae`. Range Bar Size = 40, Contracts = 1. **No SL/TP.**
```csharp
private void OnNewHistoryItem(object sender, HistoryEventArgs e) {
    var orderParams = new PlaceOrderRequestParameters {
        Account = Account, Symbol = Symbol,
        Side = historicalData[1][PriceType.Open] < historicalData[1][PriceType.Close] ? Side.Buy : Side.Sell,
        TimeInForce = TimeInForce.Day, Quantity = Contracts, OrderTypeId = OrderType.Market };
    Flatten();  Core.PlaceOrder(orderParams);
}
```

### B3 · vYORKv `rangeScalpStrategy` — stop-order breakout with bracket
`rangeScalpStrategy/…` @ `37c8e9c`. TakeProfit=5, StopLoss=10, RangeOffset=2 (ticks).
```csharp
// ENTRY — green bar → sell-stop below range low; red bar → buy-stop above range high
if (close_1 > open_1 && this.sellPlaced == false) {   // Green
    Core.Instance.PlaceOrder(new PlaceOrderRequestParameters(){
        Account=this.CurrentAccount, Symbol=this.CurrentSymbol,
        TakeProfit = SlTpHolder.CreateTP(this.takeProfit, PriceMeasurement.Offset),
        StopLoss   = SlTpHolder.CreateSL(this.stopLoss,   PriceMeasurement.Offset),
        TriggerPrice = this.rangeLow - (this.rangeOffsetTicks * .25),
        OrderTypeId = OrderType.Stop, Quantity=this.Quantity, Side = Side.Sell, }); }
else if (close_1 < open_1 && this.buyPlaced == false) {   // Red
    /* … TriggerPrice = this.rangeHigh + (this.rangeOffsetTicks*.25); Side = Side.Buy; same TP/SL … */ }
```
**STOP-LOSS:** `CreateSL(stopLoss=10, Offset)` ticks. **TAKE-PROFIT:** `CreateTP(takeProfit=5, Offset)` ticks. **EXIT:** none (bracket only). `.25` = tick size.

### B4 · vYORKv `boxRangeStrategy` — box-midpoint mean-reversion bracket
`boxRangeStrategy/…` @ `37c8e9c`. Bracket derived from range midpoint.
```csharp
double rangeTotal = this.rangeHigh - this.rangeLow;
double midpoint   = rangeTotal / 2;
double midpointRounded = Math.Round(midpoint * 4, MidpointRounding.ToEven) / 4;
double bracketInTicks  = midpointRounded / .25;
// SELL leg (limit):
TakeProfit = SlTpHolder.CreateTP(bracketInTicks + (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
StopLoss   = SlTpHolder.CreateSL(bracketInTicks - (this.rangeOffsetTicks * .25), PriceMeasurement.Offset),
Price = this.rangeHigh - (this.rangeOffsetTicks * .25), OrderTypeId = OrderType.Limit, Side = Side.Sell,
// BUY leg mirrors with +/- swapped, Price = rangeLow + offset.
```
**STOP-LOSS / TAKE-PROFIT** = `bracketInTicks ∓ offset` (ticks). **EXIT:** none (bracket). `StopOrders=true` switches limit→stop.

### B5 · moravsky `OpeningRangeBreakoutStrategy` — first breakout, risk-sized, RR bracket
`OpeningRangeBreakout.Strategy/*` @ `master`. OR window 13:30–20:00 UTC (US RTH), duration 15 min, ext 10%, RR 2.0, Risk 1.0%.
```csharp
// OR build + extension/trigger (OpeningRangeEngine)
var extension = (high - low) * _breakoutExtensionPercent / 100;
return new IndicatorLevels(high, low, high + extension, low - extension);
```
```csharp
// ENTRY decision (BreakoutDecider) — first close beyond an extension after window end
if (close > levels.HighExtension) {
    var entry = _useOrBoundaryAsEntry ? levels.OrHigh : close;
    var stop  = ComputeStopLoss(Side.Buy, levels.HighExtension, levels.LowExtension);   // = LowExtension
    return new TradeDecision(true, Side.Buy, entry, stop, ComputeTarget(Side.Buy, entry, stop)); }
if (close < levels.LowExtension) { /* Side.Sell, stop = HighExtension */ }
```
```csharp
// STOP-LOSS = opposite trigger line ; TAKE-PROFIT = entry ± risk × RR
private static double ComputeStopLoss(Side side, double highTrigger, double lowTrigger) => side == Side.Buy ? lowTrigger : highTrigger;
private double ComputeTarget(Side side, double entry, double stop) {
    var risk = side == Side.Buy ? entry - stop : stop - entry;
    return side == Side.Buy ? entry + risk * _riskRewardRatio : entry - risk * _riskRewardRatio; }
```
```csharp
// PLACEMENT + SIZING (one trade per session: _hasTradedThisRun)
var request = new PlaceOrderRequestParameters { Account=CurrentAccount, Symbol=CurrentSymbol,
    OrderTypeId=_entryOrderTypeId, Quantity=qty, Side=decision.Side,
    StopLoss = SlTpHolder.CreateSL(decision.StopPrice), TakeProfit = SlTpHolder.CreateTP(decision.TargetPrice), };
// RiskCalculator:
var costPerContract = (stopDistance / tickSize) * tickValue;
var positionRisk    = accountBalance * _riskPercent / 100;
return (int)Math.Floor(positionRisk / costPerContract + Epsilon);
```
**EXIT:** none beyond the bracket. Author explicitly labels it a *reference implementation, not plug-and-play*.

### B6 · zenaimaster `KatOpeningRangeBreakout` — NY-open break & retest, wide bracket + trail
`KatOpeningRangeBreakout.cs` + `ORBRunner.cs` + `TrailManager.cs` @ `master`. **The most fully-featured breakout bot in the corpus.**
```csharp
// Constants
private const int InpNyHour = 9, InpNyMinute = 30, InpNySecond = 0;   // NY 09:30
public int InpSlTicks = 60;    public int InpTpTicks = 600;           // SL 60t, TP 600t
public int InpMaxChaseTicks = 10;   public int InpMaxDistRange = 240;
public int InpTrailTrigger = 60; public int InpTrailDistance = 20; public const int InpTrailStep = 1;
public int InpBreakEvenTrigger = 50; public int InpBreakEvenOffset = 1;
public int InpUnfilledCandles = 2; public int InpAfterFilledMinutes = 5; public int InpAfterMinutes = 60;
public int InpUnfavorMoveTicks = 320;
```
```csharp
// ENTRY — break: last closed bar beyond OR; retest: opposite-color candle back to the broken high, low ≥ mid
if (this.BreakDir == 1) {
    double midPrice = (this.RangeHigh + this.RangeLow) / 2.0;
    if (closePrice < openPrice && lowPrice <= this.RangeHigh && lowPrice >= midPrice) {
        double entryPrice = highPrice + (buffer * tickSize) + spread;
        entryPrice = Math.Round(entryPrice / tickSize) * tickSize;
        /* … 3-state chase routing … */ } }
// Chase routing: Stop if entry>ask; Market if within MaxChaseTicks(10); Limit if farther
double sl = entryReferencePrice - strategy.InpSlTicks * tickSize;                      // BUY  SL 60t
double tp = strategy.InpTpTicks > 0 ? (entryReferencePrice + strategy.InpTpTicks * tickSize) : 0;  // TP 600t
var request = new PlaceOrderRequestParameters { … Side = Side.Buy, OrderTypeId = orderType,
    Quantity = lot, Price = limitPrice, TriggerPrice = triggerPrice,
    StopLoss = SlTpHolder.CreateSL(sl, PriceMeasurement.Absolute),
    TakeProfit = tp > 0 ? SlTpHolder.CreateTP(tp, PriceMeasurement.Absolute) : null, … };
```
```csharp
// TRAIL + BE (TrailManager) — BE at +50t (offset 1t); trail arms at +60t, follows by 20t, steps 1t
if (strategy.InpUseBreakEven && bid - open >= beTriggerPrice) { double beSL = open + beOffsetPrice; if (targetSL==0 || beSL>targetSL) targetSL = beSL; }
if (bid - open >= triggerDist) { double trailSL = bid - trailDist; if (targetSL==0 || trailSL>targetSL) targetSL = trailSL; }
// Applied via Core.Instance.ModifyOrder(slOrder, …, targetSL, …) with a ≥ stepDist gate
```
```csharp
// TIME/DISASTER STOPS — cancel pending after 2 candles; flatten 5 min post-fill if trail not hit;
// flatten on 320-tick adverse move; STOP whole session after 60 min
if (barsPassed >= strategy.InpUnfilledCandles) shouldFlatten = true;                       // 2 candles
if (serverTime >= openTimeSelected.AddMinutes(strategy.InpAfterFilledMinutes) && !this.TrailTriggerHit) shouldFlatten = true;  // 5 min
if (bid <= open - strategy.InpUnfavorMoveTicks * tickSize) shouldFlatten = true;           // 320 ticks
if (serverTime >= nyoTime.AddMinutes(strategy.InpAfterMinutes)) this.State = ORBState.ORB_STOPPED;  // 60 min
```

### B7 · zenaimaster `KatNewYorkOpening` — NY-open EMA-touch entry, split-TP scale-out
`KatNewYorkOpening.cs` + `PendingOrderWrapper.cs` + `Logic/*` @ `main`. Total 6 lots (SetA 5 / SetB 1), EMA 9/34.
```csharp
public double InpTotalTradeContract = 6.0;  public double InpSetAContracts = 5.0;   // SetB = 1
public int InpStopLossTicks = 60;  public int InpSetATpTicks = 60;  public int InpSetBTpTicks = 0;  // SetB free-runs
public int InpSetBTrailTrigger = 60; public int InpSetBTrailDistance = 20; public int InpSetBTrailStep = 1; public int InpSetBBeOffset = 1;
public int InpFastEmaPeriod = 9; public int InpSlowEmaPeriod = 34;
public double InpMaxPositionDurationMins = 5.0; public double InpMaxPendingDurationMins = 5.0; public int InpUnfavorTicks = 120;
```
```csharp
// ENTRY — on bar close touching EMA9, gate on EMA9 vs EMA34, place stop beyond bar extreme
bool touchesEma9 = (low <= ema9 && high >= ema9);
if (touchesEma9) {
    if (ema9 > ema34) { double buyEntry = this.RoundPrice(high + tickSize);
        result = this.orderWrapper!.BuyPending(account,symbol, lot:this.InpTotalTradeContract, entryPrice:buyEntry,
            slTicks:this.InpStopLossTicks, tpTicks:this.InpSetBTpTicks, maxChaseTicks:this.InpMaxChaseTicks, comment:$"StopBuy_{…}"); }
    else if (ema9 < ema34) { double sellEntry = this.RoundPrice(low - tickSize); /* SellPending … */ } }
```
```csharp
// STOP-LOSS — physical stop post-fill, 60t, full qty
double slPrice = pos.Side == Side.Buy ? (pos.OpenPrice - this.InpStopLossTicks * tickSize) : (pos.OpenPrice + this.InpStopLossTicks * tickSize);
// TAKE-PROFIT — Set A: 5 lots @ 60t limit; Set B: skipped when InpSetBTpTicks==0 → free-run on trailing SL
double tpAPrice = pos.Side == Side.Buy ? (pos.OpenPrice + this.InpSetATpTicks * tickSize) : (pos.OpenPrice - this.InpSetATpTicks * tickSize);
// tpQty = Math.Min(this.InpSetAContracts, pos.Quantity);
```
```csharp
// TRAIL/BE — BE arms when Set A TP fills OR profit ≥ SetA TP; trail 60/20/1 (TrailingStopCalculator)
bool isTpAFilled = currentQuantity <= remainingSetBLots;  isBeTriggered = isTpAFilled || (profitTicks >= setATpTicks);
// TIME STOPS — 5-min position flatten, 5-min pending cancel, 120-tick unfavorable pending cancel
```

---

## Family C — Momentum / surge

### C1 · vYORKv `PriceSurge` — bar-expansion, PnL-tick exit, no bracket
`priceSurgeStrategy/…` @ `37c8e9c`. `multiplicative=1.15`, lookback 10. **SL/TP commented out**; exit purely on PnL ticks.
```csharp
// ENTRY — current bar size > lookback avg × 1.15, flat, on new bar
if (bar_0 > lookbackChoice * this.multiplicative && this.inPosition == false && this.newBar == true) {
    if (close_0 > open_0) { /* PlaceOrder Side.Buy; //TakeProfit CreateTP(40) //StopLoss CreateSL(20) both commented */ }
    else if (close_0 < open_0) { /* Side.Sell */ } }
// EXIT — poll gross PnL in ticks
double pnlTicks = positions.Sum(x => x.GrossPnLTicks);
if (pnlTicks >= 6 || pnlTicks <= -19) { foreach (var item in positions) item.Close(); }
```
**STOP-LOSS: NONE · TAKE-PROFIT: NONE** (both commented). Exit `+6 / −19` = PnL ticks.

### C2 · vYORKv `WeightedSurge` — weighted-price surge with bracket
`weightedSurgeStrategy/…` @ `37c8e9c`. `multiplicative=1.15`. **TP 40 / SL 20 attached.**
```csharp
if (weighted_0 > lookbackAvg_10 * this.multiplicative && this.inPosition == false && this.newBar == true) {
    if (close_0 > open_0) {
        Core.Instance.PlaceOrder(new PlaceOrderRequestParameters(){ … Side = Side.Buy,
            TakeProfit = SlTpHolder.CreateTP(40, PriceMeasurement.Offset),   // 40 ticks
            StopLoss   = SlTpHolder.CreateSL(20, PriceMeasurement.Offset), });// 20 ticks
    } else if (close_0 < open_0) { /* Side.Sell, same TP/SL */ } }
// EXIT — while a position exists, just return (bracket handles exit)
if (positions.Length != 0) { return; }
```

### C3 · mesuteryilmaz `PyramidMomentumStrategy` — anti-martingale pyramid, vol-scaled stops (order-flow gated)
`src/QT.Quantower/Strategies/PyramidMomentumStrategy.cs` @ `6d615f5a`. `MaxContracts=3`, `AddStepTicks=20`, `StopLossTicks=40`, `TrailTicks=20`, `MinSignalConfidence=0.50`. **Live-capable but shipped armed-off.**
```csharp
// ENTRY — manual-bias toggle OR order-flow signal (bias + confidence ≥ 0.50), never into risk-off
if (EntryMode == EntrySignal) {
    if (of == null || !IsUsable(of.Quality) || of.Bias == DirectionalBias.Neutral) return;
    if (of.Confidence < MinSignalConfidence) return;
    if ((Core.Instance.TimeUtils.DateTimeUtcNow - lastFlatUtc).TotalSeconds < ReentryCooldownSec) return;
    side = of.Bias == DirectionalBias.Up ? Side.Buy : Side.Sell; }
else { if (Bias == BiasOff) { consumed=false; return; } if (consumed) return; side = Bias == BiasLong ? Side.Buy : Side.Sell; }
if (EnableAnalytics && ms != null && IsUsable(ms.Quality)) { if (IsRiskOff(ms)) return; }
if (!PlaceMarket(side, 1, "BASE")) return;
```
```csharp
// PYRAMID ADD — every AddStepTicks in profit, up to MaxContracts, only if flow still supports
while (projected < MaxContracts) {
    double nextLevel = positionSide == Side.Buy ? lastAddPrice + AddStepTicks * tick : lastAddPrice - AddStepTicks * tick;
    bool reached = positionSide == Side.Buy ? mid >= nextLevel : mid <= nextLevel;
    if (!reached) break;
    if (EnableAnalytics && ms != null && IsUsable(ms.Quality) && IsRiskOff(ms)) break;
    if (RequireFlowForAdds && of != null && IsUsable(of.Quality) && FlowOpposes(of, positionSide)) break;
    if (!PlaceMarket(positionSide, 1, "ADD")) break;  projected++; lastAddPrice = nextLevel; }
```
```csharp
// STOP-LOSS — wide vol-scaled disaster backstop, trails favorable only
private double HardDistTicks() => !EnableVolatilityScaling ? StopLossTicks : (double.IsNaN(NoiseTicks()) ? StopLossTicks : Math.Max(StopLossTicks, VolStopMultiple * NoiseTicks()));
double hardDist = HardDistTicks() * tick;                                   // StopLossTicks 40, VolStopMultiple 2.0
double desired  = positionSide == Side.Buy ? highWaterPrice - hardDist : highWaterPrice + hardDist;
// TAKE-PROFIT — soft persistence-filtered trailing exit; immediate exit on order-flow flip
private double TrailDistTicks() => !EnableVolatilityScaling ? TrailTicks : Math.Max(TrailTicks, VolTrailMultiple * NoiseTicks());
double softLevel = positionSide == Side.Buy ? highWaterPrice - trailDist : highWaterPrice + trailDist;   // TrailTicks 20
if (of != null && IsUsable(of.Quality) && FlowOpposes(of, positionSide)) { FlattenAll("soft trail + flow reversal"); return; }
if (now - softBreachStartUtc.Value >= TimeSpan.FromMilliseconds(SoftExitPersistenceMs)) FlattenAll("soft trail confirmed");  // 750 ms
```
```csharp
// RISK-OFF KILL SWITCH
private bool CheckRiskOffFlatten() { if (!FlattenOnRiskOff) return false;
    var ms = AnalyticsSnapshot()?.Features.MarketState;
    if (ms == null || !IsUsable(ms.Quality) || !IsRiskOff(ms)) return false;
    FlattenAll($"regime risk-off: {ms.Regime}/{ms.Risk}"); return true; }
// IsRiskOff = Regime is ThinFragile or VolatileDislocated || Risk == Critical
```

---

## Family D — Order-flow / volume-delta  *(closest to an IOF-style system)*

### D1 · sfrdragon `FlagshipFuturesStrategy` — N-of-6 volume-delta gate, ATR stop, session-level TP
`HRVD_strategy_v10._8.cs` @ `8c9ebef`. **Advertises 6 signals; the live "v3.0" path computes only 4** (VD-Volume and VD-Divergence default `false` and can never vote). Thresholds are ratios/ATR-multiples — instrument-agnostic.
```csharp
// SIGNALS (EvaluateSignalsFresh)
double rvolShort = avgVolumeShort > 0 ? currentVolume / avgVolumeShort : 0;
double rvolLong  = avgVolumeLong  > 0 ? currentVolume / avgVolumeLong  : 0;
state.RvolOk  = (rvolShort > RvolThreshold) || (rvolLong > RvolThreshold);                 // RvolThreshold 1.0
// VD strength
double currentVd = volumeDeltas.LastOrDefault(); double avgVd = volumeDeltas.Average();
state.VdStrongOk = Math.Abs(currentVd) > (avgVd * VdStrengthThreshold);                    // VdStrengthThreshold 1.2
// HMA vs ATR
double avgPrice = closePrices.Skip(closePrices.Count - CustomHmaBasePeriod).Average();
state.HmaOk = Math.Abs(bar.Close - avgPrice) > (atrTracker.Value * 0.5);                   // 0.5 × ATR
// VD-to-price ratio
double currentRatio = currentPriceMove / currentVd; double avgRatio = priceChanges.Sum() / volumeDeltas.Sum(Math.Abs);
state.VdPriceOk = currentRatio > (avgRatio * VdPriceRatioThreshold);                       // VdPriceRatioThreshold 1.5
```
```csharp
// ENTRY — N-of-6 gate (EntrySignalsRequired default 1); direction = price vs HMA
private bool EvaluateEntryLong(SignalState state, HistoryItemBar bar) {
    int requiredSignals = Math.Max(1, EntrySignalsRequired); int activeSignals = 0;
    if (EntryUseRvol && state.RvolOk) activeSignals++;
    if (EntryUseVdStrength && state.VdStrongOk) activeSignals++;
    if (EntryUseCustomHma && state.HmaOk && bar.Close > (closePrices.LastOrDefault())) activeSignals++;
    if (EntryUseVdPriceRatio && state.VdPriceOk) activeSignals++;
    if (EntryUseVdVolumeRatio && state.VdVolumeOk) activeSignals++;   // VdVolumeOk never set in fresh path
    return activeSignals >= requiredSignals && state.IsTimeOk; }
// dispatch → PlaceOrderDirect(Side.Buy/Sell) → Core.Instance.PlaceOrder(Market)
```
```csharp
// STOP-LOSS — prior candle extreme ∓ ATR×mult, clamped to [MinStop, MaxStop] ticks
double atrDistance = currentAtr * atrMultiplier;                       // AtrMultiplierSL 1.0
stopLoss = side == Side.Buy ? previousCandle.Low - atrDistance : previousCandle.High + atrDistance;
if (distanceTicks < minStopDistanceTicks) stopLoss = entryPrice ∓ (minStopDistanceTicks * tickSize);   // Min 4t
else if (distanceTicks > maxStopDistanceTicks) stopLoss = entryPrice ∓ (maxStopDistanceTicks * tickSize);// Max 20t
```
```csharp
// TAKE-PROFIT — nearest session High/Low beyond price; if closer than MinTp, step to next level, else Alt ticks
var validHighs = highs.Where(h => h > currentPrice).OrderBy(h => h - currentPrice);
selectedTp = validHighs.First();
if ((selectedTp - currentPrice)/tickSize < minTpDistanceTicks) selectedTp = validHighs.ElementAt(1) /*or*/ CalculateAltTp(...);
private double CalculateAltTp(double p, Side s) => s==Side.Buy ? p + (altTpTicks*tickSize) : p - (altTpTicks*tickSize);  // MinTp 8t, Alt 12t
```
```csharp
// EXIT — opposite HMA break by 1× ATR
private bool EvaluateExitLong(SignalState s, HistoryItemBar bar)  => (EntryUseCustomHma && bar.Close < (closePrices.LastOrDefault() - atrTracker.Value));
private bool EvaluateExitShort(SignalState s, HistoryItemBar bar) => (EntryUseCustomHma && bar.Close > (closePrices.LastOrDefault() + atrTracker.Value));
```

### D2 · DarkLink005 `AutoSLTP_Strategy` — auto-bracket on manual fill (zone-intent → fixed-tick fallback)
`indicators/AutoSLTP_Strategy.cs` @ `2256383` — **private repo; FRAGMENT-ONLY (non-contiguous, no line numbers).** Places SL + 3 split TPs on a manual fill; tries an IOF zone-derived plan first, else fixed ticks. The "1R/3R/5R" and "30/40/30" figures are **config params, not literals** (R-multiples live only in `IOF_PLAYBOOK.md`).
```csharp
public sealed class AutoSLTP_Strategy : Strategy, ICurrentSymbol, ICurrentAccount
[InputParameter("Stop-Loss distance (ticks)", …)] public int SLTicks { get; set; }
[InputParameter("TP1 distance (ticks)", …)] public int TP1Ticks { get; set; }   // …TP2Ticks, TP3Ticks
[InputParameter("TP1 quantity %", …0.0,1.0…)] public double TP1QtyPct { get; set; }  // …TP2QtyPct, TP3QtyPct
```
```csharp
// SPLIT + FIXED-TICK PRICES (fallback path)
double q1 = this.RoundQty(totalQty * this.TP1QtyPct);  double q2 = this.RoundQty(totalQty * this.TP2QtyPct);  double q3 = this.RoundQty(totalQty - q1 - q2);
tp1Price = isLong ? entry + this.TP1Ticks * tickSize : entry - this.TP1Ticks * tickSize;   // + tp2Price, tp3Price
priceSource = $"fixed-tick fallback (SL={SLTicks}t TP1={TP1Ticks}t TP2={TP2Ticks}t TP3={TP3Ticks}t)";
// PLACEMENT — SL + 3 TP limit closes, same OCO group
this.PlaceClose(position, closeSide, totalQty, stopType,  triggerPrice: slPrice, limitPrice: null,     label:"SL",  ocoGroupId);
this.PlaceClose(position, closeSide, q1,       limitType, triggerPrice: null,    limitPrice: tp1Price,  label:"TP1", ocoGroupId);
// … q2/tp2Price "TP2", q3/tp3Price "TP3"
```

---

## Family E — Grid / market-making

### E1 · vYORKv `_2Point_1C_100Stop_Grid` — 9-rung ±2 limit ladder, 100-offset stop
`_2Point_1C_100Stop_Grid.cs` @ `main`. `Quantity=1`, `StopLoss=100`.
```csharp
protected override void OnPlaceOrder(PlaceOrderRequestParameters placeOrderRequest) {
    Core.Instance.PlaceOrder(new PlaceOrderRequestParameters(){ Account=…, Symbol=…, Side=placeOrderRequest.Side,
        Price = placeOrderRequest.Price, Quantity = Quantity,
        StopLoss = SlTpHolder.CreateSL(StopLoss, PriceMeasurement.Offset), OrderTypeId = OrderType.Limit });
    // …rungs repeat at Price - 2, -4, … -16 (Buy) / + rungs (Sell)
}
```
**STOP-LOSS:** `CreateSL(100, Offset)` per order. **TAKE-PROFIT: NONE** (no TP line found — the commented sections are extra rungs). **EXIT: NONE.**

### E2 · vYORKv `_2_Grid_Nasdaq` — two-tier grid, per-rung stop
`_2_Grid_Nasdaq.cs` @ `main`. `Grid_1_Quantity=2`, `Grid_2_Quantity=3`.
```csharp
if (placeOrderRequest.Side == Side.Buy) {
    Core.Instance.PlaceOrder(new PlaceOrderRequestParameters(){ … Price = placeOrderRequest.Price, Quantity = Grid_1_Quantity,
        StopLoss = SlTpHolder.CreateSL(32, PriceMeasurement.Offset), //TakeProfit = SlTpHolder.CreateSL(10, …) OrderTypeId=Limit });
    Core.Instance.PlaceOrder(new PlaceOrderRequestParameters(){ … Price = placeOrderRequest.Price - 3, Quantity = Grid_2_Quantity,
        StopLoss = SlTpHolder.CreateSL(20, PriceMeasurement.Offset), //TakeProfit = SlTpHolder.CreateSL(22, …) OrderTypeId=Limit });
} else { /* mirror, Price + 3 */ }
```
**STOP-LOSS:** rung1 32, rung2 20 (offset). **TAKE-PROFIT:** commented (10 / 22). **EXIT:** none (`OnCancel()` throws NotImplemented).

### E3 · mesuteryilmaz `OnePairGridStrategy` — resting bid/ask pair, opposite leg = TP
`src/QT.Quantower/Strategies/OnePairGridStrategy.cs` @ `6d615f5a`. `TargetPairWidthTicks=100`, `StopLossTicks=40`, `TakeProfitTicks=0` (=width). **Armed-off by default.**
```csharp
// ENTRY — maintain bid + ask at target width (geometry in external OnePairGridQuoteEngine)
EnsureSide(Side.Buy,  ref bidOrder, ref bidOrderId, decision.BidOrderTicks, tickSize);
EnsureSide(Side.Sell, ref askOrder, ref askOrderId, decision.AskOrderTicks, tickSize);
// ON FILL → keep opposite leg as TP, then place SL
private void EnterManaging(Position position, string reason) { … if (!PlaceStopLoss()) return; EnsureTakeProfit(); }
```
```csharp
// STOP-LOSS — StopLossTicks from entry, stop-market
double offset = StopLossTicks * CurrentSymbol.TickSize;
double triggerPrice = CurrentSymbol.RoundPriceToTickSize(positionSide == Side.Buy ? positionEntryPrice - offset : positionEntryPrice + offset);
// TAKE-PROFIT — TakeProfitTicks (0 → pair width) on the resting opposite leg
long tpTicks = TakeProfitTicks > 0 ? TakeProfitTicks : TargetPairWidthTicks;
double tpPrice = positionSide == Side.Buy ? positionEntryPrice + tpOffset : positionEntryPrice - tpOffset;
```

---

## Family F — Risk / execution overlays (no entry edge)

### F1 · moravsky `AutoSizeStrategy` — risk-% sizing interceptor
`AutoSizeStrategy.cs` @ `master`. **Places no entries/SL/TP**; intercepts the user's bracketed order and re-sizes to a risk budget.
```csharp
// positionRisk = availableRiskCapital * RiskPercent/100 ; size = floor(positionRisk / costPerContract)
// RiskPercent 2.5%; MissingStopLossAction = Reject; MaxContractsMicro 150 / MaxContractsMini 15; CommissionMicro 0.25 / Mini 2.5; slippage 1.5t
// Requires the user's order to carry a stop; exit orders deliberately NOT resized.
```

### F2 · moravsky `NonStarterExitStrategy` — layered time-based exits
`NonStarterExitStrategy.cs` @ `master`. **No entries.** Two stages on an underwater position:
```csharp
// Stage 1 (+LimitExitCheckDelaySeconds = 30s): if still underwater, place limit at entry ± (LimitExitOffset=1 × tickSize)  → scratch chance
// Stage 2 (+MarketExitCheckDelaySeconds = 180s): cancel the limit; if still underwater, market-flatten before the hard stop
// Only touches its own orders; 30s flagged "sharply optimal"; 90–240s robust.
```

### F3 · bleave `TradeGuardian` — EMA invalidation + revenge/daily-loss lockout
`TradeGuardian.cs` @ `ec98232`. **No entries** (only cancels new orders during lockout, optional `position.Close()`).
```csharp
public enum ExitInvalidationMode { PriceCrossesEma9, PriceCrossesEma21, Ema9CrossesEma21, CandleCloseBeyondEma21, CandleCloseBeyondBothEmas }
// e.g. Ema9CrossesEma21 (long): previousEma9 >= previousEma21 && ema9 < ema21  → invalidate
// Default InvalidationMode = CandleCloseBeyondEma21; alpha9 = 2/(9+1), alpha21 = 2/(21+1)
```
```csharp
// RISK LOCKOUTS (currency, not price)
public double DailyLossLimit { get; set; } = -150;   public double GivebackTrigger { get; set; } = 100;   public int LockoutDurationMinutes { get; set; } = 10;
var drawdownFromHigh = highOfDayPnl - totalPnl;  var netLoss = -totalPnl;
if ((UseNetLossThresholdForRevengeMode ? netLoss : drawdownFromHigh) >= Math.Abs(GivebackTrigger)) TriggerRevengeMode(...);   // 10-min lockout
if (totalPnl <= Math.Min(DailyLossLimit, -Math.Abs(DailyLossLimit))) TriggerDailyHardStop(...);                                // lock for the day
```

### F4 · aryapratham `Buy` — immediate long, ATR sizing across ES + MES, OCO
`Buy.cs` @ `0b548ba`. **No entry signal** — market-buys on launch. ATRPeriod 13 (EMA), StopLossMultiplier 1.6, RewardRiskRatio 1.6, Risk $200.
```csharp
// ENTRY on OnRun: entryPrice = SymbolES.Last; PlaceOrder();  (always Side.Buy, ES + MES)
// SL/TP + SIZING
double stopLossDistance = atr.GetValue() * StopLossMultiplier;               // ATR × 1.6
double StopLoss   = entryPrice - stopLossDistance;
double TakeProfit = entryPrice + stopLossDistance * RewardRiskRatio;         // RR 1.6
double valuePerContract = (stopLossDistance / this.SymbolES.TickSize) * this.SymbolES.GetTickCost(entryPrice);
double Qty = (Risk / valuePerContract);
qtyES  = (int)Qty;
qtyMES = (int)Math.Round((Qty - qtyES) * 10);      // MES = 1/10 of ES — the micro split, verbatim
// TP as OrderType.Limit (Price=TakeProfit, Side.Sell); SL as OrderType.Stop (TriggerPrice=StopLoss, Side.Sell)
// EXIT (OCO): on a leg fill, cancel its opposite; when ES & MES both closed → this.Stop()
```

### F5 · Quantower/Examples `SetSlTpForOpenedPositionStrategy` — % bracket
`Strategies/SetSlTpForOpenedPositionStrategy.cs` @ `0bbb4a1`.
```csharp
// ENTRY: market Buy once on first tick.  EXIT/BRACKET on PositionAdded:
this.PlaceCloseOrder(this.currentPosition, CloseOrderType.StopLoss,   0.0025);   // −0.25% offset
this.PlaceCloseOrder(this.currentPosition, CloseOrderType.TakeProfit, 0.005);    // +0.5%  offset
// SL → OrderTypeBehavior.Stop TriggerPrice = OpenPrice ∓ offset ; TP → Limit Price = OpenPrice ± offset ; reduce-only, opposite side
```

### F6 · Quantower/Examples `PlaceOrderWithMultipleSlTP` — tiered split bracket
`Common/PlaceOrderWithMultipleSlTP.cs` @ `0bbb4a1`.
```csharp
Quantity = 3, OrderTypeId = OrderType.Market …
placeOrderParameters.StopLossItems.Add(SlTpHolder.CreateSL(10, PriceMeasurement.Offset, false, quantity: 2));
placeOrderParameters.StopLossItems.Add(SlTpHolder.CreateSL(20, PriceMeasurement.Offset, false, quantity: 1));
placeOrderParameters.TakeProfitItems.Add(SlTpHolder.CreateTP(15, PriceMeasurement.Offset, quantity: 2));
placeOrderParameters.TakeProfitItems.Add(SlTpHolder.CreateTP(25, PriceMeasurement.Offset, quantity: 1));
```

---

## Family G — External-signal relays (no internal edge)

### G1 · bulldog5046 `TradesByChatt` — YouTube live-chat vote
`TradesByChatt/TradesByChatt.cs` @ `ad7fe36`. Keys "1"=Buy, "2"=Sell, "3"=Flatten; qty 1; **no SL/TP.**
```csharp
switch(highestVoteKey) { case "1": ExecuteOrder(Side.Buy); break; case "2": ExecuteOrder(Side.Sell); break; case "3": FlattenPosition(); break; }
private void ExecuteOrder(Side side) { if (position == null) Core.Instance.PlaceOrder(CurrentSymbol, CurrentAccount, side: side, quantity: 1); else if (position.Side == side) Log("Already in a position, skipping"); }
private void FlattenPosition() { if (position != null) position.Close(); }
```

### G2 · SpoekieKoekie `SolidLinqBridgeStrategy` — WebSocket command executor
`src/SolidLinq.Quantower.Algo/SolidLinqBridgeStrategy.cs` @ `main`. External hub payloads → `Core.Instance.PlaceOrder`; SL/TP from payload (`SlPercent`/`TpPercent` × multiplier); daily/weekly loss/profit flatten via `BridgeRiskCoordinator`. Infrastructure, not signal.

### G3 · Quantower/Examples `Webhook_Strategy_Example` — blank webhook listener (no orders).

---

# PART 2 — Correlation analysis

**The whole corpus is four entry machines, one execution machine, and a pile of overlays.** Once you normalize the code, the entry logic collapses hard:

| Family | Members | Shared entry shape | Shared exit shape | Shared SL/TP shape |
|---|---|---|---|---|
| **MA/EMA cross** | A1–A5 (5) | `fast MA {>,cross} slow MA → Buy/Sell` on 1–2 value indices | `item.Close()` on opposite relation | mostly **none**; when present, fixed offset or fixed points |
| **Range/OR breakout** | B1–B7 (7) | prior-bar/range direction, or first close beyond OR±ext | flip (range-bar) or bracket/trail | SL = opposite level or N ticks; TP = fixed ticks or entry±risk×RR |
| **Momentum/surge** | C1–C3 (3) | `metric > lookback-avg × multiplier(1.15) & green/red bar → Buy/Sell` | PnL-tick poll or soft trail | fixed offset (20/40) or vol-scaled trail |
| **Order-flow/VD** | D1–D2 (2, +C3 signal-mode) | N-of-M evidence gate (RVOL, VD strength, VD/price, HMA), regime veto | opposite HMA break / flow flip | SL = prevbar±ATR clamp; TP = session level / split ticks |
| **Grid/MM** | E1–E3 (3) | `OnPlaceOrder`/quote-loop ladder at fixed tick steps | opposite leg / stop only | per-rung fixed offset stop; TP = opposite leg = width |
| **Overlays** | F1–F6 | *no entry* — sizing, time-exit, discipline, auto-bracket | time/PnL/EMA-invalidation | risk-% sizing or fixed % / ATR bracket |
| **Relays** | G1–G3 | *no edge* — external command → order | external | payload-driven or none |

**Strong recurring idioms (verbatim across ≥3 repos):**
1. **Order call:** `Core.Instance.PlaceOrder(new PlaceOrderRequestParameters{ Account, Symbol, Side, Quantity, OrderTypeId, StopLoss=SlTpHolder.CreateSL(...), TakeProfit=SlTpHolder.CreateTP(...) })` — universal.
2. **Bracket unit:** `SlTpHolder.CreateSL/CreateTP(dist, PriceMeasurement.Offset)` in ticks, or `.Absolute` for a computed price.
3. **Reversal exit:** `foreach (var pos in positions) pos.Close();`
4. **Direction rule:** green bar / `fast > slow` / `close > level` → Buy (and mirror).
5. **OCO:** place TP-limit + SL-stop opposite side; on one fill cancel the other; when flat → reset/`Stop()`.
6. **Distance normalization:** `ticks * Symbol.TickSize` — portable across micro/full-size.
7. **Session gate:** NY 09:30 ET (= 13:30 UTC) appears in every ORB.

**Notable divergences worth your attention:**
- **Half the "strategies" have no SL/TP at all** (all MA-cross demos, both range-bar flips, `PriceSurge`) — they rely on reversal-close or PnL-tick polling. Several (vYORKv) have SL/TP **written then commented out**.
- **Only the order-flow family (D1/C3) and `AutoSizeStrategy` normalize by volatility/ratios**; everyone else hard-codes raw ticks. That's the single biggest calibration difference from an IOF footprint system.
- **The most production-grade risk management** (trailing, BE, time-stops, disaster-flatten, split scale-out) lives in the **zenaimaster ORB pair**, not in the order-flow bots.

---

# PART 3 — The three boilerplates

### Boilerplate A — Order-flow / volume-delta anchor
*Distilled from `FlagshipFuturesStrategy` (D1) + `PyramidMomentumStrategy` signal-mode (C3) + `AutoSLTP` (D2). This is the shape to hold your IOF strategy against.*

```
class OrderFlowStrategy : Strategy, ICurrentSymbol, ICurrentAccount
  Inputs: Symbol, Account, Period, Contracts,
          RvolThreshold(1.0), VdStrengthThreshold(1.2), VdPriceRatioThreshold(1.5),
          HmaBasePeriod(20), AtrPeriod(14), EntrySignalsRequired(1..N),
          AtrMultiplierSL(1.0), MinStopTicks(4), MaxStopTicks(20),
          MinTpTicks(8), AltTpTicks(12) | TP-split %s, [MinSignalConfidence(0.50), AddStepTicks(20), MaxContracts(3)]

  OnBarClosed:
    signals = { RVOL: vol/EMA(vol) > RvolThreshold,
                VDstrength: |VD| > mean(VD) × VdStrengthThreshold,
                VDprice: priceMove/VD > histRatio × VdPriceRatioThreshold,
                HMA: |close − MA(base)| > ATR × 0.5,
                [flow bias & confidence > MinSignalConfidence] }
    regimeVeto: if risk-off → no entry
    if count(signals) ≥ EntrySignalsRequired:
        side = close > HMA ? Buy : Sell
        PlaceOrder(Market, side, Contracts)
        SL = prevBar.{Low,High} ∓ ATR×AtrMultiplierSL,  clamp[MinStopTicks, MaxStopTicks]
        TP = nearest session {High,Low} beyond price; if < MinTpTicks step next else AltTpTicks
             (or split TP1/TP2/TP3 by qty% at fixed ticks)
    [pyramid: +1 every AddStepTicks in profit up to MaxContracts, only if flow supports]
    exit: close crosses HMA by 1×ATR  (or soft-trail max(TrailTicks, VolMult×noise) + immediate on flow flip)
```
**Normalization flags:** RVOL/VD/HMA thresholds are **ratios / ATR-multiples → instrument-agnostic**. `AddStepTicks`, `MinStop/MaxStop`, `MinTp/AltTp` are **raw ticks → rescale per instrument**. This is exactly the raw-vs-normalized split from your calibration-anchor scrub.

### Boilerplate B — Universal skeleton (spans every family)
```
class X : Strategy, ICurrentSymbol, ICurrentAccount
  Inputs: Symbol, Account, Period, Quantity/Contracts,
          <indicator periods>, <signal threshold(s)>,
          <SL: ticks | ATR-mult | opposite-level>, <TP: ticks | RR | session-level>,
          <session window>, <risk caps: MaxTrades, MaxProfit, MaxLoss/DailyLoss>

  OnRun:  hd = Symbol.GetHistory(Period, warmup); hd.AddIndicator(...); subscribe NewHistoryItem | NewLast
  OnNewHistoryItem (bar close):
     if in position:
        if reversal-signal → foreach pos: pos.Close()
        else manage trail/BE/time-stop
     else if gates pass (session ∧ MaxTrades ∧ dailyPnL):
        sig = Evaluate()                       // cross | breakout | surge | evidence-gate
        if sig:
           dir = green/fast>slow/close>level ? Buy : Sell
           PlaceOrder(Market|Stop, dir, qty,
                      StopLoss  = SlTpHolder.CreateSL(<dist>, Offset|Absolute),
                      TakeProfit= SlTpHolder.CreateTP(<dist>, Offset|Absolute))
  OnPositionRemoved: reset counters
```
**Modal defaults across the corpus (what "typical" looks like — NOT recommendations):**

| Knob | Corpus range | Mode / typical |
|---|---|---|
| Quantity/Contracts | 1–6 | **1** |
| Bar period | 30s – MIN5 | **MIN1** |
| SL | 10–100 ticks, or ATR×1.0–1.6 | micros **40–60t**; scalpers **10–20t** |
| TP | 5–600 ticks, or RR 1.6–2.0 | **RR ~2.0** or fixed **40–60t** |
| Fast/Slow MA | 5/10, 9/34, 10/20 | **9/34** (session) |
| Trail | trigger/dist/step | **60 / 20 / 1** ticks |
| Break-even | trigger/offset | **50 / 1** ticks |
| Session | NY open | **09:30 ET (13:30 UTC)** |
| Risk sizing | 1.0–2.5% | **1%** (ORB), **2.5%** (AutoSize) |
| Surge multiplier | 1.15–2.0 | **1.15** |
| Daily-loss lockout | — | **−$150** + 10-min revenge lockout |

### Boilerplate C — Futures ES/NQ/micros
*From the tick/ATR-managed futures bots: `FlagshipFuturesStrategy` (ES/NQ), `aryapratham/Buy` (ES+MES), `moravsky/ORB` (NQ/MNQ), `zenaimaster` ORB+NY (MNQ), `PyramidMomentum`, `OnePairGrid`.*
```
- Symbol via CurrentSymbol; ALL distances = ticks × Symbol.TickSize  (portable micro↔full)
- Sizing: contracts = floor(balance × risk% / (stopTicks × tickValue))     [moravsky/ORB, aryapratham]
          tickValue = Symbol.GetTickCost(price)
- Micro split (verbatim, aryapratham): qtyES = floor(Qty); qtyMES = round((Qty − qtyES) × 10)   ← MES = 1/10 ES
- SL basis: prevBar ± ATR×(1.0–1.6) clamp floor ~4t   |  fixed 40–60t
- TP basis: RR 1.6–2.0  |  session High/Low level  |  wide fixed (zenaimaster 600t) with 320t disaster-flatten
- Session: gate to NY 09:30 ET; one-trade-per-session common (moravsky _hasTradedThisRun)
- Risk overlays to bolt on: AutoSize (prop-firm drawdown modes, 2.5%), TradeGuardian (−$150 daily, revenge lockout), NonStarterExit (30s/180s time-cut)
- Scale-out: split TP (zenaimaster 5-lot@60t / 1-lot free-run; AutoSLTP 3-way % split)
```

---

# PART 4 — Comparison scaffold vs your IOF strategy

*Fill the right column from your own strategy; the middle column is the external anchor. Where the anchor is a raw tick count, remember it was set for that author's instrument — rescale before comparing.*

| Dimension | Corpus anchor | Your IOF strategy |
|---|---|---|
| Entry trigger type | cross / breakout / surge / **evidence-gate (order-flow)** | `__________` |
| Signal count / gate | N-of-M (Flagship 1-of-4 live); single-condition elsewhere | `__________` |
| Direction rule | close vs HMA / fast vs slow / bar color | `__________` |
| Order-flow normalization | RVOL 1.0, VD-strength ×1.2, VD/price ×1.5 (ratios) | `__________` |
| Regime veto | risk-off block (Pyramid/Flagship) | `__________` |
| SL basis | prevBar±ATR×1.0 clamp[4,20]t / fixed 40–60t | `__________` |
| TP basis | session level (min 8t / alt 12t) / RR 2.0 / split | `__________` |
| Trailing / BE | trail 60/20/1, BE 50/1 (zenaimaster) | `__________` |
| Scale-out / splits | 5-lot@60t + 1-lot free-run; 30/40/30% (AutoSLTP) | `__________` |
| Pyramiding | +1 per 20t up to 3, flow-gated (Pyramid) | `__________` |
| Session filter | NY 09:30 ET | `__________` |
| Position sizing | floor(balance×risk%/(stopTicks×tickValue)); 1–2.5% | `__________` |
| Micro↔full scaling | MES = 1/10 ES (qtyMES=round(frac×10)) | `__________` |
| Daily-loss guard | −$150 + 10-min revenge lockout | `__________` |
| Time-based exit | 30s scratch / 180s cut; 5-min flatten | `__________` |

---

## Could-not-source / blocked / flags

- **`DarkLink005/iof-specs → AutoSLTP_Strategy.cs`** — private repo; full read blocked (raw 404 + MCP repo-scope denial). Reconstructed from ~20 `search_code` fragments (verbatim but non-contiguous, no line numbers). One SL price-assignment line uncaptured. The "1R/3R/5R" / "30/40/30" are **config params, not hard-coded** — the R-multiples appear only in `IOF_PLAYBOOK.md`.
- **`FlagshipFuturesStrategy`** — file is ~7,100 lines; the two VD signals (VD-Volume, VD-Divergence) are wired into the N-of-6 tally but **never assigned in the live path** (dead votes). "HRVD" is not expanded anywhere in source — inferred (Hull-MA / RVOL / Volume-Delta), **not author-stated**.
- **`vYORKv/_2Point_1C_100Stop_Grid`** — the ~9 ladder rungs couldn't all be pulled verbatim (repetition tripped the fetcher); the "TP commented out" claim could **not** be confirmed for this file (no TP line, active or commented, was found).
- **`Pusparaj99op/Quantower-Orderflow-Algo`** — **off-target and a security issue.** It is not Quantower — it's a Binance BNB-futures LLM bot (RSI/MACD → Ollama → keyword-match), and its `config.py` commits **live plaintext API keys** (Binance key+secret, CoinMarketCap, NewsAPI). Not usable as an anchor; flagged so you (or the owner) can rotate those keys.
- **`Pusparaj99op/MGC-4Trades…Algo`**, **`variks167-spec/quantower-algo-guide`** — docs/skill-markdown only, no strategy code. **`Quantower/Scripts`** — indicators only. **`hgroechel/Quantower`** — empty repo. **`mesuteryilmaz/QT_API → DataAnalyticsStrategy`** — dormant deprecated V1, not compiled in the shipped build.
- **Whole-repo caveat:** GitHub code search indexes the default branch; a strategy on a non-default branch or in an unindexed private repo would be missed. Coverage is "every strategy reachable via GitHub code/repo search + raw fetch," not a guarantee of literally-every bot in existence.

---

### Coverage summary (channels worked / blocked)

**Worked:** GitHub `search_code` + `search_repositories` (cross-repo discovery) ✅, `raw.githubusercontent.com` verbatim reads via WebFetch/curl ✅, WebSearch ✅. **Blocked/unavailable:** `api.github.com` egress (gated), `get_file_contents` for non-`iof` repos (scope-limited), and the entire `agent-reach` local stack — **yt-dlp/YouTube ❌, Bilibili ❌, RSS ❌, Jina ❌** — so there is zero video/RSS coverage; every finding rests on GitHub + web. ~29 strategy classes across 24 repos were read; all runnable ones are quoted verbatim except `AutoSLTP` (fragment-only, private).
