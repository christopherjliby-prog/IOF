// IOF_DOMHeatmap.cs  — Bookmap-style DOM liquidity heatmap for Quantower
//
// Overlays a real-time order book (DOM) heatmap directly on the price chart.
// Every bar, the current bid/ask depth is captured and stored. On repaint,
// each captured snapshot is rendered as heat-colored rectangles:
//
//   DARK BLUE  = thin liquidity (small orders)
//   GREEN      = moderate liquidity
//   YELLOW     = heavy liquidity
//   RED/WHITE  = wall (very large orders stacked at that level)
//
// MODES
//   Combined (default) — total DOM volume = bid + ask, single color gradient
//   Split              — bid (green) left half, ask (red) right half per bar
//
// FALLBACK
//   If Level2/DOM data is unavailable (no subscription, brokerage doesn't
//   provide it), the indicator falls back to VolumeAnalysisData.PriceLevels
//   which shows TRADED volume at price — still extremely useful, just not
//   a live order book. Enable "Use volume profile fallback" in settings.
//
// LOAD ORDER — standalone indicator, no dependencies. Works alone.
//
// INSTALL
//   Drop IOF_DOMHeatmap/ folder into:
//   C:\Quantower\Settings\Scripts\Indicators\
//
// Confirmed SDK: TradingPlatform.BusinessLayer v1.145.17

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    public class IOF_DOMHeatmap : Indicator, IVolumeAnalysisIndicator
    {
        // ── Display ───────────────────────────────────────────────────────────────
        [InputParameter("Heatmap opacity (0-255)", 0, 10, 255, 5, 0)]
        public int Opacity = 200;

        [InputParameter("Max DOM levels per side", 1, 5, 100, 5, 0)]
        public int DomDepth = 40;

        [InputParameter("Max bars of history", 2, 50, 1000, 50, 0)]
        public int MaxHistory = 300;

        [InputParameter("Min cell height px", 3, 1, 20, 1, 0)]
        public int MinCellPx = 1;

        // ── Mode ──────────────────────────────────────────────────────────────────
        [InputParameter("Heat mode", 4, variants: new object[]
        {
            "Combined (Bookmap)", 0,
            "Split bid/ask",      1,
            "Bid side only",      2,
            "Ask side only",      3,
        })]
        public int HeatMode = 0;

        // ── Thresholds ────────────────────────────────────────────────────────────
        [InputParameter("Wall threshold (x avg volume)", 5, 1.0, 20.0, 0.5, 1)]
        public double WallThreshold = 5.0;

        [InputParameter("Show wall labels", 6)]
        public bool ShowWallLabels = true;

        [InputParameter("Wall label min size", 7, 1, 10000, 100, 0)]
        public int WallLabelMinSize = 500;

        // ── Fallback ──────────────────────────────────────────────────────────────
        [InputParameter("Use volume profile fallback", 8)]
        public bool UseVpFallback = false;

        // ── Auto-discover DOM property ─────────────────────────────────────────────
        [InputParameter("Auto-discover DOM property (reflection)", 9)]
        public bool AutoDiscoverDom = false;

        // ── Volume normalization ───────────────────────────────────────────────────
        [InputParameter("Volume scale (0=auto)", 10, 0, 100000, 100, 0)]
        public int ManualVolumeScale = 0;

        // ── Internal ──────────────────────────────────────────────────────────────
        private readonly List<BarSnapshot>   _history          = new List<BarSnapshot>();
        private readonly object              _lock             = new object();
        private double                       _maxVolSeen       = 1.0;
        private bool                         _domFailed        = false;
        private bool                         _vaLoaded         = false;
        private string                       _statusMsg        = "";
        private string                       _discoveredProp   = null;
        private string                       _discoveredBids   = null;
        private string                       _discoveredAsks   = null;
        private Font                         _labelFont;
        private Font                         _statusFont;

        // ── IVolumeAnalysisIndicator ──────────────────────────────────────────────
        public bool IsRequirePriceLevelsCalculation => true;
        public void VolumeAnalysisData_Loaded() { _vaLoaded = true; }

        // ── Snapshot types ────────────────────────────────────────────────────────
        private class DomLevel
        {
            public double Price;
            public double BidVol;
            public double AskVol;
            public double Total => BidVol + AskVol;
        }

        private class BarSnapshot
        {
            public DateTime          Time;
            public List<DomLevel>    Levels = new List<DomLevel>();
            public double            MaxTotal;
        }

        // ─────────────────────────────────────────────────────────────────────────
        public IOF_DOMHeatmap() : base()
        {
            Name           = "IOF DOM Heatmap";
            Description    = "Bookmap-style DOM liquidity overlay. Shows order book depth as heat-colored cells on the price chart.";
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            _labelFont  = new Font("Segoe UI", 7f, FontStyle.Regular);
            _statusFont = new Font("Segoe UI", 9f, FontStyle.Bold);

            if (UseVpFallback)
                _statusMsg = "VP mode: traded volume at price (enable chart Volume Analysis for best results)";
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (UseVpFallback)
            {
                CaptureFromVolumeProfile();
                return;
            }
            CaptureDOM();
        }

        // ── DOM property discovery ────────────────────────────────────────────────

        // Scans Symbol via reflection to find the first property that looks like
        // a DOM/depth object with Bids and Asks collections. Caches result so it
        // only runs once per indicator load.
        private bool DiscoverDomProperty()
        {
            if (_discoveredProp != null) return true;
            if (Symbol == null) return false;

            try
            {
                var symbolType = Symbol.GetType();
                var allProps   = symbolType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

                // Candidate DOM property names (ordered by likelihood)
                var domCandidates = new[]
                {
                    "DepthOfMarket", "DOMItems", "OrderBook", "Level2",
                    "DOM", "Depth", "MarketDepth", "BookDepth", "L2"
                };

                // First pass: try known names
                foreach (var name in domCandidates)
                {
                    var prop = symbolType.GetProperty(name,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (prop == null) continue;

                    var val = prop.GetValue(Symbol);
                    if (val == null) continue;

                    var (bids, asks) = FindBidsAsks(val);
                    if (bids != null && asks != null)
                    {
                        _discoveredProp = prop.Name;
                        _discoveredBids = bids;
                        _discoveredAsks = asks;
                        _statusMsg = $"DOM discovered: Symbol.{_discoveredProp} (bids={_discoveredBids}, asks={_discoveredAsks})";
                        return true;
                    }
                }

                // Second pass: scan all properties
                foreach (var prop in allProps)
                {
                    try
                    {
                        var val = prop.GetValue(Symbol);
                        if (val == null) continue;
                        var (bids, asks) = FindBidsAsks(val);
                        if (bids != null && asks != null)
                        {
                            _discoveredProp = prop.Name;
                            _discoveredBids = bids;
                            _discoveredAsks = asks;
                            _statusMsg = $"DOM discovered: Symbol.{_discoveredProp} (bids={_discoveredBids}, asks={_discoveredAsks})";
                            return true;
                        }
                    }
                    catch { }
                }

                // Log all property names so user can report back
                var names = string.Join(", ", allProps.Select(p => p.Name));
                _statusMsg = $"DOM not found. Symbol properties: {names}";
                return false;
            }
            catch (Exception ex)
            {
                _statusMsg = $"Discovery error: {ex.Message}";
                return false;
            }
        }

        // Looks for Bids/Asks collection properties on a candidate DOM object.
        private (string Bids, string Asks) FindBidsAsks(object domObj)
        {
            var t    = domObj.GetType();
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            string bids = null, asks = null;
            var bidNames = new[] { "Bids", "BidItems", "BidSide", "Bid", "BuyOrders" };
            var askNames = new[] { "Asks", "AskItems", "AskSide", "Ask", "SellOrders", "Offers" };

            foreach (var p in props)
            {
                var n = p.Name;
                if (bids == null && bidNames.Any(b => string.Equals(b, n, StringComparison.OrdinalIgnoreCase)))
                    bids = p.Name;
                if (asks == null && askNames.Any(a => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)))
                    asks = p.Name;
                if (bids != null && asks != null) return (bids, asks);
            }
            return (null, null);
        }

        // Reads items from a discovered bid or ask collection using reflection.
        private void ReadDomSide(object domObj, string propName, bool isBid, BarSnapshot snap)
        {
            try
            {
                var items = domObj.GetType()
                    .GetProperty(propName, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(domObj) as System.Collections.IEnumerable;

                if (items == null) return;

                int count = 0;
                foreach (var item in items)
                {
                    if (count++ >= DomDepth) break;
                    var itype = item.GetType();

                    double price = 0, size = 0;
                    foreach (var p in itype.GetProperties())
                    {
                        var pn = p.Name.ToLowerInvariant();
                        if (pn == "price" || pn == "level" || pn == "rate")
                            price = Convert.ToDouble(p.GetValue(item));
                        else if (pn == "size" || pn == "volume" || pn == "quantity" || pn == "qty")
                            size = Convert.ToDouble(p.GetValue(item));
                    }

                    if (price <= 0 || size <= 0) continue;
                    var level = snap.Levels.Find(l => l.Price == price);
                    if (level == null) { level = new DomLevel { Price = price }; snap.Levels.Add(level); }
                    if (isBid) level.BidVol += size;
                    else       level.AskVol += size;
                }
            }
            catch { }
        }

        // ── DOM capture ───────────────────────────────────────────────────────────

        private void CaptureDOM()
        {
            try
            {
                // Auto-discover path
                if (AutoDiscoverDom)
                {
                    if (!DiscoverDomProperty()) { _domFailed = true; return; }

                    var domObj = Symbol.GetType()
                        .GetProperty(_discoveredProp, BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(Symbol);

                    if (domObj == null) { _domFailed = true; return; }

                    var snap = new BarSnapshot { Time = Time(0) };
                    ReadDomSide(domObj, _discoveredBids, true,  snap);
                    ReadDomSide(domObj, _discoveredAsks, false, snap);

                    if (snap.Levels.Count == 0) { _domFailed = true; return; }
                    snap.MaxTotal = snap.Levels.Max(l => l.Total);
                    _domFailed = false;
                    CommitSnapshot(snap);
                    return;
                }

                // Standard dynamic path
                dynamic dom = Symbol?.DepthOfMarket;
                if (dom == null)
                {
                    _domFailed = true;
                    _statusMsg = "DOM unavailable — try 'Use volume profile fallback'";
                    return;
                }

                var snap2 = new BarSnapshot { Time = Time(0) };

                // Bids (buy side)
                try
                {
                    int count = 0;
                    foreach (dynamic item in dom.Bids)
                    {
                        if (count++ >= DomDepth) break;
                        double price = (double)item.Price;
                        double size  = (double)item.Size;
                        if (size <= 0) continue;
                        var level = snap2.Levels.Find(l => l.Price == price);
                        if (level == null) { level = new DomLevel { Price = price }; snap2.Levels.Add(level); }
                        level.BidVol += size;
                    }
                }
                catch { }

                // Asks (sell side)
                try
                {
                    int count = 0;
                    foreach (dynamic item in dom.Asks)
                    {
                        if (count++ >= DomDepth) break;
                        double price = (double)item.Price;
                        double size  = (double)item.Size;
                        if (size <= 0) continue;
                        var level = snap2.Levels.Find(l => l.Price == price);
                        if (level == null) { level = new DomLevel { Price = price }; snap2.Levels.Add(level); }
                        level.AskVol += size;
                    }
                }
                catch { }

                if (snap2.Levels.Count == 0)
                {
                    _domFailed = true;
                    _statusMsg = "No DOM data received — enable 'Auto-discover DOM property' or VP fallback";
                    return;
                }

                snap2.MaxTotal = snap2.Levels.Max(l => l.Total);
                _domFailed = false;
                _statusMsg = "";
                CommitSnapshot(snap2);
            }
            catch (Exception ex)
            {
                _domFailed = true;
                _statusMsg = $"DOM error: {ex.Message}";
            }
        }

        private void CaptureFromVolumeProfile()
        {
            try
            {
                var bar = HistoricalData[0, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar?.VolumeAnalysisData?.PriceLevels == null || bar.VolumeAnalysisData.PriceLevels.Count == 0)
                    return;

                var snap = new BarSnapshot { Time = bar.TimeLeft };
                foreach (var kvp in bar.VolumeAnalysisData.PriceLevels)
                {
                    double vol = kvp.Value.Volume;
                    if (vol <= 0) continue;
                    snap.Levels.Add(new DomLevel
                    {
                        Price  = kvp.Key,
                        BidVol = vol * 0.5,
                        AskVol = vol * 0.5,
                    });
                }

                if (snap.Levels.Count == 0) return;
                snap.MaxTotal = snap.Levels.Max(l => l.Total);
                CommitSnapshot(snap);
            }
            catch { }
        }

        private void CommitSnapshot(BarSnapshot snap)
        {
            lock (_lock)
            {
                // Deduplicate by time — replace if same bar
                int existing = _history.FindLastIndex(s => s.Time == snap.Time);
                if (existing >= 0)
                    _history[existing] = snap;
                else
                    _history.Add(snap);

                // Rolling max with slow decay to adapt to current conditions
                _maxVolSeen *= 0.997;
                _maxVolSeen  = Math.Max(_maxVolSeen, snap.MaxTotal);
                if (_maxVolSeen < 1) _maxVolSeen = 1;

                // Trim
                while (_history.Count > MaxHistory)
                    _history.RemoveAt(0);
            }
        }

        // ── Painting ──────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            var gr  = args.Graphics;
            try
            {
                dynamic win  = CurrentChart.MainWindow;
                var     rect = (Rectangle)win.ClientRectangle;

                // Show status if DOM unavailable
                if (_domFailed || _statusMsg.Length > 0)
                {
                    gr.DrawString(
                        $"IOF DOM Heatmap: {_statusMsg}",
                        _statusFont,
                        Brushes.OrangeRed,
                        rect.Left + 6, rect.Top + 6);
                }

                List<BarSnapshot> snapshots;
                lock (_lock)
                    snapshots = new List<BarSnapshot>(_history);

                if (snapshots.Count < 2) return;

                // Estimate bar width from last two snapshots
                int x0       = (int)win.CoordinatesConverter.GetChartX(snapshots[^1].Time);
                int x1       = (int)win.CoordinatesConverter.GetChartX(snapshots[^2].Time);
                int barWidth = Math.Max(1, Math.Abs(x0 - x1));

                double volScale = ManualVolumeScale > 0 ? ManualVolumeScale : _maxVolSeen;
                if (volScale < 1) volScale = 1;

                foreach (var snap in snapshots)
                {
                    int xCenter = (int)win.CoordinatesConverter.GetChartX(snap.Time);

                    // Skip off-screen bars
                    if (xCenter < rect.Left - barWidth || xCenter > rect.Right + barWidth)
                        continue;

                    double avgVol = snap.Levels.Count > 0 ? snap.Levels.Average(l => l.Total) : 1;

                    foreach (var lvl in snap.Levels)
                    {
                        // Price cell boundaries (centered on price, half-tick above/below)
                        double tickSize = Symbol?.TickSize ?? 0.25;
                        double halfTick = tickSize * 0.5;

                        int yTop = (int)win.CoordinatesConverter.GetChartY(lvl.Price + halfTick);
                        int yBot = (int)win.CoordinatesConverter.GetChartY(lvl.Price - halfTick);

                        int cellH = Math.Max(MinCellPx, Math.Abs(yBot - yTop));
                        int yDraw = Math.Min(yTop, yBot);

                        if (yDraw > rect.Bottom || yDraw + cellH < rect.Top) continue;

                        switch (HeatMode)
                        {
                            case 0: // Combined
                            {
                                double t = lvl.Total / volScale;
                                var   c = HeatColor(t, Opacity);
                                gr.FillRectangle(
                                    new SolidBrush(c),
                                    xCenter - barWidth / 2, yDraw, barWidth, cellH);
                                break;
                            }
                            case 1: // Split bid/ask
                            {
                                int half = Math.Max(1, barWidth / 2);
                                if (lvl.BidVol > 0)
                                {
                                    double t = lvl.BidVol / volScale;
                                    var   c = BidColor(t, Opacity);
                                    gr.FillRectangle(
                                        new SolidBrush(c),
                                        xCenter - barWidth / 2, yDraw, half, cellH);
                                }
                                if (lvl.AskVol > 0)
                                {
                                    double t = lvl.AskVol / volScale;
                                    var   c = AskColor(t, Opacity);
                                    gr.FillRectangle(
                                        new SolidBrush(c),
                                        xCenter, yDraw, half, cellH);
                                }
                                break;
                            }
                            case 2: // Bid only
                            {
                                if (lvl.BidVol <= 0) continue;
                                double t = lvl.BidVol / volScale;
                                gr.FillRectangle(
                                    new SolidBrush(BidColor(t, Opacity)),
                                    xCenter - barWidth / 2, yDraw, barWidth, cellH);
                                break;
                            }
                            case 3: // Ask only
                            {
                                if (lvl.AskVol <= 0) continue;
                                double t = lvl.AskVol / volScale;
                                gr.FillRectangle(
                                    new SolidBrush(AskColor(t, Opacity)),
                                    xCenter - barWidth / 2, yDraw, barWidth, cellH);
                                break;
                            }
                        }

                        // Wall label
                        if (ShowWallLabels && lvl.Total >= WallThreshold * avgVol && lvl.Total >= WallLabelMinSize)
                        {
                            string lbl = lvl.Total >= 1000
                                ? $"{lvl.Total / 1000.0:0.#}K"
                                : $"{lvl.Total:0}";
                            gr.DrawString(lbl, _labelFont, Brushes.White,
                                xCenter - barWidth / 2 + 1, yDraw);
                        }
                    }
                }
            }
            catch { }
        }

        // ── Color helpers ─────────────────────────────────────────────────────────

        // Bookmap-style gradient: dark blue → cyan → green → yellow → red → white
        private static Color HeatColor(double t, int alpha)
        {
            t = Math.Max(0, Math.Min(1, t));
            int r, g, b;

            if (t < 0.2)
            {
                float f = (float)(t / 0.2);
                r = 0; g = (int)(60 * f); b = (int)(80 + 175 * f);
            }
            else if (t < 0.4)
            {
                float f = (float)((t - 0.2) / 0.2);
                r = 0; g = (int)(60 + 195 * f); b = (int)(255 * (1 - f));
            }
            else if (t < 0.6)
            {
                float f = (float)((t - 0.4) / 0.2);
                r = (int)(200 * f); g = 255; b = 0;
            }
            else if (t < 0.8)
            {
                float f = (float)((t - 0.6) / 0.2);
                r = 255; g = (int)(255 * (1 - f * 0.6f)); b = 0;
            }
            else
            {
                float f = (float)((t - 0.8) / 0.2);
                r = 255; g = (int)(102 + 153 * f); b = (int)(200 * f);
            }

            return Color.FromArgb(alpha,
                Clamp(r), Clamp(g), Clamp(b));
        }

        // Bid side: dark navy → blue → cyan → bright green
        private static Color BidColor(double t, int alpha)
        {
            t = Math.Max(0, Math.Min(1, t));
            float f = (float)t;
            int r = 0;
            int g = Clamp((int)(80  + 175 * f));
            int b = Clamp((int)(100 + 100 * (1 - f)));
            return Color.FromArgb(alpha, r, g, b);
        }

        // Ask side: dark red → orange → bright red
        private static Color AskColor(double t, int alpha)
        {
            t = Math.Max(0, Math.Min(1, t));
            float f = (float)t;
            int r = Clamp((int)(100 + 155 * f));
            int g = Clamp((int)(30  * (1 - f)));
            int b = 0;
            return Color.FromArgb(alpha, r, g, b);
        }

        private static int Clamp(int v) => Math.Max(0, Math.Min(255, v));

        // ── Cleanup ───────────────────────────────────────────────────────────────

        public override void Dispose()
        {
            _labelFont?.Dispose();
            _statusFont?.Dispose();
            base.Dispose();
        }
    }
}
