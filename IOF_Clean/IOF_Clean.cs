// =============================================================================
// IOF_Clean.cs — Standalone supply/demand zone detector + position sizer
// =============================================================================
// Self-contained. Does NOT depend on IOF v2 or any other indicator.
//
// Zone detection:
//   Finds base candles (small range) followed by a strong departure move.
//   Classifies each zone: RBR / DBR (demand) or RBD / DBD (supply).
//   Draws clean semi-transparent boxes. Fades tested zones.
//   Invalidates zones when price closes through them.
//
// Position sizing:
//   When price enters or approaches a zone, shows a panel with:
//     - Direction (LONG / SHORT)
//     - Contracts to enter based on your risk and stop distance
//     - Exact stop price, tick distance, and dollar risk
//
// Parameters to tune:
//   Base Max Range Ticks  — how small a candle must be to count as a base
//   Departure Multiplier  — departure move must be N× the base size
//   Risk Per Trade ($)    — your max dollar risk per trade
//   Max Contracts         — hard cap (set to your account's limit)
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    public class IOF_Clean : Indicator
    {
        // ── Parameters ────────────────────────────────────────────────────────

        [InputParameter("Risk Per Trade ($)", 0, 10.0, 5000.0, 10.0, 2)]
        public double RiskPerTrade = 200.0;

        [InputParameter("Max Contracts (account cap)", 1, 1, 20, 1, 0)]
        public int MaxContracts = 10;

        [InputParameter("Stop Buffer Ticks (past zone edge)", 2, 1, 30, 1, 0)]
        public int StopBufferTicks = 4;

        [InputParameter("Base Max Range Ticks", 3, 3, 200, 1, 0)]
        public int BaseMaxRangeTicks = 20;

        [InputParameter("Departure Multiplier", 4, 1.0, 10.0, 0.5, 1)]
        public double DepartureMultiplier = 2.0;

        [InputParameter("Lookback Bars", 5, 50, 1000, 25, 0)]
        public int LookbackBars = 200;

        [InputParameter("Max Zones Per Side", 6, 1, 10, 1, 0)]
        public int MaxZones = 4;

        [InputParameter("Zone Proximity Ticks (show panel)", 7, 1, 150, 1, 0)]
        public int ZoneProximityTicks = 15;

        [InputParameter("Zone Fill Opacity (0-255)", 8, 5, 255, 5, 0)]
        public int ZoneOpacity = 35;

        [InputParameter("Show Zone Labels", 9)]
        public bool ShowLabels = true;

        // ── Internal state ────────────────────────────────────────────────────

        private List<ZoneBox> _zones = new List<ZoneBox>();
        private readonly object _zoneLock = new object();
        private double _lastPrice;

        private Font _fontLabel;
        private Font _fontTitle;
        private Font _fontBig;
        private Font _fontDetail;

        private enum ZType { RBR, DBR, RBD, DBD }

        private class ZoneBox
        {
            public double Top;
            public double Bottom;
            public ZType  Type;
            public bool   IsDemand;
            public int    Touches;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public IOF_Clean()
        {
            Name           = "IOF Clean";
            Description    = "Clean supply/demand zones + contract sizer. Standalone — no IOF v2 needed.";
            SeparateWindow = false;
            AddLineSeries("Clean_anchor", Color.Transparent, 1, LineStyle.Solid);
        }

        protected override void OnInit()
        {
            _fontLabel  = new Font("Consolas", 8f,  FontStyle.Bold);
            _fontTitle  = new Font("Consolas", 9f,  FontStyle.Bold);
            _fontBig    = new Font("Consolas", 20f, FontStyle.Bold);
            _fontDetail = new Font("Consolas", 9f,  FontStyle.Regular);
            lock (_zoneLock) { _zones.Clear(); }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (this.Symbol == null) return;
            if (args.Reason != UpdateReason.NewBar && args.Reason != UpdateReason.HistoricalBar) return;
            if (this.Count < LookbackBars + 3) return;

            _lastPrice = this.GetPrice(PriceType.Close, 0);
            ScanZones();
        }

        // ── Zone detection ────────────────────────────────────────────────────

        private void ScanZones()
        {
            double tick         = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double baseMax      = BaseMaxRangeTicks * tick;
            double price        = _lastPrice;
            int    lookback     = Math.Min(LookbackBars, this.Count - 2);
            var    candidates   = new List<ZoneBox>();

            for (int i = 1; i < lookback; i++)
            {
                // i   = base bar
                // i-1 = departure bar (more recent)
                // i+1 = approach bar  (older)

                double baseHi  = this.GetPrice(PriceType.High,  i);
                double baseLo  = this.GetPrice(PriceType.Low,   i);
                double baseRng = baseHi - baseLo;

                if (baseRng <= 0 || baseRng > baseMax) continue;

                // Departure bar
                double depHi  = this.GetPrice(PriceType.High,  i - 1);
                double depLo  = this.GetPrice(PriceType.Low,   i - 1);
                double depO   = this.GetPrice(PriceType.Open,  i - 1);
                double depC   = this.GetPrice(PriceType.Close, i - 1);
                double depRng = depHi - depLo;

                if (depRng < DepartureMultiplier * baseRng) continue;

                bool depBull = depC > depO && depC > baseHi;
                bool depBear = depC < depO && depC < baseLo;
                if (!depBull && !depBear) continue;

                // Approach bar
                double appO = this.GetPrice(PriceType.Open,  i + 1);
                double appC = this.GetPrice(PriceType.Close, i + 1);

                // Zone must be on the right side of price
                bool isDemand = depBull;
                if (isDemand  && baseHi >= price) continue;
                if (!isDemand && baseLo <= price) continue;

                // Invalidated if price closed through zone after formation
                bool dead = false;
                for (int j = i - 1; j >= 0; j--)
                {
                    double c = this.GetPrice(PriceType.Close, j);
                    if (isDemand && c < baseLo - tick)  { dead = true; break; }
                    if (!isDemand && c > baseHi + tick) { dead = true; break; }
                }
                if (dead) continue;

                // Deduplicate
                bool dup = false;
                foreach (var ex in candidates)
                    if (Math.Abs(ex.Top - baseHi) < tick * 3 && Math.Abs(ex.Bottom - baseLo) < tick * 3)
                    { dup = true; break; }
                if (dup) continue;

                // Count touches after formation
                int touches = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    double hi = this.GetPrice(PriceType.High, j);
                    double lo = this.GetPrice(PriceType.Low,  j);
                    if (hi >= baseLo && lo <= baseHi) touches++;
                }

                ZType ztype;
                if (isDemand)
                    ztype = (appC < appO) ? ZType.DBR : ZType.RBR;
                else
                    ztype = (appC > appO) ? ZType.RBD : ZType.DBD;

                var z      = new ZoneBox();
                z.Top      = baseHi;
                z.Bottom   = baseLo;
                z.Type     = ztype;
                z.IsDemand = isDemand;
                z.Touches  = touches;
                candidates.Add(z);
            }

            // Keep N closest zones per side
            var demand = new List<ZoneBox>();
            var supply = new List<ZoneBox>();
            foreach (var z in candidates)
            {
                if (z.IsDemand) demand.Add(z);
                else supply.Add(z);
            }

            // Demand: sort highest top first (closest below price)
            demand.Sort((a, b) => b.Top.CompareTo(a.Top));
            // Supply: sort lowest bottom first (closest above price)
            supply.Sort((a, b) => a.Bottom.CompareTo(b.Bottom));

            var result = new List<ZoneBox>();
            for (int i = 0; i < Math.Min(MaxZones, demand.Count); i++) result.Add(demand[i]);
            for (int i = 0; i < Math.Min(MaxZones, supply.Count); i++) result.Add(supply[i]);

            lock (_zoneLock) { _zones.Clear(); _zones.AddRange(result); }
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null || this.CurrentChart == null) return;

            List<ZoneBox> zones;
            lock (_zoneLock) { zones = new List<ZoneBox>(_zones); }
            if (zones.Count == 0) return;

            var gr   = args.Graphics;
            var win  = this.CurrentChart.MainWindow;
            if (win == null) return;

            var    rect      = (Rectangle)win.ClientRectangle;
            double tick      = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double price     = _lastPrice;
            double proximity = ZoneProximityTicks * tick;

            ZoneBox nearest     = null;
            double  nearestDist = double.MaxValue;

            foreach (var z in zones)
            {
                int yTop, yBot;
                try
                {
                    yTop = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(z.Top));
                    yBot = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(z.Bottom));
                }
                catch { continue; }

                if (yTop > yBot) { int t = yTop; yTop = yBot; yBot = t; }
                if (yBot < rect.Top || yTop > rect.Bottom) continue;

                int drawTop = Math.Max(yTop, rect.Top);
                int drawBot = Math.Min(yBot, rect.Bottom);
                int drawH   = Math.Max(1, drawBot - drawTop);

                // Tested zones render at half opacity
                int opacity = z.Touches > 0 ? ZoneOpacity / 2 : ZoneOpacity;
                Color fill  = z.IsDemand
                    ? Color.FromArgb(opacity, 0, 200, 90)
                    : Color.FromArgb(opacity, 255, 55, 55);

                using (var fb = new SolidBrush(fill))
                    gr.FillRectangle(fb, rect.Left, drawTop, rect.Width, drawH);

                // Proximal edge line (top of demand zone, bottom of supply zone)
                Color edge = z.IsDemand
                    ? Color.FromArgb(160, 0, 230, 100)
                    : Color.FromArgb(160, 255, 80, 80);
                int edgeY = z.IsDemand ? yTop : yBot;

                using (var ep = new Pen(edge, 1))
                    gr.DrawLine(ep, rect.Left, edgeY, rect.Right, edgeY);

                // Label
                if (ShowLabels)
                {
                    string lbl = z.Type.ToString() + (z.Touches > 0 ? "  (" + z.Touches + "T)" : "  FRESH");
                    using (var lb = new SolidBrush(Color.FromArgb(190, edge)))
                        gr.DrawString(lbl, _fontLabel, lb, rect.Left + 6,
                            z.IsDemand ? yTop - 15 : yBot + 3);
                }

                // Track nearest for sizing panel
                double dist = price < z.Bottom ? z.Bottom - price
                            : price > z.Top    ? price - z.Top
                            : 0.0;
                if (dist < nearestDist) { nearestDist = dist; nearest = z; }
            }

            // Sizing panel when near a zone
            if (nearest != null && nearestDist <= proximity)
                DrawSizerPanel(gr, win, rect, nearest, price, tick);
        }

        private void DrawSizerPanel(Graphics gr, dynamic win, Rectangle rect,
                                    ZoneBox zone, double price, double tick)
        {
            bool   isLong    = zone.IsDemand;
            double buf       = StopBufferTicks * tick;
            double stopPrice = isLong ? zone.Bottom - buf : zone.Top + buf;
            double stopDist  = Math.Abs(price - stopPrice);
            double stopTicks = stopDist / tick;
            double ptVal     = ResolvePointValue();
            double riskPerCt = stopDist * ptVal;

            if (riskPerCt <= 0) return;

            int    cts       = (int)Math.Floor(RiskPerTrade / riskPerCt);
            cts = Math.Max(1, Math.Min(cts, MaxContracts));
            double totalRisk = cts * riskPerCt;

            Color accent  = isLong ? Color.FromArgb(255, 0, 215, 100)  : Color.FromArgb(255, 255, 65, 65);
            Color bg      = isLong ? Color.FromArgb(235, 0, 22, 8)     : Color.FromArgb(235, 28, 0, 0);

            int pw = 260, ph = 140;
            int px = rect.Right - pw - 12, py = 12;

            using (var bgb = new SolidBrush(bg))
                gr.FillRectangle(bgb, px, py, pw, ph);
            using (var brd = new Pen(accent, 2))
                gr.DrawRectangle(brd, px, py, pw, ph);
            using (var topBar = new SolidBrush(Color.FromArgb(65, accent.R, accent.G, accent.B)))
                gr.FillRectangle(topBar, px + 2, py + 2, pw - 4, 20);

            string inside = (price >= zone.Bottom && price <= zone.Top) ? "INSIDE" : "NEAR";
            using (var tb = new SolidBrush(accent))
                gr.DrawString(inside + "  " + zone.Type.ToString() + "  ·  " + (isLong ? "LONG" : "SHORT"),
                    _fontTitle, tb, px + 8, py + 5);

            using (var cb = new SolidBrush(Color.White))
                gr.DrawString(cts + " contracts", _fontBig, cb, px + 8, py + 24);

            using (var div = new Pen(Color.FromArgb(35, 255, 255, 255), 1))
                gr.DrawLine(div, px + 8, py + 70, px + pw - 8, py + 70);

            string[] lines =
            {
                "Entry  " + price.ToString("F2"),
                "Stop   " + stopPrice.ToString("F2") + "  (" + ((int)Math.Round(stopTicks)) + " ticks)",
                "Risk   $" + ((int)Math.Round(totalRisk)) + "  ($" + ((int)Math.Round(riskPerCt)) + "/ct)",
                "Zone   " + zone.Bottom.ToString("F2") + " – " + zone.Top.ToString("F2"),
            };

            using (var dt = new SolidBrush(Color.FromArgb(185, 185, 185)))
                for (int i = 0; i < lines.Length; i++)
                    gr.DrawString(lines[i], _fontDetail, dt, px + 8, py + 75 + i * 16);

            // Dashed stop line on chart
            try
            {
                int ys = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(stopPrice));
                if (ys >= rect.Top && ys <= rect.Bottom)
                {
                    using (var sp = new Pen(Color.FromArgb(200, 255, 80, 80), 1))
                    {
                        sp.DashStyle = DashStyle.Dash;
                        gr.DrawLine(sp, rect.Left, ys, rect.Right, ys);
                    }
                    using (var sl = new SolidBrush(Color.FromArgb(200, 255, 80, 80)))
                        gr.DrawString("STOP  " + stopPrice.ToString("F2"), _fontDetail, sl,
                            rect.Left + 4, ys - 14);
                }
            }
            catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private double ResolvePointValue()
        {
            if (this.Symbol == null) return 2.0;
            double ts = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            foreach (var p in new string[] { "TickCost", "TickValue", "PointValue", "ContractMultiplier" })
            {
                try
                {
                    var prop = this.Symbol.GetType().GetProperty(p);
                    if (prop == null) continue;
                    double n = Convert.ToDouble(prop.GetValue(this.Symbol));
                    if (n <= 0) continue;
                    return (p == "TickCost" || p == "TickValue") ? n / ts : n;
                }
                catch { }
            }
            string root = this.Symbol.Name.TrimEnd("0123456789HMUZ".ToCharArray()).ToUpperInvariant();
            switch (root)
            {
                case "MNQ": return 2.0;
                case "NQ":  return 20.0;
                case "MES": return 5.0;
                case "ES":  return 50.0;
                case "M2K": return 5.0;
                case "RTY": return 50.0;
                case "MYM": return 0.5;
                case "YM":  return 5.0;
                case "CL":  return 1000.0;
                case "MCL": return 100.0;
                case "GC":  return 100.0;
                case "MGC": return 10.0;
                default:    return 2.0;
            }
        }

        protected override void OnClear()
        {
            lock (_zoneLock) { _zones.Clear(); }
        }

        public override void Dispose()
        {
            _fontLabel?.Dispose();
            _fontTitle?.Dispose();
            _fontBig?.Dispose();
            _fontDetail?.Dispose();
            base.Dispose();
        }
    }
}
