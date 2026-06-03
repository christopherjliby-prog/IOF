// =============================================================================
// IOF_PositionSizer.cs — Zone detector + contract calculator
// =============================================================================
// Reads live IOF zones from IOFZoneRegistry (TradePhantoms_IOF_v2.dll).
// When price enters or approaches a zone, shows:
//   - Zone type + direction (LONG / SHORT)
//   - How many contracts to enter
//   - Exact stop price and tick distance
//   - Dollar risk at that size
//
// Requires TradePhantoms_IOF_v2 loaded on the same chart so zones are live.
// Run this ON TOP of IOF v2 — it reads the same registry, adds nothing heavy.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    public class IOF_PositionSizer : Indicator
    {
        // ── Parameters ────────────────────────────────────────────────────────

        [InputParameter("Risk Per Trade ($)", 0, 10.0, 5000.0, 10.0, 2)]
        public double RiskPerTrade = 200.0;

        [InputParameter("Max Contracts (account cap)", 1, 1, 20, 1, 0)]
        public int MaxContracts = 10;

        [InputParameter("Stop Buffer Ticks (past zone edge)", 2, 1, 30, 1, 0)]
        public int StopBufferTicks = 4;

        [InputParameter("Zone Proximity Ticks (show when within)", 3, 1, 100, 1, 0)]
        public int ZoneProximityTicks = 12;

        [InputParameter("Min Contracts to show signal", 4, 1, 10, 1, 0)]
        public int MinContracts = 1;

        // ── Internal ──────────────────────────────────────────────────────────

        private SizerRec _rec  = null;
        private readonly object _lock = new object();

        private Font _fontTitle;
        private Font _fontContracts;
        private Font _fontDetail;

        private class SizerRec
        {
            public string ZoneType;
            public double ZoneTop;
            public double ZoneBottom;
            public string Direction;
            public double Entry;
            public double Stop;
            public double StopTicks;
            public double RiskPerContract;
            public int    Contracts;
            public double TotalRisk;
            public bool   InsideZone;
            public double ZoneScore;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public IOF_PositionSizer()
        {
            Name           = "IOF Position Sizer";
            Description    = "Zone detector + contract calculator. Reads live IOF v2 zones.";
            SeparateWindow = false;
            AddLineSeries("Sizer_anchor", Color.Transparent, 1, LineStyle.Solid);
        }

        protected override void OnInit()
        {
            _fontTitle     = new Font("Consolas", 9f,  FontStyle.Bold);
            _fontContracts = new Font("Consolas", 22f, FontStyle.Bold);
            _fontDetail    = new Font("Consolas", 9f,  FontStyle.Regular);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (this.Symbol == null) return;

            double price     = this.GetPrice(PriceType.Close, 0);
            double tickSize  = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double proximity = ZoneProximityTicks * tickSize;
            double pointVal  = ResolvePointValue();

            string period = this.HistoricalData?.Aggregation?.ToString() ?? "5";
            string regKey = this.Symbol.Name + "_" + period;

            // Find nearest IOF zone
            ZoneData zone = GetNearestZone(regKey, price);
            if (zone == null)
            {
                lock (_lock) { _rec = null; }
                return;
            }

            // Only activate when price is close enough
            double distToZone = price < zone.Bottom ? zone.Bottom - price
                              : price > zone.Top    ? price - zone.Top
                              : 0.0;

            if (distToZone > proximity)
            {
                lock (_lock) { _rec = null; }
                return;
            }

            // Direction: DBR/RBR = demand → LONG; DBD/RBD = supply → SHORT
            bool isLong     = zone.Type == "DBR" || zone.Type == "RBR";
            double stopBuf  = StopBufferTicks * tickSize;
            double stopPrice = isLong ? zone.Bottom - stopBuf : zone.Top + stopBuf;

            double stopDist      = Math.Abs(price - stopPrice);
            double stopTicks     = stopDist / tickSize;
            double riskPerCt     = stopDist * pointVal;

            if (riskPerCt <= 0)
            {
                lock (_lock) { _rec = null; }
                return;
            }

            int contracts = (int)Math.Floor(RiskPerTrade / riskPerCt);
            contracts = Math.Max(0, Math.Min(contracts, MaxContracts));

            if (contracts < MinContracts)
            {
                lock (_lock) { _rec = null; }
                return;
            }

            var rec = new SizerRec();
            rec.ZoneType       = zone.Type;
            rec.ZoneTop        = zone.Top;
            rec.ZoneBottom     = zone.Bottom;
            rec.Direction      = isLong ? "LONG" : "SHORT";
            rec.Entry          = price;
            rec.Stop           = stopPrice;
            rec.StopTicks      = stopTicks;
            rec.RiskPerContract = riskPerCt;
            rec.Contracts      = contracts;
            rec.TotalRisk      = contracts * riskPerCt;
            rec.InsideZone     = (price >= zone.Bottom && price <= zone.Top);
            rec.ZoneScore      = zone.Score;

            lock (_lock) { _rec = rec; }
        }

        // ── Paint ─────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            SizerRec rec;
            lock (_lock) { rec = _rec; }
            if (rec == null || this.CurrentChart == null) return;

            var gr  = args.Graphics;
            var win = this.CurrentChart.MainWindow;
            if (win == null) return;

            var rect = (Rectangle)win.ClientRectangle;

            bool isLong   = rec.Direction == "LONG";
            Color accent  = isLong ? Color.FromArgb(255, 0, 210, 100) : Color.FromArgb(255, 255, 60, 60);
            Color panelBg = isLong ? Color.FromArgb(220, 0, 28, 10)   : Color.FromArgb(220, 35, 0, 0);
            Color dimText = Color.FromArgb(180, 180, 180);

            // ── Panel ─────────────────────────────────────────────────────────
            int pw = 270;
            int ph = 148;
            int px = rect.Right - pw - 12;
            int py = 12;

            using (var bg = new SolidBrush(panelBg))
                gr.FillRectangle(bg, px, py, pw, ph);

            using (var border = new Pen(accent, 2))
                gr.DrawRectangle(border, px, py, pw, ph);

            // Top accent bar
            using (var bar = new SolidBrush(Color.FromArgb(80, accent.R, accent.G, accent.B)))
                gr.FillRectangle(bar, px + 2, py + 2, pw - 4, 20);

            // Header
            string status = rec.InsideZone ? "INSIDE ZONE" : "APPROACHING";
            string header = status + "  ·  " + rec.ZoneType + "  ·  " + rec.Direction;
            using (var tb = new SolidBrush(accent))
                gr.DrawString(header, _fontTitle, tb, px + 8, py + 5);

            // Big contract count
            string ctLabel = rec.Contracts + " contracts";
            using (var cb = new SolidBrush(Color.White))
                gr.DrawString(ctLabel, _fontContracts, cb, px + 8, py + 26);

            // Divider
            using (var divPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1))
                gr.DrawLine(divPen, px + 8, py + 74, px + pw - 8, py + 74);

            // Details
            string[] details = new string[]
            {
                "Entry  " + rec.Entry.ToString("F2"),
                "Stop   " + rec.Stop.ToString("F2") + "  (" + ((int)Math.Round(rec.StopTicks)).ToString() + " ticks)",
                "Risk   $" + ((int)Math.Round(rec.TotalRisk)).ToString() + "  ($" + ((int)Math.Round(rec.RiskPerContract)).ToString() + " / ct)",
                "Zone   " + rec.ZoneBottom.ToString("F2") + " – " + rec.ZoneTop.ToString("F2"),
            };

            using (var db = new SolidBrush(dimText))
            {
                for (int i = 0; i < details.Length; i++)
                    gr.DrawString(details[i], _fontDetail, db, px + 8, py + 79 + i * 16);
            }

            // ── Stop line on chart ────────────────────────────────────────────
            try
            {
                int yStop = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(rec.Stop));
                if (yStop >= rect.Top && yStop <= rect.Bottom)
                {
                    using (var stopPen = new Pen(Color.FromArgb(200, 255, 80, 80), 1))
                    {
                        stopPen.DashStyle = DashStyle.Dash;
                        gr.DrawLine(stopPen, rect.Left, yStop, rect.Right, yStop);
                    }
                    using (var sb2 = new SolidBrush(Color.FromArgb(200, 255, 80, 80)))
                        gr.DrawString("STOP  " + rec.Stop.ToString("F2"), _fontDetail, sb2, rect.Left + 4, yStop - 14);
                }
            }
            catch { }

            // ── Zone box (light fill so it doesn't fight IOF v2 drawing) ─────
            try
            {
                int yTop = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(rec.ZoneTop));
                int yBot = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(rec.ZoneBottom));
                if (yTop > yBot) { int tmp = yTop; yTop = yBot; yBot = tmp; }
                int zoneH = Math.Max(1, yBot - yTop);

                if (yTop <= rect.Bottom && yBot >= rect.Top)
                {
                    using (var zoneFill = new SolidBrush(Color.FromArgb(18, accent.R, accent.G, accent.B)))
                        gr.FillRectangle(zoneFill, rect.Left, yTop, rect.Width, zoneH);
                }
            }
            catch { }
        }

        // ── Zone registry lookup ──────────────────────────────────────────────

        private class ZoneData
        {
            public double Top;
            public double Bottom;
            public string Type;
            public double Score;
        }

        private ZoneData GetNearestZone(string regKey, double price)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TradePhantoms_IOF_v2") continue;

                    var regType = asm.GetType("TradePhantoms.IOFZoneRegistry");
                    if (regType == null) continue;

                    var getZones = regType.GetMethod("GetZones",
                        BindingFlags.Public | BindingFlags.Static);
                    if (getZones == null) continue;

                    var raw = getZones.Invoke(null, new object[] { regKey })
                              as System.Collections.IEnumerable;
                    if (raw == null) return null;

                    double bestDist = double.MaxValue;
                    ZoneData best  = null;

                    foreach (var z in raw)
                    {
                        double top    = GetProp<double>(z, "Top");
                        double bottom = GetProp<double>(z, "Bottom");
                        double mid    = (top + bottom) / 2.0;
                        double dist   = Math.Abs(price - mid);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best     = new ZoneData();
                            best.Top    = top;
                            best.Bottom = bottom;
                            object zt   = GetProp<object>(z, "Type");
                            best.Type   = zt != null ? zt.ToString() : "";
                            best.Score  = GetProp<double>(z, "Score");
                        }
                    }

                    return best;
                }
            }
            catch { }
            return null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static T GetProp<T>(object obj, string name)
        {
            try
            {
                var prop = obj.GetType().GetProperty(name);
                if (prop != null)
                {
                    object v = prop.GetValue(obj);
                    if (v is T t) return t;
                    return (T)Convert.ChangeType(v, typeof(T));
                }
                var field = obj.GetType().GetField(name);
                if (field != null)
                {
                    object v = field.GetValue(obj);
                    if (v is T t2) return t2;
                    return (T)Convert.ChangeType(v, typeof(T));
                }
            }
            catch { }
            return default(T);
        }

        private double ResolvePointValue()
        {
            if (this.Symbol == null) return 2.0;
            double tickSize = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            foreach (var propName in new string[] { "TickCost", "TickValue", "PointValue", "ContractMultiplier" })
            {
                try
                {
                    var prop = this.Symbol.GetType().GetProperty(propName);
                    if (prop == null) continue;
                    double num = Convert.ToDouble(prop.GetValue(this.Symbol));
                    if (num <= 0) continue;
                    return (propName == "TickCost" || propName == "TickValue") ? num / tickSize : num;
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
            lock (_lock) { _rec = null; }
        }

        public override void Dispose()
        {
            _fontTitle?.Dispose();
            _fontContracts?.Dispose();
            _fontDetail?.Dispose();
            base.Dispose();
        }
    }
}
