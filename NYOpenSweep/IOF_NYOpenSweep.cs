using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace IOF_NYOpenSweep
{
    public class IOF_NYOpenSweep : Indicator
    {
        // ── Settings ────────────────────────────────────────────────────────
        [InputParameter("NY Open hour (UTC)", 0, 0, 23)]
        public int NyOpenHourUtc = 13;  // 13 = EDT (summer), 14 = EST (winter)

        [InputParameter("15m candle range color", 10)]
        public Color RangeColor = Color.DodgerBlue;

        [InputParameter("Long entry zone color", 20)]
        public Color LongColor = Color.FromArgb(60, 0, 200, 80);

        [InputParameter("Short entry zone color", 30)]
        public Color ShortColor = Color.FromArgb(60, 220, 50, 50);

        [InputParameter("Show R:R label", 40)]
        public bool ShowRR = true;

        // ── State per day ────────────────────────────────────────────────────
        private DateTime _currentDay = DateTime.MinValue;

        private double _range15High = double.NaN;
        private double _range15Low  = double.NaN;
        private bool   _range15Built = false;

        // index of the first 5m bar at NY open
        private int _nyBar0 = -1;

        // sweep tracking
        private bool _highSwept = false;
        private bool _lowSwept  = false;
        private bool _setupDrawn = false;

        // the bar index whose close completed the sweep
        private int _sweepBarIndex = -1;

        // ── Drawing storage (clear on new day) ──────────────────────────────
        private readonly List<LineToDraw>  _lines  = new();
        private readonly List<BoxToDraw>   _boxes  = new();
        private readonly List<LabelToDraw> _labels = new();

        public IOF_NYOpenSweep()
        {
            Name        = "IOF NY Open Sweep";
            Description = "ICT NY open first-15m range, sweep detection, and entry/stop/target zones.";
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            AddLineSeries("_dummy", Color.Transparent, 1, LineStyle.Solid);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason == UpdateReason.HistoricalBar && Count > 5)
                return; // only process on live/new bars after initial load — we'll scan history in OnInit via full pass

            ProcessBar(Count - 1);
        }

        // Full recalc pass called implicitly via OnUpdate for all bars
        private void ProcessBar(int i)
        {
            var bar = (HistoryItemBar)HistoricalData[i];
            if (bar == null) return;

            DateTime barTime = bar.TimeLeft.ToUniversalTime();
            DateTime barDay  = barTime.Date;

            // ── New trading day → reset ──────────────────────────────────────
            if (barDay != _currentDay)
            {
                _currentDay  = barDay;
                _range15High = double.NaN;
                _range15Low  = double.NaN;
                _range15Built = false;
                _nyBar0      = -1;
                _highSwept   = false;
                _lowSwept    = false;
                _setupDrawn  = false;
                _sweepBarIndex = -1;
            }

            // ── Identify NY open bars (first 3 × 5m = 15m candle) ───────────
            if (!_range15Built)
            {
                if (barTime.Hour == NyOpenHourUtc && barTime.Minute == 30)
                    _nyBar0 = i;

                if (_nyBar0 >= 0)
                {
                    int offset = i - _nyBar0;
                    if (offset >= 0 && offset <= 2) // bars 0, 1, 2 = 9:30 / 9:35 / 9:40
                    {
                        double h = _range15High.IsNaN() ? bar.High : Math.Max(_range15High, bar.High);
                        double l = _range15Low.IsNaN()  ? bar.Low  : Math.Min(_range15Low,  bar.Low);
                        _range15High = h;
                        _range15Low  = l;

                        if (offset == 2) // third 5m bar closes → 15m complete
                        {
                            _range15Built = true;
                            DrawRangeLines(bar.TimeLeft, i);
                        }
                    }
                }
                return; // still building range
            }

            if (_setupDrawn) return; // one setup per day

            // ── Sweep detection ──────────────────────────────────────────────
            // Wick sweep counts — any price through the level
            if (!_highSwept && bar.High > _range15High)
            {
                _highSwept     = true;
                _sweepBarIndex = i;
            }
            else if (!_lowSwept && bar.Low < _range15Low)
            {
                _lowSwept      = true;
                _sweepBarIndex = i;
            }

            // ── Entry candle = bar immediately after sweep bar ───────────────
            if ((_highSwept || _lowSwept) && i == _sweepBarIndex + 1)
            {
                double bodyHi = Math.Max(bar.Open, bar.Close);
                double bodyLo = Math.Min(bar.Open, bar.Close);
                double wickHi = bar.High;
                double wickLo = bar.Low;

                if (_lowSwept) // long setup — swept low, target = 15m high
                    DrawLongSetup(bar, bodyLo, wickLo, _range15High);
                else           // short setup — swept high, target = 15m low
                    DrawShortSetup(bar, bodyHi, wickHi, _range15Low);

                _setupDrawn = true;
            }
        }

        // ── Drawing helpers ──────────────────────────────────────────────────

        private void DrawRangeLines(DateTime time, int barIndex)
        {
            // Extend lines to end of session (rough: +8 hours)
            var endTime = time.AddHours(8);
            _lines.Add(new LineToDraw(_range15High, time, endTime, RangeColor, "15m High"));
            _lines.Add(new LineToDraw(_range15Low,  time, endTime, RangeColor, "15m Low"));
        }

        private void DrawLongSetup(HistoryItemBar bar, double entry, double stop, double target)
        {
            DateTime t0 = bar.TimeLeft;
            DateTime t1 = t0.AddMinutes(10); // box width = 2 candles

            // Entry zone box (body low = entry level)
            _boxes.Add(new BoxToDraw(t0, t1, entry, stop, LongColor));

            // Target line
            _lines.Add(new LineToDraw(target, t0, t0.AddHours(6), Color.Lime, "Target"));

            if (ShowRR)
            {
                double risk   = Math.Abs(entry - stop);
                double reward = Math.Abs(target - entry);
                double rr     = risk > 0 ? reward / risk : 0;
                _labels.Add(new LabelToDraw(t1, entry, $"L  E:{entry:F2}  SL:{stop:F2}  TP:{target:F2}  R:{rr:F1}R", Color.Lime));
            }
        }

        private void DrawShortSetup(HistoryItemBar bar, double entry, double stop, double target)
        {
            DateTime t0 = bar.TimeLeft;
            DateTime t1 = t0.AddMinutes(10);

            _boxes.Add(new BoxToDraw(t0, t1, stop, entry, ShortColor));

            _lines.Add(new LineToDraw(target, t0, t0.AddHours(6), Color.OrangeRed, "Target"));

            if (ShowRR)
            {
                double risk   = Math.Abs(stop - entry);
                double reward = Math.Abs(entry - target);
                double rr     = risk > 0 ? reward / risk : 0;
                _labels.Add(new LabelToDraw(t1, entry, $"S  E:{entry:F2}  SL:{stop:F2}  TP:{target:F2}  R:{rr:F1}R", Color.OrangeRed));
            }
        }

        // ── Renderer ─────────────────────────────────────────────────────────
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            var gr      = args.Graphics;
            var chart   = this.CurrentChart;
            if (chart == null) return;

            var window  = chart.MainWindow;

            // Draw range + target lines
            foreach (var l in _lines)
            {
                try
                {
                    float x0 = (float)window.CoordinatesConverter.GetChartX(l.T0);
                    float x1 = (float)window.CoordinatesConverter.GetChartX(l.T1);
                    float y  = (float)window.CoordinatesConverter.GetChartY(l.Price);
                    using var pen = new Pen(l.Color, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                    gr.DrawLine(pen, x0, y, x1, y);
                }
                catch { }
            }

            // Draw entry/stop boxes
            foreach (var b in _boxes)
            {
                try
                {
                    float x0 = (float)window.CoordinatesConverter.GetChartX(b.T0);
                    float x1 = (float)window.CoordinatesConverter.GetChartX(b.T1);
                    float y0 = (float)window.CoordinatesConverter.GetChartY(b.PriceTop);
                    float y1 = (float)window.CoordinatesConverter.GetChartY(b.PriceBot);
                    float w  = Math.Abs(x1 - x0);
                    float h  = Math.Abs(y1 - y0);
                    float xl = Math.Min(x0, x1);
                    float yt = Math.Min(y0, y1);
                    using var br = new SolidBrush(b.Color);
                    gr.FillRectangle(br, xl, yt, w, h);
                    using var pen = new Pen(Color.FromArgb(200, b.Color));
                    gr.DrawRectangle(pen, xl, yt, w, h);
                }
                catch { }
            }

            // Labels
            foreach (var lbl in _labels)
            {
                try
                {
                    float x = (float)window.CoordinatesConverter.GetChartX(lbl.Time);
                    float y = (float)window.CoordinatesConverter.GetChartY(lbl.Price);
                    using var font = new Font("Arial", 8f);
                    using var br   = new SolidBrush(lbl.Color);
                    gr.DrawString(lbl.Text, font, br, x + 4, y - 10);
                }
                catch { }
            }
        }

        // ── Data classes ─────────────────────────────────────────────────────
        private class LineToDraw
        {
            public double Price; public DateTime T0, T1; public Color Color; public string Tag;
            public LineToDraw(double price, DateTime t0, DateTime t1, Color color, string tag)
            { Price = price; T0 = t0; T1 = t1; Color = color; Tag = tag; }
        }
        private class BoxToDraw
        {
            public DateTime T0, T1; public double PriceBot, PriceTop; public Color Color;
            public BoxToDraw(DateTime t0, DateTime t1, double bot, double top, Color color)
            { T0 = t0; T1 = t1; PriceBot = bot; PriceTop = top; Color = color; }
        }
        private class LabelToDraw
        {
            public DateTime Time; public double Price; public string Text; public Color Color;
            public LabelToDraw(DateTime time, double price, string text, Color color)
            { Time = time; Price = price; Text = text; Color = color; }
        }
    }

    internal static class DoubleExt
    {
        internal static bool IsNaN(this double d) => double.IsNaN(d);
    }
}
