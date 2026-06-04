// =============================================================================
// IOF_Clean.cs — Standalone supply/demand zone detector + position sizer
// Detection algorithm is a direct port of MultiTFZoneScanner.ScanTimeframe
// from TradePhantoms IOF v2. No v2 runtime dependency.
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

        // -- Zone detection --
        [InputParameter("Cluster Max Range (ticks)", 3, 5, 500, 5, 0)]
        public double ClusterMaxRangeTicks = 120;

        [InputParameter("Leg Strength Threshold (body/range)", 4, 0.1, 0.9, 0.05, 2)]
        public double LegStrengthPct = 0.5;

        [InputParameter("Min Impulse Ratio (x zone height)", 5, 0.5, 10.0, 0.5, 1)]
        public double MinImpulseRatio = 2.0;

        [InputParameter("Max Base Candles", 6, 1, 15, 1, 0)]
        public int MaxBaseCandles = 15;

        [InputParameter("Lookback Bars", 7, 20, 1000, 25, 0)]
        public int LookbackBars = 150;

        [InputParameter("Max Zones Per Side", 8, 1, 10, 1, 0)]
        public int MaxZones = 5;

        [InputParameter("Zone Proximity Ticks (show panel)", 9, 1, 150, 1, 0)]
        public int ZoneProximityTicks = 20;

        [InputParameter("Zone Fill Opacity (0-255)", 10, 5, 200, 5, 0)]
        public int ZoneOpacity = 40;

        [InputParameter("Show Zone Labels", 11)]
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
            public double Top;       // drawn top  (demand=BodyHi, supply=WickHi)
            public double Bottom;    // drawn bot  (demand=WickLo, supply=BodyLo)
            public double WickEdge;  // invalidation level (demand=WickLo, supply=WickHi)
            public ZType  Type;
            public bool   IsDemand;
            public int    Touches;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public IOF_Clean()
        {
            Name           = "IOF Clean";
            Description    = "Standalone supply/demand zones + contract sizer. Same detection as IOF v2.";
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

        // ── Zone scan — direct port of MultiTFZoneScanner.ScanTimeframe ───────

        private void ScanZones()
        {
            var data      = this.HistoricalData;
            int total     = data.Count;
            double tick   = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;

            int lookback  = Math.Min(LookbackBars, total - 4);
            int firstBar  = Math.Max(2, total - lookback);

            var candidates = new List<ZoneBox>();

            // Walk oldest → newest (same as v2).
            // endIndex = last candle of the base cluster.
            // The bar at endIndex+1 is the leg-out (impulse).
            // The bar at startIndex-1 is the leg-in.
            for (int endIndex = firstBar; endIndex < total - 2; endIndex++)
            {
                for (int baseLen = 1; baseLen <= MaxBaseCandles; baseLen++)
                {
                    int startIndex = endIndex - baseLen + 1;
                    if (startIndex < 1) continue;
                    if (endIndex + 1 >= total) continue;

                    // Every bar in [startIndex..endIndex] must be a base candle:
                    // body / range <= BaseCandleBodyPct
                    if (!IsValidBase(data, startIndex, endIndex)) continue;

                    // Leg-in: bar before the base must be a strong directional candle
                    LegDir legIn = ClassifyLeg(data, startIndex - 1);
                    if (legIn == LegDir.None) continue;

                    // Leg-out: bar after the base must also be a strong directional candle
                    LegDir legOut = ClassifyLeg(data, endIndex + 1);
                    if (legOut == LegDir.None) continue;

                    // Formation from in+out direction
                    ZType ztype;
                    bool  isDemand;
                    if      (legIn == LegDir.Up   && legOut == LegDir.Up)   { ztype = ZType.RBR; isDemand = true;  }
                    else if (legIn == LegDir.Down && legOut == LegDir.Up)   { ztype = ZType.DBR; isDemand = true;  }
                    else if (legIn == LegDir.Up   && legOut == LegDir.Down) { ztype = ZType.RBD; isDemand = false; }
                    else if (legIn == LegDir.Down && legOut == LegDir.Down) { ztype = ZType.DBD; isDemand = false; }
                    else continue;

                    // Build asymmetric zone rectangle (same as v2)
                    double bodyHi = double.MinValue, bodyLo = double.MaxValue;
                    double wickHi = double.MinValue, wickLo = double.MaxValue;
                    for (int i = startIndex; i <= endIndex; i++)
                    {
                        var b = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                        if (b == null) goto NextBaseLen;
                        double bh = Math.Max(b.Open, b.Close);
                        double bl = Math.Min(b.Open, b.Close);
                        if (bh > bodyHi) bodyHi = bh;
                        if (bl < bodyLo) bodyLo = bl;
                        if (b.High > wickHi) wickHi = b.High;
                        if (b.Low  < wickLo) wickLo = b.Low;
                    }

                    {
                        // Demand: top = BodyHi, bottom = WickLo
                        // Supply: top = WickHi, bottom = BodyLo
                        double zoneTop    = isDemand ? bodyHi : wickHi;
                        double zoneBot    = isDemand ? wickLo : bodyLo;
                        double wickEdge   = isDemand ? wickLo : wickHi;
                        double baseHeight = zoneTop - zoneBot;
                        if (baseHeight <= 0) goto NextBaseLen;

                        // Impulse extension from end of base outward must be >=
                        // MinImpulseRatio * baseHeight (matches v2 MeasureMoveOut)
                        double moveOut = MeasureMoveOut(data, endIndex, total, isDemand, bodyHi, bodyLo);
                        if (moveOut < MinImpulseRatio * baseHeight) goto NextBaseLen;

                        // Dedup: Jaccard overlap >= 75% with same-direction candidates
                        if (IsDuplicate(candidates, isDemand, zoneTop, zoneBot)) goto NextBaseLen;

                        // Count touches after the base
                        int touches = 0;
                        for (int j = endIndex + 2; j < total; j++)
                        {
                            var jb = data[j, SeekOriginHistory.Begin] as HistoryItemBar;
                            if (jb != null && jb.High >= zoneBot && jb.Low <= zoneTop)
                                touches++;
                        }

                        var z      = new ZoneBox();
                        z.Top      = zoneTop;
                        z.Bottom   = zoneBot;
                        z.WickEdge = wickEdge;
                        z.Type     = ztype;
                        z.IsDemand = isDemand;
                        z.Touches  = touches;
                        candidates.Add(z);
                    }

                    NextBaseLen:;
                }
            }

            // Invalidate zones where a later bar's close crossed the far wick
            for (int z = candidates.Count - 1; z >= 0; z--)
            {
                var zone = candidates[z];
                bool dead = false;
                // find the endIndex of this zone by matching geometry (approximate)
                // We invalidate by scanning all bars and checking close vs wickEdge
                for (int i = firstBar; i < total; i++)
                {
                    var b = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (b == null) continue;
                    if (zone.IsDemand && b.Close < zone.WickEdge) { dead = true; break; }
                    if (!zone.IsDemand && b.Close > zone.WickEdge) { dead = true; break; }
                }
                if (dead) candidates.RemoveAt(z);
            }

            // Keep N closest per side, sorted closest to current price first
            var curBar2 = data[total - 1, SeekOriginHistory.Begin] as HistoryItemBar;
            double curPrice = curBar2 != null ? curBar2.Close : 0;

            var demand = new List<ZoneBox>();
            var supply = new List<ZoneBox>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].IsDemand) demand.Add(candidates[i]);
                else supply.Add(candidates[i]);
            }

            // Demand: keep zones below price, sort highest top first (closest)
            var demandBelow = new List<ZoneBox>();
            for (int i = 0; i < demand.Count; i++)
                if (demand[i].Top < curPrice) demandBelow.Add(demand[i]);
            demandBelow.Sort((a, b2) => b2.Top.CompareTo(a.Top));

            // Supply: keep zones above price, sort lowest bottom first (closest)
            var supplyAbove = new List<ZoneBox>();
            for (int i = 0; i < supply.Count; i++)
                if (supply[i].Bottom > curPrice) supplyAbove.Add(supply[i]);
            supplyAbove.Sort((a, b2) => a.Bottom.CompareTo(b2.Bottom));

            var result = new List<ZoneBox>();
            for (int i = 0; i < Math.Min(MaxZones, demandBelow.Count); i++) result.Add(demandBelow[i]);
            for (int i = 0; i < Math.Min(MaxZones, supplyAbove.Count); i++) result.Add(supplyAbove[i]);

            lock (_zoneLock) { _zones.Clear(); _zones.AddRange(result); }
        }

        // ── Detection helpers (same logic as MultiTFZoneScanner) ─────────────

        private enum LegDir { None, Up, Down }

        private bool IsValidBase(HistoricalData data, int start, int end)
        {
            double tick = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double clusterHigh = double.MinValue;
            double clusterLow  = double.MaxValue;
            for (int i = start; i <= end; i++)
            {
                var bar = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) return false;
                if (bar.High > clusterHigh) clusterHigh = bar.High;
                if (bar.Low  < clusterLow)  clusterLow  = bar.Low;
            }
            if (clusterHigh == double.MinValue || clusterLow == double.MaxValue) return false;
            return (clusterHigh - clusterLow) <= ClusterMaxRangeTicks * tick;
        }

        private LegDir ClassifyLeg(HistoricalData data, int idx)
        {
            var bar = data[idx, SeekOriginHistory.Begin] as HistoryItemBar;
            if (bar == null) return LegDir.None;
            double range = bar.High - bar.Low;
            if (range <= 0) return LegDir.None;
            double body = Math.Abs(bar.Close - bar.Open);
            if (body / range < LegStrengthPct + 0.05) return LegDir.None;
            if (bar.Close > bar.Open) return LegDir.Up;
            if (bar.Close < bar.Open) return LegDir.Down;
            return LegDir.None;
        }

        private double MeasureMoveOut(HistoricalData data, int endOfBase, int total,
                                      bool isDemand, double bodyHi, double bodyLo)
        {
            int scanLimit = Math.Min(total - 1, endOfBase + Math.Max(20, LookbackBars / 4));
            var baseBar   = data[endOfBase, SeekOriginHistory.Begin] as HistoryItemBar;
            if (baseBar == null) return 0.0;
            double baseRange = baseBar.High - baseBar.Low;
            if (baseRange <= 0) baseRange = Math.Abs(baseBar.Close - baseBar.Open);

            double extreme = double.NaN;
            for (int i = endOfBase + 1; i <= scanLimit; i++)
            {
                var b = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (b == null) break;
                if (isDemand)
                {
                    if (double.IsNaN(extreme) || b.High > extreme) extreme = b.High;
                    if (b.Close < baseBar.Close - baseRange) break;
                }
                else
                {
                    if (double.IsNaN(extreme) || b.Low < extreme) extreme = b.Low;
                    if (b.Close > baseBar.Close + baseRange) break;
                }
            }
            if (double.IsNaN(extreme)) return 0.0;
            return isDemand ? (extreme - bodyHi) : (bodyLo - extreme);
        }

        private bool IsDuplicate(List<ZoneBox> existing, bool isDemand, double top, double bot)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                var z = existing[i];
                if (z.IsDemand != isDemand) continue;
                double overlap = Math.Min(z.Top, top) - Math.Max(z.Bottom, bot);
                if (overlap <= 0) continue;
                double union = Math.Max(z.Top, top) - Math.Min(z.Bottom, bot);
                if (union <= 0) continue;
                if (overlap / union >= 0.75) return true;
            }
            return false;
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

                // Edge line on the body side (entry edge)
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

            int    cts    = (int)Math.Floor(RiskPerTrade / riskPerCt);
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
                case "MNQ": return 2.0;    case "NQ":  return 20.0;
                case "MES": return 5.0;    case "ES":  return 50.0;
                case "M2K": return 5.0;    case "RTY": return 50.0;
                case "MYM": return 0.5;    case "YM":  return 5.0;
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
