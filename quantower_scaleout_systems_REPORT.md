# Quantower Scale-Out / Multi-TP Systems — Focused Findings

**Prepared:** 2026-07-06
**Your ask:** make sure your bot sees strategy bots **successfully running multiple take-profits with a scale-out system** (partial exits at TP1/TP2/TP3, move-to-breakeven, leave a runner).

> **Companion file:** `quantower_strategy_microscope_REPORT.md` has the full ~29-bot corpus. This file filters that corpus to **only the strategies that actually scale out**, plus a targeted GitHub sweep for the multi-TP API (`TakeProfitItems.Add` / `SlTpHolder.CreateTP(quantity:)`) beyond the repos already scrubbed.

---

## Headline finding

**Multi-TP scale-out is rare in the open-source Quantower population.** Most bots use a single TP, no TP (reversal-close), or PnL-tick full-close. After sweeping every repo that touches the multi-TP API, only **three** implement a real scale-out, and only **one** of those is a mature, tested, runnable strategy:

| Rank | Strategy | Scale-out mechanism | "Running" maturity signals | Verdict |
|---|---|---|---|---|
| ⭐ **1** | zenaimaster-lab `KatNewYorkOpening` | **2-tranche**: Set A (5 lots) banks at TP 60t; Set B (1 lot) free-runs on trailing SL + BE | **v1.0.47**, has `KatNewYorkOpening.Tests` project, `build-deploy.ps1`, adapts to `Symbol.TickSize` (MNQ) | **Best working example** |
| **2** | DarkLink005 `AutoSLTP_Strategy` (your `iof-specs`) | **3-tranche**: TP1/TP2/TP3 by configurable qty% + monotonic SL, OCO group; zone-intent → fixed-tick fallback | Part of IOF v2/v3.5 toolchain (paired `TradePhantoms_IOF_v2.cs`, `IOF_PLAYBOOK.md`); private repo | **Your own design** |
| **3** | Quantower/Examples `PlaceOrderWithMultipleSlTP` | **2-tier**: SL 10/20 (qty 2/1) + TP 15/25 (qty 2/1) via `StopLossItems`/`TakeProfitItems` | Official Quantower sample (115★ repo), but **fires once** on activation | **Canonical API reference** |
| — | NeoNix-Lab `OrderManager` `TpSlPositionManager` | Loops `TakeProfitItems.Add` over a TP list | `UpdateTp()` throws `NotImplementedException`; no qty split | **Scaffolding, not working** |
| — | alihamza1221 `ImbalanceCluster-Stragtegy` | *(order-flow entry, relevant)* — but exits **whole** position at ±`SLTPTicks` | martingale sizing; single close | **Not a scale-out** |

> **Honesty flag on "successfully running":** you cannot prove live profitability from source. "Running" here is inferred from **maturity signals only** — version tags, test projects, deploy scripts, releases, tick-size adaptivity. Treat #1 as *demonstrably built to run live*, not *demonstrably profitable*.

---

## The three real scale-out systems — verbatim

### ⭐ 1. zenaimaster-lab `KatNewYorkOpening` — 2-tranche bank-and-run
`KatNewYorkOpening.cs` @ `main`. **This is the pattern to model.** Total 6 lots split 5 + 1: the 5-lot "Set A" takes profit at a fixed target; the 1-lot "Set B" is left to run on a trailing stop after breakeven.

```csharp
// PARAMETERS — the split is explicit
public bool   InpUseSplitMode      = true;
public double InpTotalTradeContract = 6.0;   // total
public double InpSetAContracts      = 5.0;   // Set A banks;  Set B = Total - A = 1 lot runs
public int    InpStopLossTicks      = 60;    // shared initial SL
public int    InpSetATpTicks        = 60;    // Set A fixed TP
public int    InpSetBTpTicks        = 0;     // 0 => Set B has NO fixed TP → free-run on trail
public int    InpSetBTrailTrigger   = 60;    // runner trail arms at +60t
public int    InpSetBTrailDistance  = 20;    // runner trails by 20t
public int    InpSetBTrailStep      = 1;
public int    InpSetBBeOffset       = 1;     // breakeven offset
```
```csharp
// TAKE-PROFIT — Set A: place a limit for MIN(SetA, position) lots at +60t
if (this.InpSetATpTicks > 0) {
    double tpQty = 0.0;
    if (!this.InpUseSplitMode) tpQty = pos.Quantity;
    else if (pos.Quantity > (this.InpTotalTradeContract - this.InpSetAContracts))   // more than Set B remains
        tpQty = Math.Min(this.InpSetAContracts, pos.Quantity);                        // → bank up to 5 lots
    if (tpQty > 0) {
        double tpAPrice = pos.Side == Side.Buy ? (pos.OpenPrice + this.InpSetATpTicks * tickSize)
                                               : (pos.OpenPrice - this.InpSetATpTicks * tickSize);
        var tpARequest = new PlaceOrderRequestParameters {
            Account = this.Account!, Symbol = pos.Symbol,
            Side = pos.Side == Side.Buy ? Side.Sell : Side.Buy,
            OrderTypeId = OrderType.Limit, Quantity = tpQty, Price = tpAPrice,
            Comment = $"TakeProfit1_{StrategyMagicNumber}", TimeInForce = TimeInForce.GTC };
        /* … PlaceOrder … */ } }
```
```csharp
// Set B — only placed if InpSetBTpTicks > 0; with default 0 this block is SKIPPED → the runner free-runs
if (this.InpUseSplitMode && this.InpSetBTpTicks > 0) {
    double tpAQuantity = (FindActiveTakeProfit1Order(pos) != null || this.isPlacingTP1) ? Math.Min(this.InpSetAContracts, pos.Quantity) : 0;
    double tpBQuantity = pos.Quantity - tpAQuantity;
    /* … place TP2 limit for tpBQuantity … */ }
```
```csharp
// BREAKEVEN — arms when Set A TP fills (qty drops to Set B) OR profit ≥ Set A TP; SL → open ± offset
bool isTpAFilled = currentQuantity <= remainingSetBLots;
isBeTriggered = isTpAFilled || (profitTicks >= setATpTicks);
if (isBeTriggered) {
    double bePrice = posSide.Equals("Buy") ? (openPrice + beOffsetTicks * tickSize)
                                           : (openPrice - beOffsetTicks * tickSize);
    /* … move SL to bePrice … */ }
```
```csharp
// RUNNER TRAIL — after +60t, trail the survivor by 20t, step 1t (TrailingStopCalculator, BUY branch)
if (!result.NewTrailingTriggered && (currentBid - openPrice >= triggerDist)) result.NewTrailingTriggered = true;
if (result.NewTrailingTriggered) {
    double newSL = PriceRoundingHelper.RoundPrice(currentBid - trailDist, tickSize);
    if (newSL > referenceSl || referenceSl == 0)
        if (referenceSl == 0 || (newSL - referenceSl) >= stepDist) { result.ShouldModify = true; result.NewSlPrice = newSL; } }
```
**Scale-out shape:** bank 5/6 at a fixed target → move stop to breakeven → trail the last 1/6 for the tail. SL is resized on each partial fill so it always covers the remaining quantity. Time-stops (5-min flatten) cap the runner.

### 2. DarkLink005 `AutoSLTP_Strategy` — 3-tranche % split (your IOF-specs)
`indicators/AutoSLTP_Strategy.cs` @ `2256383` — **private repo, fragment-only** (reconstructed from search; no line numbers). On a manual fill it brackets the position with one SL and **three** TP limit orders, each a configurable **percentage** of the position, at configurable tick distances (tries an IOF zone-derived plan first, falls back to fixed ticks). This is already a 3-way scale-out design — it's your own.

```csharp
[InputParameter("Stop-Loss distance (ticks)", …)] public int    SLTicks    { get; set; }
[InputParameter("TP1 distance (ticks)", …)]       public int    TP1Ticks   { get; set; }   // + TP2Ticks, TP3Ticks
[InputParameter("TP1 quantity %", …0.0,1.0…)]     public double TP1QtyPct  { get; set; }   // + TP2QtyPct, TP3QtyPct
```
```csharp
// QTY SPLIT — three tranches; residual forced onto TP3 so the whole position is covered
double q1 = this.RoundQty(totalQty * this.TP1QtyPct);
double q2 = this.RoundQty(totalQty * this.TP2QtyPct);
double q3 = this.RoundQty(totalQty - q1 - q2);
if (q3 <= 0) { q3 = Math.Max(this.CurrentSymbol.LotSize > 0 ? this.CurrentSymbol.LotSize : 1, totalQty - q1 - q2);
               if (q1 + q2 + q3 > totalQty) q1 = Math.Max(0, totalQty - q2 - q3); }
```
```csharp
// FIXED-TICK PRICES (fallback) + three OCO limit closes
tp1Price = isLong ? entry + this.TP1Ticks * tickSize : entry - this.TP1Ticks * tickSize;   // + tp2Price, tp3Price
this.PlaceClose(position, closeSide, totalQty, stopType,  triggerPrice: slPrice, limitPrice: null,    label:"SL",  ocoGroupId);
if (q1 > 0) this.PlaceClose(position, closeSide, q1, limitType, triggerPrice: null, limitPrice: tp1Price, label:"TP1", ocoGroupId);
if (q2 > 0) this.PlaceClose(position, closeSide, q2, limitType, triggerPrice: null, limitPrice: tp2Price, label:"TP2", ocoGroupId);
if (q3 > 0) this.PlaceClose(position, closeSide, q3, limitType, triggerPrice: null, limitPrice: tp3Price, label:"TP3", ocoGroupId);
```
**vs zenaimaster:** AutoSLTP splits into **3 tranches by percentage** (e.g. 30/40/30) vs zenaimaster's **2 tranches by lot count** (5/1). AutoSLTP's doctrine is monotonic SL (never widen). It reacts to *manual* fills — no entry signal — so it's the exit/scale-out layer, which is exactly the piece you're comparing.

### 3. Quantower/Examples `PlaceOrderWithMultipleSlTP` — the official 2-tier API demo
`Common/PlaceOrderWithMultipleSlTP.cs` @ `0bbb4a1`. The canonical way to attach tiered SL/TP in one order — this is the exact API your bot should emit.
```csharp
PlaceOrderRequestParameters placeOrderParameters = new PlaceOrderRequestParameters() {
    Symbol = this.CurrentSymbol, Account = this.CurrentAccount,
    Quantity = 3, OrderTypeId = OrderType.Market, TimeInForce = TimeInForce.GTC };

placeOrderParameters.StopLossItems.Add(SlTpHolder.CreateSL(10, PriceMeasurement.Offset, false, quantity: 2));
placeOrderParameters.StopLossItems.Add(SlTpHolder.CreateSL(20, PriceMeasurement.Offset, false, quantity: 1));
placeOrderParameters.TakeProfitItems.Add(SlTpHolder.CreateTP(15, PriceMeasurement.Offset, quantity: 2));
placeOrderParameters.TakeProfitItems.Add(SlTpHolder.CreateTP(25, PriceMeasurement.Offset, quantity: 1));

Core.Instance.PlaceOrder(placeOrderParameters);
```
**Two ways to build a scale-out, seen across these three:** (a) **attached tiers** on the entry order via `StopLossItems`/`TakeProfitItems` with per-item `quantity:` (this example) — the broker manages the OCO; or (b) **separate child orders** placed post-fill, one per tranche, in a shared OCO group (KatNewYorkOpening, AutoSLTP), which gives you programmatic control to move SL to BE and trail the runner. **For a footprint/IOF system that wants BE-after-TP1 and a trailed runner, pattern (b) is the one to model** — (a) can't move the stop to breakeven on a partial fill.

---

## Order-flow bots that do NOT scale out (so your bot doesn't over-credit them)
Relevant because they're order-flow like IOF, but they close the **whole** position — no tranches:
- **alihamza1221 `ImbalanceCluster-Stragtegy`** — 3:1 imbalance + stacked-imbalance entries (`ImbalanceRatio=3`, `WindowSize=3`), but `CheckCloseOnProfit` closes the full position at ±`SLTPTicks` (default 4t) and uses martingale lot sizing (`Multiplier=2`, `MaxLossTrades=4`). Good order-flow entry reference; **not** a scale-out.
- **sfrdragon `FlagshipFuturesStrategy`** — single TP (nearest session level, min 8t / alt 12t); no tranches.
- **mesuteryilmaz `PyramidMomentumStrategy`** — scales **in** (pyramids), exits on one soft trail; opposite of scale-out.

---

## Scale-out boilerplate (model your bot against this)

```
On entry fill (side, entryPrice, totalQty, tickSize):
  # ---- STOP ----
  SL = entryPrice ∓ SLTicks*tickSize            # single stop covering full remaining qty
  place child STOP (opposite side, totalQty) in ocoGroup

  # ---- MULTI-TP SCALE-OUT (pattern b: separate OCO children) ----
  q1 = round(totalQty * TP1Pct); q2 = round(totalQty * TP2Pct); q3 = totalQty - q1 - q2   # 3-tranche %
      # (or lot-count tranches: qA = min(SetA, totalQty); qB = totalQty - qA)
  place child LIMIT q1 @ entry ± TP1Ticks*tickSize   ("TP1")
  place child LIMIT q2 @ entry ± TP2Ticks*tickSize   ("TP2")
  # leave q3 (the runner) with NO fixed TP  → trails

On TP1 fill (partial):
  move SL → breakeven ± BEOffset*tickSize       # bank secured, risk removed
  resize SL to remaining qty

On runner in profit ≥ TrailTrigger*tickSize:
  trail SL by TrailDistance*tickSize, stepping TrailStep, favorable-only, ≥ step gate

Guards: resize SL on every partial fill; cap the runner with a time-stop; OCO so any leg fill
        cancels/repositions the siblings.
```

**Corpus defaults for these knobs (anchors, not recommendations):**

| Knob | zenaimaster (2-tranche) | AutoSLTP (3-tranche) | Examples (2-tier) |
|---|---|---|---|
| Tranches | 5 lots + 1 runner | %-split (playbook 30/40/30) | qty 2 + 1 |
| TP distances | Set A 60t; runner none | TP1/TP2/TP3 ticks (config) | 15t / 25t |
| SL | 60t → BE on TP1 fill | `SLTicks`, monotonic (never widen) | 10t / 20t tiered |
| Runner mgmt | trail 60/20/1, BE offset 1t | (trail lives elsewhere in toolchain) | none (fires once) |
| Build style | separate OCO children | separate OCO children | attached tier items |

**What to make your bot check against each candidate:** (1) does it split qty across ≥2 TP orders? (2) does it move SL to breakeven after TP1? (3) does it leave a runner with a trailing stop rather than a fixed TP? (4) does it resize the stop on partial fills? Only **KatNewYorkOpening** (and your own **AutoSLTP** by design) answer yes to all four; the official example answers (1) only.

---

### Coverage summary (channels worked / blocked)
**Worked:** GitHub `search_code` targeted on the multi-TP API (`TakeProfitItems.Add`, `SlTpHolder.CreateTP(quantity:)`, partial-close) ✅, `raw.githubusercontent.com` verbatim reads ✅. **Blocked:** `api.github.com` egress (gated); `DarkLink005/iof-specs` private (AutoSLTP fragment-only); and the whole `agent-reach` stack — **yt-dlp/YouTube ❌, Bilibili ❌, RSS ❌, Jina ❌** — zero video/RSS coverage. Net: of ~29 strategy bots, exactly **3 implement multi-TP scale-out** and only **1** (`KatNewYorkOpening`) is a mature, tested, runnable one; your own `AutoSLTP` is the 3-tranche design.
