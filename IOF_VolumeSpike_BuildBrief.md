# IOF Volume Spike — Build Brief for NinjaTrader
**Ported from:** TradePhantoms `IOF_VolumeSpike.cs` (Quantower / C# SDK)
**Purpose:** Volume profile heatmap + High Volume Node (HVN) detection with IOF zone confluence flagging.

---

## What It Does (Plain English)

1. Builds a volume profile for the current session (price → volume at that price)
2. Scans every price level and flags it as an **HVN (High Volume Node)** if its volume is significantly above the average of the N levels surrounding it
3. Classifies each HVN by shape: **PEAK, BUILDING, BACKSIDE, or ISOLATED**
4. Draws horizontal lines on the chart at every HVN price — dashed/solid based on context
5. Draws a **color heatmap panel** on the right side of the chart (cool blue = low volume → hot red = HVN)
6. Optionally flags `★IOF` on any HVN that overlaps a live IOF zone (cross-indicator confluence)

---

## User Parameters

| Parameter | Default | Description |
|---|---|---|
| Spike threshold % above avg | 150% | A level must be this % above its neighbors' average to qualify as HVN |
| Surrounding levels (N each side) | 5 | How many levels on each side to use for the average |
| Profile days | 1 | How many days of native volume profile data to pull |
| Fallback lookback bars | 500 | Bars to scan if native VP unavailable |
| Fallback bucket size (ticks) | 4 | Price bucket size for bar-based fallback profile |
| IOF zone proximity (ticks) | 8 | How close to an IOF zone = confluence flag |
| Show HVN labels | true | Show text label at each HVN line |
| HVN line width | 2 | Line thickness |
| Show heatmap | true | Draw right-side volume heatmap panel |
| Heatmap width (px) | 80 | Width of heatmap panel in pixels |
| Heatmap opacity (0–255) | 180 | Transparency of heatmap bars |

---

## Step 1 — Build the Volume Profile

The indicator tries two methods in order, using whichever returns > 10 levels.

### Method A: Native Volume Profile (preferred)
Pull the platform's built-in volume profile for the last N days.
Each price level → total volume traded at that price.

```
// Quantower equivalent:
hd = Symbol.GetHistory(HistoryAggregationVolumeProfile(Period.DAY1), fromDate, now)
for each item in hd:
    for each priceLevel in item.PriceLevels:
        profile.add( (price, volume) )
```

**NinjaTrader equivalent:** Use `VolumetricBars` or `SessionVolumeProfile`. If using `VolumetricBars`, call `GetBarVolumeSeries()` or iterate `Bars.GetClose(i)` etc. with the volumetric data. The cleanest NinjaTrader approach is a `VolumetricSessionIterator` or manually using `Bars.BarsSinceNewTradingDay` to scope to the current session.

### Method B: Bar-based Fallback (always works)
If native VP fails, build a synthetic profile from OHLC bars:

```
bucketSize = BucketTicks × tickSize

for each bar in last LookbackBars:
    volPerPoint = bar.Volume / 4          // split equally across O/H/L/C
    for each price in [Open, High, Low, Close]:
        bucket = round(price / bucketSize)
        buckets[bucket] += volPerPoint

profile = buckets.Select( bucket → (bucket × bucketSize, totalVolume) )
```

**Key insight:** Dividing bar volume by 4 and attributing it to each of Open/High/Low/Close is a simple approximation. NinjaTrader with `VolumetricBars` can do this more accurately using `BidVolume`/`AskVolume` per bar.

---

## Step 2 — Detect HVN Spikes

After sorting the profile by price ascending:

```
for each level[i] in profile (skip first N and last N):

    centerVol = profile[i].Volume
    surroundingSum = sum of volumes for profile[i-N .. i-1] + profile[i+1 .. i+N]
    avgNeighbor = surroundingSum / (N × 2)

    pctAboveAvg = (centerVol / avgNeighbor) × 100

    if pctAboveAvg >= SpikeThresholdPct:
        → This is an HVN. Record it.
```

**Default threshold is 150%** — meaning the level has volume 1.5× the average of its 5 neighbors on each side. Raise it to 200–300% to find only the sharpest spikes.

---

## Step 3 — Classify Each HVN

Look at the shape of the volume distribution around the spike to classify its context:

```
leftRising  = profile[i-N .. i-1] volume is monotonically increasing (volume builds into the level)
rightFalling = profile[i+1 .. i+N] volume is monotonically decreasing (volume falls off after)
```

| leftRising | rightFalling | Classification | Meaning |
|---|---|---|---|
| ✓ | ✓ | **PEAK** | True HVN — clean spike, volume peaked here |
| ✓ | ✗ | **BUILDING** | Volume still building above this level |
| ✗ | ✓ | **BACKSIDE** | HVN is on the declining side of a larger cluster |
| ✗ | ✗ | **ISOLATED** | Standalone spike, no clear trend around it |

### Visual treatment per context:
- **PEAK** → solid line (most significant)
- **BUILDING** → dash-dot line
- **BACKSIDE** → dashed line (least reliable — price often blows through)
- **ISOLATED** → dotted line

---

## Step 4 — IOF Zone Confluence Check

For each HVN, check if it overlaps a live IOF supply/demand zone:

```
proximityTolerance = IOFZoneProximityTicks × tickSize

isConfluence = any active IOF zone where:
    zoneBottom - tolerance ≤ hvnPrice ≤ zoneTop + tolerance
```

In the Quantower version this is done via reflection into the `IOFZoneRegistry` static class from the IOF v2 DLL. **In NinjaTrader**, if you're running a separate IOF zone indicator, you'd need to share zone data via a static registry class in a shared namespace, or use `Indicators[indicatorName]` to reference the other indicator's public properties.

If IOF is not loaded, this check is a no-op — the indicator still works fine on its own.

**Visual treatment:** IOF confluence HVNs draw in **gold** instead of cyan, and get the `★IOF` suffix on the label.

---

## Step 5 — Rendering

### HVN Lines
For each HVN level:
1. Convert price → Y pixel coordinate
2. Draw horizontal line from chart left edge to heatmap left edge
3. Line style = context (solid/dash/dot)
4. Line color = gold (IOF confluence) / orange (Backside) / cyan (standard)
5. Label: `HVN PEAK +87%` or `HVN BACKSIDE +210% ★IOF`

### Heatmap Panel (right side)
The heatmap is a vertical strip on the right edge of the chart. Each price level gets a horizontal bar whose **width** is proportional to its volume vs the max volume level.

```
for each level in profile:
    t = level.Volume / maxVolume          // normalized 0.0 → 1.0
    barWidth = t × HeatmapWidth           // pixel width
    color = HeatColor(t)                  // see color ramp below
    draw filled rect at (right - barWidth, yTop) with (barWidth, rowHeight)
```

**Row height:** Each row spans from the midpoint to the previous level to the midpoint to the next level — so there are no gaps and no overlaps regardless of zoom level.

### Heatmap Color Ramp
Four-segment gradient: **cool blue → cyan → yellow → hot red**

```
t = 0.00 → 0.25:  rgb(0,   0→80,  160→255)   // dark blue → cyan
t = 0.25 → 0.50:  rgb(0,   80→255, 255→0)    // cyan → green/yellow
t = 0.50 → 0.75:  rgb(0→255, 255,  0)        // yellow → orange
t = 0.75 → 1.00:  rgb(255, 255→0,  0)        // orange → red
```

Each segment: `s = (t - segmentStart) / 0.25` gives a 0→1 local interpolation factor.

---

## Rebuild Trigger

The profile and HVN list is **rebuilt on every new bar** (`OnBarUpdate` with `Calculate.OnBarClose` in NinjaTrader). No tick-level recalculation needed — this is a bar-by-bar indicator.

---

## NinjaTrader Implementation Notes

### Volume Profile Source
- Best: use `VolumetricBars` (NinjaTrader's built-in volumetric data type). Gives exact volume at every tick price.
- Fallback: use `VOC` (Volume at Price) or the bar-based bucketing approach above.

### Threading
The Quantower version uses a `lock` object because `OnPaintChart` and `OnUpdate` run on different threads. In NinjaTrader, `OnRender` also runs on the UI thread separate from `OnBarUpdate`, so you'll want a similar lock or use `lock (_lock)` around any shared list reads/writes.

### IOF Zone Lookup
In NinjaTrader, create a static `IofZoneRegistry` class in a shared `.cs` file that both your IOF indicator and the VolumeSpike indicator reference. Or use NinjaTrader's `Indicators["IOF_ZoneName"]` accessor to call a public method on the other indicator.

### Line Drawing (NinjaTrader `OnRender`)
```csharp
// Convert price to Y coordinate
double y = chartControl.GetYByValue(chartPanel, price);
// Draw horizontal line
renderTarget.DrawLine(
    new SharpDX.Vector2(chartPanel.X, (float)y),
    new SharpDX.Vector2(chartPanel.X + chartPanel.W, (float)y),
    brush, lineWidth
);
```

---

## Summary Flow

```
OnBarUpdate (new bar)
    ↓
BuildVolumeProfile()
    ├─ Try: native VP (platform session profile)
    └─ Fallback: bar-based O/H/L/C bucketing
    ↓
For each price level:
    Compare volume vs N-neighbor average
    If > threshold → HVN
    Classify context (Peak / Building / Backside / Isolated)
    Check IOF zone proximity → ★IOF flag
    ↓
Store list of HvnLevels

OnRender / OnPaintChart
    ↓
Draw heatmap panel (right edge, width proportional to volume)
For each HvnLevel:
    Draw horizontal line (style by context, color by IOF flag)
    Draw label (if enabled)
```

---

*Source: TradePhantoms IOF VolumeSpike v1.145.17 — Quantower C# SDK*
*Logic extracted for NinjaTrader 8 port — June 2026*
