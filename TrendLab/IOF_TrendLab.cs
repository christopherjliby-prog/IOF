// IOF_TrendLab.cs
// Multi-timeframe trend dashboard + current-chart structure drawing.
// Mr. Black leg-based methodology: 3-segment structures, body closes only.
// Dual control points: ControllingHigh (bull HH) + ControllingLow (bear LL).
// FLAT when price is between both. No direct Bull→Bear flip.

using System;
using System.Collections.Generic;
using System.Drawing;
using TradePhantomsIOF.Trend;
using TradingPlatform.BusinessLayer;

namespace IOF_TrendLab
{
    public class IOF_TrendLab : Indicator
    {
        // ── Trend Logic Settings ───────────────────────────────────────────

        [InputParameter("Swing Fractal Lookback", 0, 1, 10, 1, 0)]
        public int SwingLookback = 3;

        [InputParameter("Required Segments (3 = Mr. Black default)", 1, 2, 6, 1, 0)]
        public int RequireSegments = 3;

        [InputParameter("Require Engulfing for Control Point", 2)]
        public bool RequireEngulfing = false;

        [InputParameter("Body-Close Tick Tolerance", 3, 0, 10, 1, 0)]
        public int BodyCloseTolerance = 0;

        // ── History Depth Per Timeframe ────────────────────────────────────

        [InputParameter("History Days (Monthly)", 10, 30, 3650, 30, 0)]
        public int HistDaysMN = 1825;

        [InputParameter("History Days (Weekly)", 11, 7, 1825, 7, 0)]
        public int HistDaysW = 730;

        [InputParameter("History Days (Daily)", 12, 7, 730, 7, 0)]
        public int HistDaysD = 365;

        [InputParameter("History Days (4H)", 13, 1, 365, 1, 0)]
        public int HistDays4H = 90;

        [InputParameter("History Days (1H)", 14, 1, 180, 1, 0)]
        public int HistDays1H = 30;

        [InputParameter("History Days (15M)", 15, 1, 90, 1, 0)]
        public int HistDays15M = 14;

        [InputParameter("History Days (5M)", 16, 1, 30, 1, 0)]
        public int HistDays5M = 7;

        [InputParameter("History Days (1M)", 17, 1, 14, 1, 0)]
        public int HistDays1M = 3;

        // ── Timeframe Visibility Toggles ───────────────────────────────────

        [InputParameter("Show Monthly", 20)]
        public bool ShowMN = true;

        [InputParameter("Show Weekly", 21)]
        public bool ShowW = true;

        [InputParameter("Show Daily", 22)]
        public bool ShowD = true;

        [InputParameter("Show 4H", 23)]
        public bool Show4H = true;

        [InputParameter("Show 1H", 24)]
        public bool Show1H = true;

        [InputParameter("Show 15M", 25)]
        public bool Show15M = true;

        [InputParameter("Show 5M", 26)]
        public bool Show5M = true;

        [InputParameter("Show 1M", 27)]
        public bool Show1M = true;

        // ── Panel Layout ───────────────────────────────────────────────────

        [InputParameter("Panel X Offset (px)", 30, 0, 2000, 1, 0)]
        public int PanelX = 10;

        [InputParameter("Panel Y Offset (px)", 31, 0, 2000, 1, 0)]
        public int PanelY = 10;

        [InputParameter("Row Height (px)", 32, 14, 60, 1, 0)]
        public int RowHeight = 24;

        [InputParameter("Panel Width (px)", 33, 100, 500, 1, 0)]
        public int PanelWidth = 200;

        [InputParameter("Font Size", 34, 6, 18, 1, 0)]
        public int FontSize = 9;

        [InputParameter("Label Column Width (px)", 35, 20, 100, 1, 0)]
        public int LabelColWidth = 44;

        // ── Display Options ────────────────────────────────────────────────

        [InputParameter("Show Control Point Price", 40)]
        public bool ShowControlPrice = true;

        [InputParameter("Show Bar Count", 41)]
        public bool ShowBarCount = false;

        [InputParameter("Show Title Bar", 42)]
        public bool ShowTitle = true;

        [InputParameter("Min Bars Before Showing State (warm-up)", 43, 1, 50, 1, 0)]
        public int WarmupBars = 6;

        // ── Chart Drawing ──────────────────────────────────────────────────

        [InputParameter("Draw Structure Labels (HH/HL/LH/LL)", 60)]
        public bool DrawLabels = true;

        [InputParameter("Draw Controlling High Zone", 61)]
        public bool DrawCtrlHigh = true;

        [InputParameter("Draw Controlling Low Zone", 62)]
        public bool DrawCtrlLow = true;

        [InputParameter("Structure Label Font Size", 63, 6, 16, 1, 0)]
        public int LabelFontSize = 8;

        [InputParameter("Zone Line Thickness (px)", 64, 1, 5, 1, 0)]
        public int ZoneLineThick = 2;

        [InputParameter("Zone Fill Alpha (0-255)", 65, 0, 255, 1, 0)]
        public int ZoneFillAlpha = 30;

        // ── Colors ─────────────────────────────────────────────────────────

        [InputParameter("Bull Color", 50)]
        public Color BullColor = Color.FromArgb(0, 160, 60);

        [InputParameter("Bear Color", 51)]
        public Color BearColor = Color.FromArgb(200, 30, 30);

        [InputParameter("Flat Color", 52)]
        public Color FlatColor = Color.FromArgb(60, 60, 70);

        [InputParameter("Loading Color", 53)]
        public Color LoadingColor = Color.FromArgb(35, 35, 35);

        [InputParameter("Panel Background Color", 54)]
        public Color PanelBgColor = Color.FromArgb(200, 15, 15, 15);

        [InputParameter("Panel Border Color", 55)]
        public Color BorderColor = Color.FromArgb(80, 80, 80);

        [InputParameter("Label Text Color", 56)]
        public Color LabelTextColor = Color.FromArgb(180, 180, 180);

        [InputParameter("State Text Color", 57)]
        public Color StateTextColor = Color.White;

        [InputParameter("Title Text Color", 58)]
        public Color TitleTextColor = Color.FromArgb(220, 220, 220);

        [InputParameter("Controlling High Color", 66)]
        public Color CtrlHighColor = Color.FromArgb(200, 30, 30);

        [InputParameter("Controlling Low Color", 67)]
        public Color CtrlLowColor = Color.FromArgb(0, 160, 60);

        [InputParameter("HH Label Color", 68)]
        public Color HHColor = Color.FromArgb(0, 200, 80);

        [InputParameter("LL Label Color", 69)]
        public Color LLColor = Color.FromArgb(220, 40, 40);

        [InputParameter("HL Label Color", 70)]
        public Color HLColor = Color.FromArgb(0, 160, 60);

        [InputParameter("LH Label Color", 71)]
        public Color LHColor = Color.FromArgb(180, 30, 30);

        // ── Internal ───────────────────────────────────────────────────────

        private static readonly string[] TF_LABELS  = { "MN", "W", "D", "4H", "1H", "15M", "5M", "1M" };
        private static readonly Period[] TF_PERIODS = {
            Period.MONTH1, Period.WEEK1, Period.DAY1, Period.HOUR4,
            Period.HOUR1, Period.MIN15, Period.MIN5, Period.MIN1
        };
        private const int TF_COUNT = 8;

        private bool[] TF_VISIBLE => new bool[] {
            ShowMN, ShowW, ShowD, Show4H, Show1H, Show15M, Show5M, Show1M
        };

        private int[] HistDays => new int[] {
            HistDaysMN, HistDaysW, HistDaysD, HistDays4H,
            HistDays1H, HistDays15M, HistDays5M, HistDays1M
        };

        private HistoricalData[]    _feeds;
        private TrendStateMachine[] _machines;
        private int[]               _lastProcessedIdx;
        private double              _tickSize;
        private TrendSnapshot[]     _snapshots;

        // Chart-TF machine (for drawing on current chart)
        private TrendStateMachine   _chartMachine;
        private int                 _chartLastBar = -1;
        private TrendSnapshot       _chartSnapshot;

        // ── Lifecycle ──────────────────────────────────────────────────────

        protected override void OnInit()
        {
            SeparateWindow = false;
            AddLineSeries("Dummy", Color.Transparent, 1, LineStyle.Solid);

            _feeds            = new HistoricalData[TF_COUNT];
            _machines         = new TrendStateMachine[TF_COUNT];
            _lastProcessedIdx = new int[TF_COUNT];
            _snapshots        = new TrendSnapshot[TF_COUNT];
            _tickSize         = this.Symbol?.TickSize ?? 0.25;

            _chartMachine = new TrendStateMachine();
            _chartLastBar = -1;

            for (int i = 0; i < TF_COUNT; i++)
            {
                _lastProcessedIdx[i] = -1;
                _machines[i] = new TrendStateMachine
                {
                    SwingFractalLookback            = SwingLookback,
                    RequireEngulfingForControlPoint  = RequireEngulfing,
                    RequireSegments                  = RequireSegments,
                };

                try
                {
                    DateTime from = DateTime.UtcNow.AddDays(-HistDays[i]);
                    _feeds[i] = this.Symbol.GetHistory(TF_PERIODS[i], from, DateTime.UtcNow);
                }
                catch
                {
                    _feeds[i] = null;
                }
            }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            // Process chart-TF bars for the drawing machine
            var mainFeed = this.HistoricalData;
            if (mainFeed != null && mainFeed.Count >= 2)
            {
                int lastClosed = mainFeed.Count - 2;
                for (int j = _chartLastBar + 1; j <= lastClosed; j++)
                {
                    var bar = mainFeed[j] as HistoryItemBar;
                    if (bar != null)
                        _chartMachine.OnBarClose(j, bar.TimeLeft, bar.Open, bar.High, bar.Low, bar.Close, _tickSize);
                }
                _chartLastBar = lastClosed;
            }
            _chartSnapshot = _chartMachine.GetSnapshot();

            // Process MTF feeds
            for (int i = 0; i < TF_COUNT; i++)
                ProcessFeed(i);

            for (int i = 0; i < TF_COUNT; i++)
            {
                if (_machines[i] != null)
                    _snapshots[i] = _machines[i].GetSnapshot();
            }

            SetValue(double.NaN, 0);
        }

        protected override void OnClear()
        {
            if (_feeds == null) return;
            for (int i = 0; i < TF_COUNT; i++)
            {
                try { _feeds[i]?.Dispose(); } catch { }
                _feeds[i] = null;
            }
        }

        // ── MTF Bar Processing ─────────────────────────────────────────────

        private void ProcessFeed(int idx)
        {
            var feed = _feeds[idx];
            if (feed == null) return;

            int count = feed.Count;
            if (count < 2) return;

            int lastClosed = count - 2;
            int startFrom  = _lastProcessedIdx[idx] + 1;
            if (startFrom > lastClosed) return;

            for (int j = startFrom; j <= lastClosed; j++)
            {
                var bar = feed[j] as HistoryItemBar;
                if (bar == null) continue;

                _machines[idx].OnBarClose(
                    j, bar.TimeLeft, bar.Open, bar.High, bar.Low, bar.Close, _tickSize);
            }

            _lastProcessedIdx[idx] = lastClosed;
        }

        // ── Painting ───────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            var g = args.Graphics;
            if (g == null) return;

            // Draw chart structure (labels + zones) on current TF
            DrawChartStructure(g, args.Rectangle);

            // Draw MTF panel
            DrawPanel(g, args.Rectangle);
        }

        // ── Chart Structure Drawing ────────────────────────────────────────

        private void DrawChartStructure(Graphics g, Rectangle chartRect)
        {
            if (_chartSnapshot == null) return;

            var win = this.CurrentChart?.MainWindow;
            if (win == null) return;

            // ── Controlling High zone ──────────────────────────────────────
            if (DrawCtrlHigh && !double.IsNaN(_chartSnapshot.ControllingHigh))
            {
                float y = (float)win.CoordinatesConverter.GetChartY(_chartSnapshot.ControllingHigh);
                if (y >= chartRect.Top && y <= chartRect.Bottom)
                {
                    // Fill zone (thin band)
                    int bandH = Math.Max(2, ZoneLineThick * 2);
                    using (var fill = new SolidBrush(Color.FromArgb(ZoneFillAlpha, CtrlHighColor)))
                        g.FillRectangle(fill, chartRect.Left, (int)y - bandH, chartRect.Width, bandH * 2);
                    // Line
                    using (var pen = new Pen(CtrlHighColor, ZoneLineThick))
                        g.DrawLine(pen, chartRect.Left, (int)y, chartRect.Right, (int)y);
                    // Label
                    using (var f = new Font("Consolas", LabelFontSize, FontStyle.Bold))
                    using (var b = new SolidBrush(CtrlHighColor))
                        g.DrawString("Controlling High", f, b, chartRect.Right - 140, y - LabelFontSize - 4);
                }
            }

            // ── Controlling Low zone ───────────────────────────────────────
            if (DrawCtrlLow && !double.IsNaN(_chartSnapshot.ControllingLow))
            {
                float y = (float)win.CoordinatesConverter.GetChartY(_chartSnapshot.ControllingLow);
                if (y >= chartRect.Top && y <= chartRect.Bottom)
                {
                    int bandH = Math.Max(2, ZoneLineThick * 2);
                    using (var fill = new SolidBrush(Color.FromArgb(ZoneFillAlpha, CtrlLowColor)))
                        g.FillRectangle(fill, chartRect.Left, (int)y - bandH, chartRect.Width, bandH * 2);
                    using (var pen = new Pen(CtrlLowColor, ZoneLineThick))
                        g.DrawLine(pen, chartRect.Left, (int)y, chartRect.Right, (int)y);
                    using (var f = new Font("Consolas", LabelFontSize, FontStyle.Bold))
                    using (var b = new SolidBrush(CtrlLowColor))
                        g.DrawString("Controlling Low", f, b, chartRect.Right - 130, y + 4);
                }
            }

            // ── HH / HL / LH / LL labels ──────────────────────────────────
            if (DrawLabels && _chartSnapshot.LegPivots != null)
            {
                using (var f = new Font("Consolas", LabelFontSize, FontStyle.Bold))
                {
                    foreach (var p in _chartSnapshot.LegPivots)
                    {
                        float y, x;
                        try
                        {
                            y = (float)win.CoordinatesConverter.GetChartY(p.Price);
                            x = (float)win.CoordinatesConverter.GetChartX(p.Time);
                        }
                        catch { continue; }

                        if (x < chartRect.Left || x > chartRect.Right) continue;
                        if (y < chartRect.Top  || y > chartRect.Bottom) continue;

                        Color c = p.Label == "HH" ? HHColor :
                                  p.Label == "LL" ? LLColor :
                                  p.Label == "HL" ? HLColor : LHColor;

                        using (var b = new SolidBrush(c))
                        {
                            // Diamond marker
                            float dm = 4f;
                            var diamond = new PointF[]
                            {
                                new PointF(x,      y - dm),
                                new PointF(x + dm, y),
                                new PointF(x,      y + dm),
                                new PointF(x - dm, y)
                            };
                            g.FillPolygon(b, diamond);

                            // Label above (bull) or below (bear) diamond
                            float ly = p.IsBull ? y - dm - LabelFontSize - 2 : y + dm + 2;
                            g.DrawString(p.Label, f, b, x - 8, ly);
                        }
                    }
                }
            }
        }

        // ── Panel Drawing ──────────────────────────────────────────────────

        private void DrawPanel(Graphics g, Rectangle chartRect)
        {
            bool[] vis = TF_VISIBLE;
            int visibleRows = 0;
            for (int i = 0; i < TF_COUNT; i++)
                if (vis[i]) visibleRows++;

            if (visibleRows == 0) return;

            int titleH      = ShowTitle ? RowHeight : 0;
            int panelHeight = visibleRows * RowHeight + titleH + 4;
            int x           = chartRect.Left + PanelX;
            int y           = chartRect.Top  + PanelY;

            using (var bgBrush  = new SolidBrush(PanelBgColor))
                g.FillRectangle(bgBrush, x, y, PanelWidth, panelHeight);
            using (var borderPen = new Pen(BorderColor))
                g.DrawRectangle(borderPen, x, y, PanelWidth - 1, panelHeight - 1);

            float fSize  = Math.Max(6f, FontSize);
            int   stateW = PanelWidth - LabelColWidth - 8;
            int   rowX   = x + 4;
            int   stateX = rowX + LabelColWidth;
            int   curY   = y + 2;

            using (var labelFont = new Font("Consolas", fSize, FontStyle.Bold))
            using (var stateFont = new Font("Consolas", fSize, FontStyle.Bold))
            using (var titleFont = new Font("Consolas", fSize - 1f > 6f ? fSize - 1f : 6f, FontStyle.Bold))
            {
                if (ShowTitle)
                {
                    using (var titleBrush = new SolidBrush(TitleTextColor))
                    {
                        string title = "IOF TREND";
                        var sz = g.MeasureString(title, titleFont);
                        g.DrawString(title, titleFont, titleBrush,
                            x + (PanelWidth - sz.Width) / 2f,
                            curY + (RowHeight - sz.Height) / 2f);
                    }
                    using (var sepPen = new Pen(BorderColor))
                        g.DrawLine(sepPen, x, curY + RowHeight, x + PanelWidth - 1, curY + RowHeight);
                    curY += RowHeight;
                }

                for (int i = 0; i < TF_COUNT; i++)
                {
                    if (!vis[i]) continue;

                    int rowY = curY;
                    curY += RowHeight;

                    using (var lb = new SolidBrush(LabelTextColor))
                        g.DrawString(TF_LABELS[i], labelFont, lb, rowX, rowY + (RowHeight - FontSize) / 2f - 1);

                    var snap    = _snapshots[i];
                    bool loaded = _lastProcessedIdx[i] >= WarmupBars;
                    TrendState state = snap != null ? snap.State : TrendState.Flat;

                    Color  bgColor;
                    string stateLabel;

                    if (!loaded)
                    {
                        bgColor    = LoadingColor;
                        stateLabel = "LOADING";
                    }
                    else
                    {
                        switch (state)
                        {
                            case TrendState.Bull: bgColor = BullColor; stateLabel = "BULL"; break;
                            case TrendState.Bear: bgColor = BearColor; stateLabel = "BEAR"; break;
                            default:              bgColor = FlatColor;  stateLabel = "FLAT"; break;
                        }
                    }

                    using (var stateBg = new SolidBrush(bgColor))
                        g.FillRectangle(stateBg, stateX, rowY + 1, stateW, RowHeight - 3);

                    using (var stBrush = new SolidBrush(StateTextColor))
                    {
                        if (ShowControlPrice && loaded && state != TrendState.Flat && snap != null
                            && !double.IsNaN(snap.ControllingPivotPrice))
                        {
                            string priceStr = snap.ControllingPivotPrice.ToString("F2");
                            var labSz  = g.MeasureString(stateLabel, stateFont);
                            var priceSz = g.MeasureString(priceStr, stateFont);
                            float ly = rowY + (RowHeight - labSz.Height)  / 2f;
                            float py = rowY + (RowHeight - priceSz.Height) / 2f;
                            g.DrawString(stateLabel, stateFont, stBrush, stateX + 3, ly);
                            float px = stateX + stateW - priceSz.Width - 3;
                            if (px > stateX + labSz.Width + 4)
                                g.DrawString(priceStr, stateFont, stBrush, px, py);
                        }
                        else
                        {
                            var sz = g.MeasureString(stateLabel, stateFont);
                            g.DrawString(stateLabel, stateFont, stBrush,
                                stateX + (stateW - sz.Width)   / 2f,
                                rowY   + (RowHeight - sz.Height) / 2f);
                        }
                    }

                    if (ShowBarCount && loaded)
                    {
                        string cnt = _lastProcessedIdx[i].ToString();
                        using (var cntBrush = new SolidBrush(Color.FromArgb(120, 120, 120)))
                        using (var cntFont  = new Font("Consolas", Math.Max(6f, fSize - 2f)))
                            g.DrawString(cnt, cntFont, cntBrush, rowX, rowY + RowHeight - cntFont.Height - 1);
                    }

                    using (var sepPen = new Pen(Color.FromArgb(40, 80, 80, 80)))
                        g.DrawLine(sepPen, x + 1, rowY + RowHeight - 1, x + PanelWidth - 2, rowY + RowHeight - 1);
                }
            }
        }
    }
}
