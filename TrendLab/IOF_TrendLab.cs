// IOF_TrendLab.cs
// Multi-timeframe trend dashboard: shows Bull/Bear/Flat for 8 timeframes simultaneously.
// Follows ZoneLab architecture for MTF data fetching.
// Uses TrendStateMachine (3-segment body-close logic, Mr. Black methodology).

using System;
using System.Collections.Generic;
using System.Drawing;
using TradePhantomsIOF.Trend;
using TradingPlatform.BusinessLayer;

namespace IOF_TrendLab
{
    public class IOF_TrendLab : Indicator
    {
        // ── Parameters ────────────────────────────────────────────────────────

        [InputParameter("Swing Fractal Lookback", 0, 1, 10, 1, 0)]
        public int SwingLookback = 3;

        [InputParameter("Require Engulfing for Control Point", 1)]
        public bool RequireEngulfing = false;

        [InputParameter("History Days (Monthly)", 2, 30, 3650, 30, 0)]
        public int HistDaysMN = 1825;   // 5 years

        [InputParameter("History Days (Weekly)", 3, 7, 1825, 7, 0)]
        public int HistDaysW = 730;     // 2 years

        [InputParameter("History Days (Daily)", 4, 7, 730, 7, 0)]
        public int HistDaysD = 365;     // 1 year

        [InputParameter("History Days (4H)", 5, 1, 365, 1, 0)]
        public int HistDays4H = 90;

        [InputParameter("History Days (1H)", 6, 1, 180, 1, 0)]
        public int HistDays1H = 30;

        [InputParameter("History Days (15M)", 7, 1, 90, 1, 0)]
        public int HistDays15M = 14;

        [InputParameter("History Days (5M)", 8, 1, 30, 1, 0)]
        public int HistDays5M = 7;

        [InputParameter("History Days (1M)", 9, 1, 14, 1, 0)]
        public int HistDays1M = 3;

        [InputParameter("Panel X Offset (px)", 10, 0, 2000, 1, 0)]
        public int PanelX = 10;

        [InputParameter("Panel Y Offset (px)", 11, 0, 2000, 1, 0)]
        public int PanelY = 10;

        [InputParameter("Row Height (px)", 12, 14, 50, 1, 0)]
        public int RowHeight = 22;

        [InputParameter("Panel Width (px)", 13, 80, 400, 1, 0)]
        public int PanelWidth = 180;

        // ── Timeframe definitions ─────────────────────────────────────────────

        private static readonly string[] TF_LABELS = { "MN", "W", "D", "4H", "1H", "15M", "5M", "1M" };

        private static readonly Period[] TF_PERIODS = {
            Period.MONTH1,
            Period.WEEK1,
            Period.DAY1,
            Period.HOUR4,
            Period.HOUR1,
            Period.MIN15,
            Period.MIN5,
            Period.MIN1
        };

        private int[] HistDays => new int[] {
            HistDaysMN, HistDaysW, HistDaysD, HistDays4H,
            HistDays1H, HistDays15M, HistDays5M, HistDays1M
        };

        private const int TF_COUNT = 8;

        // ── State ─────────────────────────────────────────────────────────────

        private HistoricalData[] _feeds;
        private TrendStateMachine[] _machines;
        private int[] _lastProcessedIdx;   // last bar index fed into each TSM (feed-indexed)
        private double _tickSize;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        protected override void OnInit()
        {
            // This indicator draws only a panel; no price series needed.
            // Using a single dummy series to satisfy the framework.
            AddLineSeries("Dummy", Color.Transparent, 0, LineStyle.Solid);
            SeparateWindow = false;

            _feeds            = new HistoricalData[TF_COUNT];
            _machines         = new TrendStateMachine[TF_COUNT];
            _lastProcessedIdx = new int[TF_COUNT];
            _tickSize         = this.Symbol?.TickSize ?? 0.25;

            for (int i = 0; i < TF_COUNT; i++)
            {
                _lastProcessedIdx[i] = -1;

                _machines[i] = new TrendStateMachine
                {
                    SwingFractalLookback         = SwingLookback,
                    RequireEngulfingForControlPoint = RequireEngulfing,
                    RequireSegments              = 3
                };

                try
                {
                    DateTime fromTime = DateTime.UtcNow.AddDays(-HistDays[i]);
                    _feeds[i] = this.Symbol.GetHistory(TF_PERIODS[i], fromTime, DateTime.UtcNow);
                }
                catch
                {
                    _feeds[i] = null;
                }
            }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            // Drive all 8 MTF state machines with any newly closed bars.
            for (int i = 0; i < TF_COUNT; i++)
            {
                ProcessFeed(i);
            }

            // Keep the dummy series populated so the indicator doesn't complain.
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

        // ── MTF bar processing ─────────────────────────────────────────────────

        private void ProcessFeed(int idx)
        {
            var feed = _feeds[idx];
            if (feed == null) return;

            int count = feed.Count;
            if (count < 2) return;  // need at least 1 closed bar + forming bar

            // Last closed bar is at [count - 2] (0=oldest indexing from GetHistory).
            int lastClosed = count - 2;

            int startFrom = _lastProcessedIdx[idx] + 1;
            if (startFrom > lastClosed) return;  // no new closed bars

            for (int j = startFrom; j <= lastClosed; j++)
            {
                var bar = feed[j] as HistoryItemBar;
                if (bar == null) continue;

                _machines[idx].OnBarClose(
                    j,
                    bar.TimeLeft,
                    bar.Open,
                    bar.High,
                    bar.Low,
                    bar.Close,
                    _tickSize);
            }

            _lastProcessedIdx[idx] = lastClosed;
        }

        // ── Dashboard rendering ────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            var g = args.Graphics;
            if (g == null) return;

            DrawDashboard(g, args.Rectangle);
        }

        private void DrawDashboard(Graphics g, Rectangle chartRect)
        {
            int panelHeight = TF_COUNT * RowHeight + 4;
            int x = chartRect.Left + PanelX;
            int y = chartRect.Top  + PanelY;

            // Panel background
            using (var bgBrush = new SolidBrush(Color.FromArgb(200, 15, 15, 15)))
                g.FillRectangle(bgBrush, x, y, PanelWidth, panelHeight);

            using (var borderPen = new Pen(Color.FromArgb(80, 80, 80)))
                g.DrawRectangle(borderPen, x, y, PanelWidth - 1, panelHeight - 1);

            int labelW  = 40;
            int stateW  = PanelWidth - labelW - 8;
            int rowX    = x + 4;
            int stateX  = rowX + labelW;

            using (var labelFont = new Font("Consolas", 9f, FontStyle.Bold))
            using (var stateFont = new Font("Consolas", 9f, FontStyle.Bold))
            {
                for (int i = 0; i < TF_COUNT; i++)
                {
                    int rowY = y + 2 + i * RowHeight;

                    // TF label
                    using (var labelBrush = new SolidBrush(Color.FromArgb(180, 180, 180)))
                        g.DrawString(TF_LABELS[i], labelFont, labelBrush, rowX, rowY + 3);

                    // State
                    TrendState state = _machines != null && _machines[i] != null
                        ? _machines[i].CurrentState
                        : TrendState.Flat;

                    bool hasData = _lastProcessedIdx != null && _lastProcessedIdx[i] > 5;

                    Color bgColor;
                    string stateLabel;

                    if (!hasData)
                    {
                        bgColor    = Color.FromArgb(40, 40, 40);
                        stateLabel = "LOADING";
                    }
                    else
                    {
                        switch (state)
                        {
                            case TrendState.Bull:
                                bgColor    = Color.FromArgb(0, 160, 60);
                                stateLabel = "BULL";
                                break;
                            case TrendState.Bear:
                                bgColor    = Color.FromArgb(200, 30, 30);
                                stateLabel = "BEAR";
                                break;
                            default:
                                bgColor    = Color.FromArgb(60, 60, 70);
                                stateLabel = "FLAT";
                                break;
                        }
                    }

                    using (var stateBg = new SolidBrush(bgColor))
                        g.FillRectangle(stateBg, stateX, rowY + 1, stateW, RowHeight - 3);

                    using (var stateBrush = new SolidBrush(Color.White))
                    {
                        var strSize = g.MeasureString(stateLabel, stateFont);
                        float sx = stateX + (stateW - strSize.Width) / 2f;
                        float sy = rowY + (RowHeight - strSize.Height) / 2f;
                        g.DrawString(stateLabel, stateFont, stateBrush, sx, sy);
                    }
                }
            }

            // Title bar at bottom of panel
            int titleY = y + panelHeight - RowHeight - 1;
            // Optional: draw bar counts for debugging
            // (omitted for clean production display)
        }
    }
}
