// =============================================================================
// FiveMinMoneyMaker.cs — HTF/ITF zone retest detector + position sizer
// =============================================================================
// Standalone. No IOF v2 dependency. Works on any chart timeframe.
//
// Zones: fetches two configurable higher timeframes (ITF + HTF), scans for
//        supply/demand zones using the exact IOF v2 algorithm, draws them
//        from their formation point rightward only.
//
// Retest: on each new bar, scans for tight base candle cluster at zone edge
//         followed by strong departure candle in the zone direction.
//         Box fires on departure close. Invalidated on candle close through
//         the zone's wick edge.
//
// Panel: bottom-left by default, position + transparency configurable.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    public class FiveMinMoneyMaker : Indicator
    {
        // ── Sizing ────────────────────────────────────────────────────────────

        [InputParameter("Risk Per Trade ($)", 0, 10.0, 1000.0, 10.0, 2)]
        public double RiskPerTrade = 250.0;

        [InputParameter("Max Contracts", 1, 1, 20, 1, 0)]
        public int MaxContracts = 10;

        [InputParameter("Stop Buffer Ticks (below zone edge)", 2, 1, 30, 1, 0)]
        public int StopBufferTicks = 3;

        [InputParameter("TP Points", 3, 5.0, 500.0, 5.0, 1)]
        public double TpPoints = 100.0;

        // ── Timeframes ────────────────────────────────────────────────────────
        // 0=1m  1=5m  2=15m  3=30m  4=1h  5=4h  6=Daily  7=Weekly

        [InputParameter("ITF Period  (0=1m 1=5m 2=15m 3=30m 4=1h 5=4h 6=D 7=W)", 10, 0, 7, 1, 0)]
        public int ITFPeriodIndex = 4;   // default 1h

        [InputParameter("HTF Period  (0=1m 1=5m 2=15m 3=30m 4=1h 5=4h 6=D 7=W)", 11, 0, 7, 1, 0)]
        public int HTFPeriodIndex = 5;   // default 4h

        // ── Zone detection ────────────────────────────────────────────────────

        [InputParameter("Base Candle Body % Max (0.1-0.9)", 20, 0.1, 0.9, 0.05, 2)]
        public double BaseCandleBodyPct = 0.5;

        [InputParameter("Max Base Candles (retest cluster)", 21, 1, 7, 1, 0)]
        public int MaxBaseCandles = 3;

        [InputParameter("Min Impulse Ratio (zone detection)", 22, 0.5, 10.0, 0.5, 1)]
        public double MinImpulseRatio = 2.0;

        [InputParameter("Zone Lookback Bars (HTF/ITF)", 23, 20, 500, 10, 0)]
        public int ZoneLookback = 100;

        [InputParameter("Zone Proximity Ticks (retest trigger)", 24, 1, 100, 1, 0)]
        public int ZoneProximityTicks = 20;

        [InputParameter("Zone Fill Opacity (0-255)", 25, 0, 255, 5, 0)]
        public int ZoneOpacity = 28;

        // ── Display ───────────────────────────────────────────────────────────
        // Dashboard position: 0=Bottom-Left  1=Bottom-Right  2=Top-Left  3=Top-Right

        [InputParameter("Dashboard Position (0=BL 1=BR 2=TL 3=TR)", 30, 0, 3, 1, 0)]
        public int DashboardPosition = 0;

        [InputParameter("Dashboard Opacity (0-255)", 31, 0, 255, 5, 0)]
        public int DashboardOpacity = 185;

        [InputParameter("Show Dashboard", 32)]
        public bool ShowDebug = true;

        [InputParameter("Extend Box Bars Right", 33, 5, 200, 5, 0)]
        public int ExtendBarsRight = 40;

        // ── Internal types ────────────────────────────────────────────────────

        private enum ZType  { RBR, DBR, RBD, DBD }
        private enum LegDir { None, Up, Down }

        private class ZoneBox
        {
            public double   Top, Bottom, WickEdge;
            public ZType    Type;
            public bool     IsDemand;
            public string   Tier;
            public DateTime FormationTime;  // start of base cluster — zones draw from here rightward
        }

        private class RetestBox
        {
            public DateTime BaseStartTime;
            public DateTime DepartureTime;
            public double   BoxHigh, BoxLow;
            public bool     IsDemand;
            public ZoneBox  Zone;
            public bool     Invalidated;
            public double   EntryPrice;
            public double   StopPrice;
            public double   TpPrice;
            public int      Contracts;
            public double   TotalRisk;
            public double   RR;
        }

        // ── Fields ────────────────────────────────────────────────────────────

        private List<ZoneBox>   _zones       = new List<ZoneBox>();
        private List<RetestBox> _retestBoxes = new List<RetestBox>();
        private readonly object _lock        = new object();

        private HistoricalData _itfData;
        private HistoricalData _htfData;
        private int _lastItfCount  = 0;
        private int _lastHtfCount  = 0;
        private int _barCounter    = 0;
        private int _prevItfIndex  = -1;
        private int _prevHtfIndex  = -1;

        private Font _fontTitle;
        private Font _fontDetail;
        private Font _fontBig;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public FiveMinMoneyMaker()
        {
            Name           = "5m Money Maker";
            Description    = "HTF/ITF zone retest with position sizing. Works on any timeframe.";
            SeparateWindow = false;
            AddLineSeries("MM_anchor", Color.Transparent, 1, LineStyle.Solid);
        }

        protected override void OnInit()
        {
            _fontTitle  = new Font("Consolas", 8f, FontStyle.Bold);
            _fontDetail = new Font("Consolas", 7f, FontStyle.Regular);
            _fontBig    = new Font("Consolas", 13f, FontStyle.Bold);

            FetchHistoricalData();

            lock (_lock) { _zones.Clear(); _retestBoxes.Clear(); }
            _barCounter = 0;
        }

        private void FetchHistoricalData()
        {
            if (this.Symbol == null) return;
            DateTime from = DateTime.UtcNow.AddDays(-60);
            try { _itfData = this.Symbol.GetHistory(IndexToPeriod(ITFPeriodIndex), this.Symbol.HistoryType, from); } catch { }
            try { _htfData = this.Symbol.GetHistory(IndexToPeriod(HTFPeriodIndex), this.Symbol.HistoryType, from); } catch { }
            _prevItfIndex = ITFPeriodIndex;
            _prevHtfIndex = HTFPeriodIndex;
            _lastItfCount = 0;
            _lastHtfCount = 0;
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (this.Symbol == null) return;
            if (args.Reason != UpdateReason.NewBar && args.Reason != UpdateReason.HistoricalBar) return;
            if (this.HistoricalData == null || this.HistoricalData.Count < 10) return;

            // Re-fetch if user changed the period settings
            if (ITFPeriodIndex != _prevItfIndex || HTFPeriodIndex != _prevHtfIndex)
            {
                lock (_lock) { _zones.Clear(); _retestBoxes.Clear(); }
                FetchHistoricalData();
                return;
            }

            _barCounter++;

            bool itfNew = _itfData != null && _itfData.Count != _lastItfCount;
            bool htfNew = _htfData != null && _htfData.Count != _lastHtfCount;
            if (itfNew || htfNew || _barCounter % 20 == 0)
            {
                ScanHTFZones();
                if (_itfData != null) _lastItfCount = _itfData.Count;
                if (_htfData != null) _lastHtfCount = _htfData.Count;
            }

            ScanRetestPatterns();
            CheckInvalidations();
        }

        // ── Zone scan (port of MultiTFZoneScanner.ScanTimeframe) ─────────────

        private void ScanHTFZones()
        {
            var all = new List<ZoneBox>();
            var itf = ScanZonesFromData(_itfData);
            var htf = ScanZonesFromData(_htfData);
            for (int i = 0; i < itf.Count; i++) { itf[i].Tier = "ITF"; all.Add(itf[i]); }
            for (int i = 0; i < htf.Count; i++) { htf[i].Tier = "HTF"; all.Add(htf[i]); }
            lock (_lock) { _zones = all; }
        }

        private List<ZoneBox> ScanZonesFromData(HistoricalData data)
        {
            var result = new List<ZoneBox>();
            if (data == null || data.Count < 4) return result;

            int total    = data.Count;
            int firstBar = Math.Max(2, total - ZoneLookback);

            for (int endIndex = firstBar; endIndex < total - 2; endIndex++)
            {
                for (int baseLen = 1; baseLen <= 3; baseLen++)
                {
                    int startIndex = endIndex - baseLen + 1;
                    if (startIndex < 1) continue;
                    if (endIndex + 1 >= total) continue;

                    if (!IsValidBase(data, startIndex, endIndex)) continue;

                    LegDir legIn  = ClassifyLeg(data, startIndex - 1);
                    if (legIn == LegDir.None) continue;
                    LegDir legOut = ClassifyLeg(data, endIndex + 1);
                    if (legOut == LegDir.None) continue;

                    ZType ztype;
                    bool  isDemand;
                    if      (legIn == LegDir.Up   && legOut == LegDir.Up)   { ztype = ZType.RBR; isDemand = true;  }
                    else if (legIn == LegDir.Down && legOut == LegDir.Up)   { ztype = ZType.DBR; isDemand = true;  }
                    else if (legIn == LegDir.Up   && legOut == LegDir.Down) { ztype = ZType.RBD; isDemand = false; }
                    else if (legIn == LegDir.Down && legOut == LegDir.Down) { ztype = ZType.DBD; isDemand = false; }
                    else continue;

                    double bodyHi = double.MinValue, bodyLo = double.MaxValue;
                    double wickHi = double.MinValue, wickLo = double.MaxValue;
                    bool   baseOk = true;
                    DateTime formationTime = DateTime.MinValue;

                    for (int i = startIndex; i <= endIndex; i++)
                    {
                        var b = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                        if (b == null) { baseOk = false; break; }
                        double bh = Math.Max(b.Open, b.Close);
                        double bl = Math.Min(b.Open, b.Close);
                        if (bh > bodyHi) bodyHi = bh;
                        if (bl < bodyLo) bodyLo = bl;
                        if (b.High > wickHi) wickHi = b.High;
                        if (b.Low  < wickLo) wickLo = b.Low;
                        if (i == startIndex) formationTime = b.TimeLeft;
                    }
                    if (!baseOk) continue;

                    double zoneTop  = isDemand ? bodyHi : wickHi;
                    double zoneBot  = isDemand ? wickLo : bodyLo;
                    double wickEdge = isDemand ? wickLo : wickHi;
                    double baseH    = zoneTop - zoneBot;
                    if (baseH <= 0) continue;

                    double moveOut = MeasureMoveOut(data, endIndex, total, isDemand, bodyHi, bodyLo);
                    if (moveOut < MinImpulseRatio * baseH) continue;

                    if (IsDuplicate(result, isDemand, zoneTop, zoneBot)) continue;

                    bool dead = false;
                    for (int j = endIndex + 2; j < total; j++)
                    {
                        var jb = data[j, SeekOriginHistory.Begin] as HistoryItemBar;
                        if (jb == null) continue;
                        if (isDemand  && jb.Close < wickEdge) { dead = true; break; }
                        if (!isDemand && jb.Close > wickEdge) { dead = true; break; }
                    }
                    if (dead) continue;

                    var z           = new ZoneBox();
                    z.Top           = zoneTop;
                    z.Bottom        = zoneBot;
                    z.WickEdge      = wickEdge;
                    z.Type          = ztype;
                    z.IsDemand      = isDemand;
                    z.FormationTime = formationTime;
                    result.Add(z);
                }
            }

            return result;
        }

        // ── Retest pattern scan ───────────────────────────────────────────────

        private void ScanRetestPatterns()
        {
            List<ZoneBox> zones;
            lock (_lock) { zones = new List<ZoneBox>(_zones); }
            if (zones.Count == 0) return;

            var    data  = this.HistoricalData;
            int    total = data.Count;
            double tick  = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double prox  = ZoneProximityTicks * tick;
            double ptVal = ResolvePointValue();

            var curBar = data[total - 1, SeekOriginHistory.Begin] as HistoryItemBar;
            if (curBar == null) return;

            int scanBack = Math.Min(60, total - 3);

            for (int zi = 0; zi < zones.Count; zi++)
            {
                var zone = zones[zi];

                for (int d = total - 2; d >= total - 1 - scanBack; d--)
                {
                    if (d < 2) break;

                    var depBar = data[d, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (depBar == null) continue;

                    LegDir depDir = ClassifyLegFromBar(depBar);
                    if (depDir == LegDir.None) continue;
                    if (zone.IsDemand  && depDir != LegDir.Up)   continue;
                    if (!zone.IsDemand && depDir != LegDir.Down) continue;

                    double depDist = zone.IsDemand
                        ? Math.Max(0, depBar.Low  - zone.Bottom)
                        : Math.Max(0, zone.Top    - depBar.High);
                    if (depDist > prox * 3) continue;

                    for (int baseLen = 1; baseLen <= MaxBaseCandles; baseLen++)
                    {
                        int baseEnd   = d - 1;
                        int baseStart = baseEnd - baseLen + 1;
                        if (baseStart < 1) continue;

                        if (!IsValidBase(data, baseStart, baseEnd)) continue;

                        double bHigh = double.MinValue, bLow = double.MaxValue;
                        bool   ok    = true;
                        for (int i = baseStart; i <= baseEnd; i++)
                        {
                            var b = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                            if (b == null) { ok = false; break; }
                            if (b.High > bHigh) bHigh = b.High;
                            if (b.Low  < bLow)  bLow  = b.Low;
                        }
                        if (!ok) continue;

                        double baseProx = zone.IsDemand
                            ? Math.Max(0, bLow  - zone.Bottom)
                            : Math.Max(0, zone.Top - bHigh);
                        if (baseProx > prox * 3) continue;

                        var startBar = data[baseStart, SeekOriginHistory.Begin] as HistoryItemBar;
                        if (startBar == null) continue;

                        bool exists = false;
                        lock (_lock)
                        {
                            for (int ri = 0; ri < _retestBoxes.Count; ri++)
                            {
                                var rb = _retestBoxes[ri];
                                if (Math.Abs((rb.BaseStartTime - startBar.TimeLeft).TotalMinutes) < 2
                                    && rb.IsDemand == zone.IsDemand)
                                { exists = true; break; }
                            }
                        }
                        if (exists) break;

                        double entry     = depBar.Close;
                        double buf       = StopBufferTicks * tick;
                        // Stop anchored to the BASE CLUSTER edge, not the full zone height.
                        // The zone tells you WHERE to look; the cluster defines your risk.
                        double stopPrice = zone.IsDemand ? bLow - buf : bHigh + buf;
                        double tpPrice   = zone.IsDemand ? entry + TpPoints  : entry - TpPoints;
                        double stopDist  = Math.Abs(entry - stopPrice);
                        double riskPerCt = stopDist * ptVal;
                        if (riskPerCt <= 0) break;

                        int    cts       = (int)Math.Floor(RiskPerTrade / riskPerCt);
                        cts = Math.Max(1, Math.Min(cts, MaxContracts));
                        double totalRisk = cts * riskPerCt;
                        double rr        = stopDist > 0 ? Math.Abs(tpPrice - entry) / stopDist : 0;

                        var newBox           = new RetestBox();
                        newBox.BaseStartTime = startBar.TimeLeft;
                        newBox.DepartureTime = depBar.TimeLeft;
                        newBox.BoxHigh       = bHigh;
                        newBox.BoxLow        = bLow;
                        newBox.IsDemand      = zone.IsDemand;
                        newBox.Zone          = zone;
                        newBox.Invalidated   = false;
                        newBox.EntryPrice    = entry;
                        newBox.StopPrice     = stopPrice;
                        newBox.TpPrice       = tpPrice;
                        newBox.Contracts     = cts;
                        newBox.TotalRisk     = totalRisk;
                        newBox.RR            = rr;

                        lock (_lock) { _retestBoxes.Add(newBox); }
                        break;
                    }
                }
            }
        }

        private void CheckInvalidations()
        {
            var data  = this.HistoricalData;
            int total = data.Count;

            List<RetestBox> boxes;
            lock (_lock) { boxes = new List<RetestBox>(_retestBoxes); }

            for (int bi = 0; bi < boxes.Count; bi++)
            {
                var rb = boxes[bi];
                if (rb.Invalidated) continue;
                for (int i = total - 1; i >= 0; i--)
                {
                    var b = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (b == null) continue;
                    if (b.TimeLeft <= rb.DepartureTime) break;
                    bool broken = rb.IsDemand
                        ? b.Close < rb.Zone.WickEdge
                        : b.Close > rb.Zone.WickEdge;
                    if (broken) { rb.Invalidated = true; break; }
                }
            }

            lock (_lock) { _retestBoxes.RemoveAll(rb => rb.Invalidated); }
        }

        // ── Paint ─────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null || this.CurrentChart == null) return;
            if (this.HistoricalData == null || this.HistoricalData.Count < 2) return;

            var gr  = args.Graphics;
            var win = this.CurrentChart.MainWindow;
            if (win == null) return;

            var clip = (Rectangle)win.ClientRectangle;

            // Bar width in pixels for right-box extension
            int barWidthPx = 6;
            try
            {
                int total = this.HistoricalData.Count;
                if (total >= 2)
                {
                    var b1 = this.HistoricalData[total - 2, SeekOriginHistory.Begin] as HistoryItemBar;
                    var b2 = this.HistoricalData[total - 1, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (b1 != null && b2 != null)
                    {
                        int x1 = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(b1.TimeLeft));
                        int x2 = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(b2.TimeLeft));
                        barWidthPx = Math.Max(2, Math.Abs(x2 - x1));
                    }
                }
            }
            catch { }

            DrawZones(gr, win, clip);

            List<RetestBox> boxes;
            lock (_lock) { boxes = new List<RetestBox>(_retestBoxes); }
            for (int i = 0; i < boxes.Count; i++)
                if (!boxes[i].Invalidated)
                    DrawRetestBox(gr, win, clip, boxes[i], barWidthPx);

            if (ShowDebug) DrawDebugPanel(gr, clip);
        }

        private void DrawZones(Graphics gr, dynamic win, Rectangle clip)
        {
            List<ZoneBox> zones;
            lock (_lock) { zones = new List<ZoneBox>(_zones); }

            for (int zi = 0; zi < zones.Count; zi++)
            {
                var z = zones[zi];
                int yTop, yBot, xFrom;
                try
                {
                    yTop  = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(z.Top));
                    yBot  = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(z.Bottom));
                    xFrom = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(z.FormationTime));
                }
                catch { continue; }

                if (yTop > yBot) { int t = yTop; yTop = yBot; yBot = t; }
                if (yBot < clip.Top || yTop > clip.Bottom) continue;

                // Zone extends from its formation point rightward only
                int drawLeft  = Math.Max(clip.Left, xFrom);
                int drawWidth = clip.Right - drawLeft;
                if (drawWidth <= 0) continue;

                int dTop = Math.Max(yTop, clip.Top);
                int dBot = Math.Min(yBot, clip.Bottom);
                int dH   = Math.Max(1, dBot - dTop);

                bool isHtf  = z.Tier == "HTF";
                int  opacity = Math.Max(1, ZoneOpacity) * (isHtf ? 1 : 0) + ZoneOpacity;
                // HTF slightly more opaque than ITF
                int fillA   = isHtf ? Math.Min(255, ZoneOpacity + 8) : ZoneOpacity;

                Color fill = z.IsDemand
                    ? Color.FromArgb(fillA, 0, 200, 90)
                    : Color.FromArgb(fillA, 255, 55, 55);
                Color edge = z.IsDemand
                    ? Color.FromArgb(isHtf ? 160 : 100, 0, 230, 100)
                    : Color.FromArgb(isHtf ? 160 : 100, 255, 80, 80);

                using (var fb = new SolidBrush(fill))
                    gr.FillRectangle(fb, drawLeft, dTop, drawWidth, dH);

                // Edge line on the body side (the entry edge)
                int edgeY = z.IsDemand ? yTop : yBot;
                using (var ep = new Pen(edge, isHtf ? 2f : 1f))
                    gr.DrawLine(ep, drawLeft, edgeY, clip.Right, edgeY);

                // Label at formation point
                string lbl = (isHtf ? "HTF " : "ITF ") + z.Type.ToString();
                using (var lb = new SolidBrush(Color.FromArgb(160, edge)))
                    gr.DrawString(lbl, _fontTitle, lb, drawLeft + 4,
                        z.IsDemand ? yTop - 13 : yBot + 3);
            }
        }

        private void DrawRetestBox(Graphics gr, dynamic win, Rectangle clip,
                                   RetestBox rb, int barWidthPx)
        {
            int xBase, xDep, yHigh, yLow;
            try
            {
                xBase  = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(rb.BaseStartTime));
                xDep   = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(rb.DepartureTime));
                yHigh  = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(rb.BoxHigh));
                yLow   = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(rb.BoxLow));
            }
            catch { return; }

            if (yHigh > yLow) { int t = yHigh; yHigh = yLow; yLow = t; }

            int rightExtPx = Math.Max(200, ExtendBarsRight * barWidthPx);
            int xRight     = xDep + rightExtPx;
            int boxH       = Math.Max(5, yLow - yHigh);

            Color accent   = rb.IsDemand
                ? Color.FromArgb(255, 0, 215, 100)
                : Color.FromArgb(255, 255, 65, 65);
            Color baseFill = rb.IsDemand
                ? Color.FromArgb(55, 0, 180, 80)
                : Color.FromArgb(55, 220, 50, 50);

            // Left box: base candle cluster
            if (xDep > xBase)
            {
                using (var fb = new SolidBrush(baseFill))
                    gr.FillRectangle(fb, xBase, yHigh, xDep - xBase, boxH);
                using (var ep = new Pen(accent, 1.5f))
                    gr.DrawRectangle(ep, xBase, yHigh, xDep - xBase, boxH);
            }

            // Right extended box: sizing panel
            int panelH = Math.Max(110, boxH);
            int panelY = rb.IsDemand ? yLow - panelH : yHigh;

            using (var bg = new SolidBrush(Color.FromArgb(230, 10, 10, 18)))
                gr.FillRectangle(bg, xDep, panelY, rightExtPx, panelH);
            using (var brd = new Pen(accent, 1.5f))
                gr.DrawRectangle(brd, xDep, panelY, rightExtPx, panelH);

            // Header
            using (var hb = new SolidBrush(Color.FromArgb(75, accent.R, accent.G, accent.B)))
                gr.FillRectangle(hb, xDep + 1, panelY + 1, rightExtPx - 2, 15);

            string header = (rb.IsDemand ? "LONG" : "SHORT") + "  "
                + rb.Zone.Tier + " " + rb.Zone.Type.ToString();
            using (var tb = new SolidBrush(accent))
                gr.DrawString(header, _fontTitle, tb, xDep + 5, panelY + 3);

            // Contract count
            using (var cb = new SolidBrush(Color.White))
                gr.DrawString(rb.Contracts + " contracts", _fontBig, cb, xDep + 5, panelY + 18);

            // Divider
            using (var dv = new Pen(Color.FromArgb(40, 255, 255, 255), 1))
                gr.DrawLine(dv, xDep + 5, panelY + 52, xDep + rightExtPx - 5, panelY + 52);

            // Detail lines
            double stopTicks = Math.Abs(rb.EntryPrice - rb.StopPrice)
                / (this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25);
            string[] lines =
            {
                "Entry   " + rb.EntryPrice.ToString("F2"),
                "Stop    " + rb.StopPrice.ToString("F2")
                           + "  (" + ((int)Math.Round(stopTicks)) + " ticks)",
                "TP      " + rb.TpPrice.ToString("F2"),
                "Risk    $" + ((int)Math.Round(rb.TotalRisk)),
                "RR      1:" + rb.RR.ToString("F1"),
            };
            using (var dt = new SolidBrush(Color.FromArgb(190, 190, 190)))
                for (int i = 0; i < lines.Length; i++)
                    gr.DrawString(lines[i], _fontDetail, dt, xDep + 5, panelY + 57 + i * 13);

            // TP line
            try
            {
                int yTp = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(rb.TpPrice));
                if (yTp >= clip.Top && yTp <= clip.Bottom)
                {
                    using (var tp = new Pen(Color.FromArgb(200, 0, 220, 100), 1f))
                    { tp.DashStyle = DashStyle.Dash; gr.DrawLine(tp, xDep, yTp, xRight + 60, yTp); }
                    using (var tb = new SolidBrush(Color.FromArgb(200, 0, 220, 100)))
                        gr.DrawString("TP  " + rb.TpPrice.ToString("F2"), _fontDetail, tb, xRight + 3, yTp - 10);
                }
            }
            catch { }

            // Stop line
            try
            {
                int ySl = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(rb.StopPrice));
                if (ySl >= clip.Top && ySl <= clip.Bottom)
                {
                    using (var sp = new Pen(Color.FromArgb(200, 255, 60, 60), 1f))
                    { sp.DashStyle = DashStyle.Dash; gr.DrawLine(sp, xBase, ySl, xRight + 60, ySl); }
                    using (var sb = new SolidBrush(Color.FromArgb(200, 255, 60, 60)))
                        gr.DrawString("SL  " + rb.StopPrice.ToString("F2"), _fontDetail, sb, xRight + 3, ySl + 2);
                }
            }
            catch { }
        }

        // ── Debug panel ───────────────────────────────────────────────────────

        private void DrawDebugPanel(Graphics gr, Rectangle clip)
        {
            List<ZoneBox>   zones;
            List<RetestBox> boxes;
            lock (_lock)
            {
                zones = new List<ZoneBox>(_zones);
                boxes = new List<RetestBox>(_retestBoxes);
            }

            int itfCount = 0, htfCount = 0, demandCount = 0, supplyCount = 0;
            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i].Tier == "ITF") itfCount++; else htfCount++;
                if (zones[i].IsDemand) demandCount++; else supplyCount++;
            }
            int active = 0;
            for (int i = 0; i < boxes.Count; i++)
                if (!boxes[i].Invalidated) active++;

            string itfStatus = _itfData != null
                ? (_itfData.Count > 0 ? _itfData.Count + " bars" : "loading...") : "no data";
            string htfStatus = _htfData != null
                ? (_htfData.Count > 0 ? _htfData.Count + " bars" : "loading...") : "no data";

            string itfLabel = PeriodLabel(ITFPeriodIndex);
            string htfLabel = PeriodLabel(HTFPeriodIndex);

            string[] lines =
            {
                "5m Money Maker",
                "ITF (" + itfLabel + "): " + itfStatus + "  zones: " + itfCount,
                "HTF (" + htfLabel + "): " + htfStatus + "  zones: " + htfCount,
                "Demand: " + demandCount + "   Supply: " + supplyCount,
                "Active setups: " + active,
            };

            int pw = 240, ph = 84;
            int margin = 10;
            int px, py;

            switch (DashboardPosition)
            {
                case 1:  px = clip.Right  - pw - margin; py = clip.Bottom - ph - margin; break; // BR
                case 2:  px = clip.Left   + margin;      py = clip.Top    + margin;      break; // TL
                case 3:  px = clip.Right  - pw - margin; py = clip.Top    + margin;      break; // TR
                default: px = clip.Left   + margin;      py = clip.Bottom - ph - margin; break; // BL
            }

            int bgAlpha = Math.Max(0, Math.Min(255, DashboardOpacity));
            using (var bg = new SolidBrush(Color.FromArgb(bgAlpha, 8, 8, 16)))
                gr.FillRectangle(bg, px - 4, py - 4, pw, ph);
            using (var brd = new Pen(Color.FromArgb(Math.Min(255, bgAlpha + 40), 90, 90, 120), 1))
                gr.DrawRectangle(brd, px - 4, py - 4, pw, ph);

            using (var tc = new SolidBrush(Color.FromArgb(220, 140, 160, 255)))
                gr.DrawString(lines[0], _fontTitle, tc, px, py);
            using (var fc = new SolidBrush(Color.FromArgb(170, 150, 155, 165)))
                for (int i = 1; i < lines.Length; i++)
                    gr.DrawString(lines[i], _fontDetail, fc, px, py + 14 + (i - 1) * 14);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private bool IsValidBase(HistoricalData data, int start, int end)
        {
            for (int i = start; i <= end; i++)
            {
                var bar = data[i, SeekOriginHistory.Begin] as HistoryItemBar;
                if (bar == null) return false;
                double range = bar.High - bar.Low;
                if (range <= 0) return false;
                double body = Math.Abs(bar.Close - bar.Open);
                if (body / range > BaseCandleBodyPct) return false;
            }
            return true;
        }

        private LegDir ClassifyLeg(HistoricalData data, int idx)
        {
            return ClassifyLegFromBar(data[idx, SeekOriginHistory.Begin] as HistoryItemBar);
        }

        private LegDir ClassifyLegFromBar(HistoryItemBar bar)
        {
            if (bar == null) return LegDir.None;
            double range = bar.High - bar.Low;
            if (range <= 0) return LegDir.None;
            double body = Math.Abs(bar.Close - bar.Open);
            if (body / range < BaseCandleBodyPct + 0.05) return LegDir.None;
            if (bar.Close > bar.Open) return LegDir.Up;
            if (bar.Close < bar.Open) return LegDir.Down;
            return LegDir.None;
        }

        private double MeasureMoveOut(HistoricalData data, int endOfBase, int total,
                                      bool isDemand, double bodyHi, double bodyLo)
        {
            int    scanLimit = Math.Min(total - 1, endOfBase + Math.Max(20, ZoneLookback / 4));
            var    baseBar   = data[endOfBase, SeekOriginHistory.Begin] as HistoryItemBar;
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

        private Period IndexToPeriod(int idx)
        {
            switch (idx)
            {
                case 0: return Period.MIN1;
                case 1: return Period.MIN5;
                case 2: return Period.MIN15;
                case 3: return Period.MIN30;
                case 4: return Period.HOUR1;
                case 5: return Period.HOUR4;
                case 6: return Period.DAY1;
                case 7: return Period.WEEK1;
                default: return Period.HOUR1;
            }
        }

        private string PeriodLabel(int idx)
        {
            switch (idx)
            {
                case 0: return "1m";
                case 1: return "5m";
                case 2: return "15m";
                case 3: return "30m";
                case 4: return "1h";
                case 5: return "4h";
                case 6: return "Daily";
                case 7: return "Weekly";
                default: return "?";
            }
        }

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
            lock (_lock) { _zones.Clear(); _retestBoxes.Clear(); }
        }

        public override void Dispose()
        {
            _fontTitle?.Dispose();
            _fontDetail?.Dispose();
            _fontBig?.Dispose();
            base.Dispose();
        }
    }
}
