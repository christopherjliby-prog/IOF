# Quantower Indicator Development Reference
### Compiled from official Quantower docs — 2026-05-29
### For use by Christopher's Claude when building/modifying IOF indicators

---

## 1. What an indicator is (Quantower model)

An indicator is a set of **line buffers** — arrays where each element maps to a chart bar or tick. You calculate values and push them into the buffers; Quantower handles the drawing. Custom zones, boxes, and text are done separately via `OnPaintChart` / GDI+.

---

## 2. Indicator skeleton

```csharp
using TradingPlatform.BusinessLayer;
using System.Drawing;

public class MyIndicator : Indicator
{
    // Input parameters declared here (see section 4)
    [InputParameter("Period", 0, 1, 500, 1, 0)]
    public int Period = 14;

    public MyIndicator() : base()
    {
        Name = "My Indicator";
        Description = "What it does";
        AddLineSeries("Line1", Color.CadetBlue, 1, LineStyle.Solid);
        SeparateWindow = false;  // true = indicator gets its own pane below chart
    }

    protected override void OnInit()
    {
        // Called once when indicator loads. Subscribe to data feeds here.
        // e.g. this.Symbol.Subscribe(SubscribeQuoteType.Level2);
    }

    protected override void OnUpdate(UpdateArgs args)
    {
        // Called on every bar/tick update. Put calculations here.
        if (Count < Period) return;  // not enough bars yet

        double value = Close();  // example
        SetValue(value);         // push to buffer line 0
    }

    public override void OnPaintChart(PaintChartEventArgs args)
    {
        // Called every chart repaint. Draw zones, boxes, text here.
        Graphics gr = args.Graphics;
        // ... GDI+ drawing (see section 6)
    }

    public override void Dispose()
    {
        // Clean up: dispose pens, brushes, subscriptions
        base.Dispose();
    }
}
```

---

## 3. Accessing price data (current chart TF)

```csharp
// Current bar
double c   = Close();
double o   = Open();
double h   = High();
double l   = Low();
double v   = GetPrice(PriceType.Volume);

// N bars back (0 = current)
double c5  = Close(5);
double h2  = High(2);

// Generic form
double val = GetPrice(PriceType.Close, 3);

// Bar count (use to guard against short history)
int bars = Count;
```

---

## 4. Input parameters

```csharp
// Integer
[InputParameter("Period", sortIndex, min, max, increment, decimalPlaces)]
public int Period = 14;

// Double
[InputParameter("Threshold", 1, 0.0, 10.0, 0.1, 2)]
public double Threshold = 1.5;

// Bool toggle
[InputParameter("Show labels", 2)]
public bool ShowLabels = true;

// Color
[InputParameter("Line color", 3)]
public Color LineColor = Color.CadetBlue;

// Dropdown (variants)
[InputParameter("Mode", 4, variants: new object[]
{
    "Fast",   0,
    "Normal", 1,
    "Slow",   2
})]
public int Mode = 1;

// Symbol picker (for multi-symbol indicators)
[InputParameter("Second symbol", 5)]
public Symbol SecondSymbol;
```

sortIndex controls the order in Quantower's settings panel. Use unique numbers.

---

## 5. Multi-timeframe history (HTF data)

```csharp
private HistoricalData htfData;

protected override void OnInit()
{
    // Download 1h history — live-updating (no end date = keeps streaming)
    htfData = this.Symbol.GetHistory(Period.HOUR1, HistoryType.Bid, DateTime.UtcNow.AddDays(-90));
}

protected override void OnUpdate(UpdateArgs args)
{
    if (htfData == null || htfData.Count < 2) return;

    // Access HTF bars — index 0 = most recent closed bar
    var bar = htfData[0, SeekOriginHistory.Begin] as HistoryItemBar;
    if (bar == null) return;

    double htfClose = bar.Close;
    double htfHigh  = bar.High;
    double htfLow   = bar.Low;
    double htfOpen  = bar.Open;
    // bar.TimeLeft = bar's timestamp
}

public override void Dispose()
{
    htfData?.Dispose();
    base.Dispose();
}
```

**Key patterns:**
- `SeekOriginHistory.Begin` = index from oldest bar forward
- `SeekOriginHistory.End` = index from newest bar (0 = latest closed)
- Cast `IHistoryItem` to `HistoryItemBar` for OHLCV, `HistoryItemTick` for tick data
- Always check for null after cast
- Always dispose HistoricalData objects in `Dispose()`

---

## 6. Custom painting — OnPaintChart / GDI+

```csharp
public override void OnPaintChart(PaintChartEventArgs args)
{
    Graphics gr    = args.Graphics;
    var      win   = args.MainWindow;  // chart window object (dynamic)

    // Convert price → Y pixel
    double price = 20000.0;
    int    yPx   = (int)win.CoordinatesConverter.GetChartY(price);

    // Convert bar index → X pixel
    int barIdx = 10;  // bars from left
    int xPx    = (int)win.CoordinatesConverter.GetChartX(barIdx);

    // Draw a horizontal line at a price level
    gr.DrawLine(new Pen(Color.Yellow, 2), 0, yPx, win.ClientRectangle.Width, yPx);

    // Draw a filled rectangle (zone box)
    float xLeft  = 100;
    float yTop   = (float)win.CoordinatesConverter.GetChartY(20050.0);
    float yBot   = (float)win.CoordinatesConverter.GetChartY(19950.0);
    float width  = win.ClientRectangle.Width - 100;
    float height = yBot - yTop;

    gr.FillRectangle(new SolidBrush(Color.FromArgb(40, 0, 200, 0)), xLeft, yTop, width, height);
    gr.DrawRectangle(new Pen(Color.LimeGreen, 1), xLeft, yTop, width, height);

    // Draw text
    gr.DrawString("Zone label", new Font("Segoe UI", 9), Brushes.White, xLeft + 4, yTop + 2);
}
```

**Coordinate conversion — the critical pattern used in IOF:**
```csharp
// Price → pixels (use win.CoordinatesConverter)
int y = (int)win.CoordinatesConverter.GetChartY(price);

// Bar time → pixels  
int x = (int)win.CoordinatesConverter.GetChartX(barDateTime);

// Or by bar index offset from the right edge
// (IOF uses this approach — walk back from rightmost visible bar)
```

**Performance note:** GDI+ pens and brushes created inside `OnPaintChart` should ideally be cached as fields and disposed in `Dispose()` — creating them every paint call is wasteful but works for low-frequency repaints.

---

## 7. Chart window access pattern (IOF-style)

The IOF indicator uses `dynamic` typing for the chart window to avoid compile-time binding:

```csharp
public override void OnPaintChart(PaintChartEventArgs args)
{
    Graphics gr  = args.Graphics;
    dynamic  win = args.MainWindow;

    try
    {
        // All chart window access inside try/catch — Quantower API can change
        var rect = win.ClientRectangle;
        int rightPx  = rect.Right;
        int leftPx   = rect.Left;
    }
    catch { /* never block draw */ }
}
```

---

## 8. Historical data iteration (IOF scanner pattern)

```csharp
// Scan backwards through chart history
int total = this.HistoricalData.Count;

for (int i = total - 1; i >= 0; i--)
{
    var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
    if (bar == null) continue;

    double barHigh  = bar.High;
    double barLow   = bar.Low;
    double barClose = bar.Close;
    double barOpen  = bar.Open;
    // bar.TimeLeft = bar's open timestamp (DateTime)
}
```

---

## 9. Build & deploy workflow (current — requires Brandon's zip)

1. Brandon builds with Visual Studio on Windows (needs `TradingPlatform.BusinessLayer.dll` from `C:\Quantower\TradingPlatform\v1.145.17\bin\`)
2. Output: `TradePhantoms_IOF_v2.dll`
3. Packaged into zip with all `.cs` source files
4. Install: drop zip contents into `C:\Quantower\Settings\Scripts\Indicators\TradePhantoms_IOF_v2\`
5. Restart Quantower or refresh scripts

**Future goal:** Once `TradingPlatform.BusinessLayer.dll` is committed to repo (`refs/` folder), build pipeline runs here on Linux — `dotnet build` → auto-package zip → push to Drive.

---

## 10. Quantower API reference links

- Indicator class: https://api.quantower.com/docs/TradingPlatform.BusinessLayer.Indicator.html
- HistoricalData class: https://api.quantower.com/docs/TradingPlatform.BusinessLayer.HistoricalData.html
- Symbol class: https://api.quantower.com/docs/TradingPlatform.BusinessLayer.Symbol.html
- Example repo: https://github.com/Quantower/Examples
- KB repo: https://github.com/Quantower/QuantowerKB

---

*Sources: Quantower official docs (help.quantower.com), QuantowerKB GitHub, Quantower API reference (api.quantower.com)*
