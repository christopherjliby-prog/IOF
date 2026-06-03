// =============================================================================
// IOF_Clean.cs — Standalone supply/demand zone detector + position sizer
// =============================================================================
// Self-contained. No IOF v2 dependency. No GetPrice() — uses HistoricalData
// directly, same as VolumeSpike (confirmed working in Quantower scripting).
//
// Zone detection:
//   Scans for small base candles followed by a strong departure move.
//   Classifies: RBR / DBR (demand) or RBD / DBD (supply).
//   Draws clean semi-transparent boxes. Fades tested zones.
//
// Sizing panel:
//   Appears when price is within ZoneProximityTicks of a zone.
//   Shows: direction, contracts, stop price, tick distance, dollar risk.
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
        public int BaseMaxRangeTicks = 25;

        [InputParameter("Departure Multiplier (x base)", 4, 1.0, 10.0, 0.5, 1)]
        public double DepartureMultiplier = 1.5;

        [InputParameter("Lookback Bars", 5, 20, 1000, 25, 0)]
        public int LookbackBars = 150;

        [InputParameter("Max Zones Per Side", 6, 1, 10, 1, 0)]
        public int MaxZones = 5;

        [InputParameter("Zone Proximity Ticks (show panel)", 7, 1, 150, 1, 0)]
        public int ZoneProximityTicks = 20;

        [InputParameter("Zone Fill Opacity (0-255)", 8, 5, 200, 5, 0)]
        public int ZoneOpacity = 40;

        [InputParameter("Show Zone Labels", 9)]
        public bool ShowLabels = true;

        // ── Internal ──────────────────────────────────────────────────────────

        private List<ZoneBox> _zones = new List<ZoneBox>();
        private readonly object _zoneLock = new object();

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
            Description    = "Standalone supply/demand zones + contract sizer.";
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
            if (this.HistoricalData == null || this.HistoricalData.Count < 5) return;

            ScanZones();
        }

        // ── Zone scan ─────────────────────────────────────────────────────────

        private void ScanZones()
        {
            int    total    = this.HistoricalData.Count;
            int    lookback = Math.Min(LookbackBars, total - 3);
            double tick     = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double baseMax  = BaseMaxRangeTicks * tick;

            // current price = close of most recent bar
            var curBar = this.HistoricalData[total - 1, SeekOriginHistory.Begin] as HistoryItemBar;
            if (curBar == null) return;
            double price = curBar.Close;

            var candidates = new List<ZoneBox>();

            // Scan from most recent bar backwards.
            // idx = base bar index in HistoricalData (0=oldest)
            // idx+1 = departure bar (more recent, just formed)  ← wait, that's wrong
            // Let me be explicit:
            //   For base at absolute index b:
            //     departure bar = b+1  (one bar newer, closer to current)
            //     approach bar  = b-1  (one bar older)
            // So b ranges from 1 to (total-2) to have valid neighbors.
            // We scan most recent first: b from (total-2) down to (total-1-lookback)

            int scanStart = total - 2;
            int scanEnd   = Math.Max(1, total - 1 - lookback);

            for (int b = scanStart; b >= scanEnd; b--)
            {
                var baseBar = this.HistoricalData[b, SeekOriginHistory.Begin] as HistoryItemBar;
                if (baseBar == null) continue;

                double baseHi  = baseBar.High;
                double baseLo  = baseBar.Low;
                double baseRng = baseHi - baseLo;

                if (baseRng <= 0 || baseRng > baseMax) continue;

                // Departure bar (b+1, more recent — just closed after the base)
                var depBar = this.HistoricalData[b + 1, SeekOriginHistory.Begin] as HistoryItemBar;
                if (depBar == null) continue;

                double depRng = depBar.High - depBar.Low;
                if (depRng < DepartureMultiplier * baseRng) continue;

                bool depBull = depBar.Close > depBar.Open;
                bool depBear = depBar.Close < depBar.Open;
                if (!depBull && !depBear) continue;

                bool isDemand = depBull;

                // Zone must sit on correct side of current price
                if (isDemand  && baseHi >= price) continue;
                if (!isDemand && baseLo <= price) continue;

                // Approach bar (b-1, older)
                ZType ztype = ZType.DBR;
                if (b >= 1)
                {
                    var appBar = this.HistoricalData[b - 1, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (appBar != null)
                    {
                        bool appBull = appBar.Close > appBar.Open;
                        if (isDemand)
                            ztype = appBull ? ZType.RBR : ZType.DBR;
                        else
                            ztype = appBull ? ZType.RBD : ZType.DBD;
                    }
                }

                // Invalidated: any bar after the base that closed through it
                bool dead = false;
                for (int j = b + 2; j < total; j++)
                {
                    var jBar = this.HistoricalData[j, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (jBar == null) continue;
                    if (isDemand  && jBar.Close < baseLo - tick) { dead = true; break; }
                    if (!isDemand && jBar.Close > baseHi + tick) { dead = true; break; }
                }
                if (dead) continue;

                // Deduplicate
                bool dup = false;
                foreach (var ex in candidates)
                    if (Math.Abs(ex.Top - baseHi) < tick * 3 && Math.Abs(ex.Bottom - baseLo) < tick * 3)
                    { dup = true; break; }
                if (dup) continue;

                // Count touches (bars after departure that entered the zone)
                int touches = 0;
                for (int j = b + 2; j < total; j++)
                {
                    var jBar = this.HistoricalData[j, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (jBar != null && jBar.High >= baseLo && jBar.Low <= baseHi)
                        touches++;
                }

                var z      = new ZoneBox();
                z.Top      = baseHi;
                z.Bottom   = baseLo;
                z.Type     = ztype;
                z.IsDemand = isDemand;
                z.Touches  = touches;
                candidates.Add(z);
            }

            // Keep N closest per side
            var demand = new List<ZoneBox>();
            var supply = new List<ZoneBox>();
            foreach (var z in candidates)
            {
                if (z.IsDemand) demand.Add(z);
                else supply.Add(z);
            }
            demand.Sort((a, b2) => b2.Top.CompareTo(a.Top));         // highest top first
            supply.Sort((a, b2) => a.Bottom.CompareTo(b2.Bottom));   // lowest bottom first

            var result = new List<ZoneBox>();
            for (int i = 0; i < Math.Min(MaxZones, demand.Count); i++) result.Add(demand[i]);
            for (int i = 0; i < Math.Min(MaxZones, supply.Count); i++) result.Add(supply[i]);

            lock (_zoneLock) { _zones.Clear(); _zones.AddRange(result); }
        }

        // ── Paint ─────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null || this.CurrentChart == null) return;
            if (this.HistoricalData == null || this.HistoricalData.Count < 2) return;

            List<ZoneBox> zones;
            lock (_zoneLock) { zones = new List<ZoneBox>(_zones); }
            if (zones.Count == 0) return;

            var gr  = args.Graphics;
            var win = this.CurrentChart.MainWindow;
            if (win == null) return;

            var rect = (Rectangle)win.ClientRectangle;

            // Get current price directly from HistoricalData (safe in OnPaintChart)
            double price = 0;
            try
            {
                var bar = this.HistoricalData[this.HistoricalData.Count - 1, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar != null) price = bar.Close;
            }
            catch { return; }

            double tick      = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
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

                if (yTop > yBot) { int tmp = yTop; yTop = yBot; yBot = tmp; }
                if (yBot < rect.Top || yTop > rect.Bottom) continue;

                int drawTop = Math.Max(yTop, rect.Top);
                int drawBot = Math.Min(yBot, rect.Bottom);
                int drawH   = Math.Max(1, drawBot - drawTop);

                int opacity = z.Touches > 0 ? ZoneOpacity / 2 : ZoneOpacity;
                Color fill  = z.IsDemand
                    ? Color.FromArgb(opacity, 0, 200, 90)
                    : Color.FromArgb(opacity, 255, 55, 55);

                using (var fb = new SolidBrush(fill))
                    gr.FillRectangle(fb, rect.Left, drawTop, rect.Width, drawH);

                Color edge = z.IsDemand
                    ? Color.FromArgb(180, 0, 230, 100)
                    : Color.FromArgb(180, 255, 80, 80);

                int edgeY = z.IsDemand ? yTop : yBot;
                using (var ep = new Pen(edge, 1))
                    gr.DrawLine(ep, rect.Left, edgeY, rect.Right, edgeY);

                if (ShowLabels)
                {
                    string lbl = z.Type.ToString() + (z.Touches > 0 ? "  (" + z.Touches + "T)" : "  FRESH");
                    using (var lb = new SolidBrush(Color.FromArgb(200, edge)))
                        gr.DrawString(lbl, _fontLabel, lb, rect.Left + 6,
                            z.IsDemand ? yTop - 15 : yBot + 3);
                }

                double dist = price < z.Bottom ? z.Bottom - price
                            : price > z.Top    ? price - z.Top
                            : 0.0;
                if (dist < nearestDist) { nearestDist = dist; nearest = z; }
            }

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

            Color accent = isLong ? Color.FromArgb(255, 0, 215, 100) : Color.FromArgb(255, 255, 65, 65);
            Color bg     = isLong ? Color.FromArgb(235, 0, 22, 8)    : Color.FromArgb(235, 28, 0, 0);

            int pw = 260, ph = 140, px = rect.Right - 272, py = 12;

            using (var bgb = new SolidBrush(bg))           gr.FillRectangle(bgb, px, py, pw, ph);
            using (var brd = new Pen(accent, 2))            gr.DrawRectangle(brd, px, py, pw, ph);
            using (var bar = new SolidBrush(Color.FromArgb(65, accent.R, accent.G, accent.B)))
                gr.FillRectangle(bar, px + 2, py + 2, pw - 4, 20);

            string status = (price >= zone.Bottom && price <= zone.Top) ? "INSIDE" : "NEAR";
            using (var tb = new SolidBrush(accent))
                gr.DrawString(status + "  " + zone.Type.ToString() + "  ·  " + (isLong ? "LONG" : "SHORT"),
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

            try
            {
                int ys = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(stopPrice));
                if (ys >= rect.Top && ys <= rect.Bottom)
                {
                    using (var sp = new Pen(Color.FromArgb(200, 255, 80, 80), 1))
                    { sp.DashStyle = DashStyle.Dash; gr.DrawLine(sp, rect.Left, ys, rect.Right, ys); }
                    using (var sl = new SolidBrush(Color.FromArgb(200, 255, 80, 80)))
                        gr.DrawString("STOP  " + stopPrice.ToString("F2"), _fontDetail, sl, rect.Left + 4, ys - 14);
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
                case "MNQ": return 2.0;   case "NQ":  return 20.0;
                case "MES": return 5.0;   case "ES":  return 50.0;
                case "M2K": return 5.0;   case "RTY": return 50.0;
                case "MYM": return 0.5;   case "YM":  return 5.0;
                case "CL":  return 1000.0; case "MCL": return 100.0;
                case "GC":  return 100.0;  case "MGC": return 10.0;
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
