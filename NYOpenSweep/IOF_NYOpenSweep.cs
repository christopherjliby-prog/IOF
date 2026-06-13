using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace IOF_NYOpenSweep
{
    public class IOF_NYOpenSweep : Indicator
    {
        // ── Settings ────────────────────────────────────────────────────────
        [InputParameter("15m range line color", 10)]
        public Color RangeColor = Color.DodgerBlue;

        [InputParameter("Active trade color", 20)]
        public Color ActiveColor = Color.FromArgb(80, 30, 144, 255);

        [InputParameter("Resolved trade color", 30)]
        public Color ResolvedColor = Color.FromArgb(80, 200, 50, 50);

        [InputParameter("Show labels", 40)]
        public bool ShowLabels = true;

        [InputParameter("Risk per trade ($)", 50)]
        public double RiskDollars = 200.0;

        [InputParameter("$ per point (MNQ=2, MES=5, NQ=20, ES=50)", 60)]
        public double DollarsPerPoint = 2.0;

        // ── Eastern Time (handles EDT/EST automatically) ─────────────────────
        private static readonly TimeZoneInfo EasternTz    = GetEasternTz();
        private static readonly TimeSpan     NyRangeStart = new TimeSpan(9, 30, 0);
        private static readonly TimeSpan     NyRangeEnd   = new TimeSpan(9, 45, 0);

        private static TimeZoneInfo GetEasternTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
        }

        // ── State per day ────────────────────────────────────────────────────
        private DateTime _currentDay    = DateTime.MinValue;
        private double   _range15High   = double.NaN;
        private double   _range15Low    = double.NaN;
        private bool     _range15Built  = false;
        private DateTime _rangeStartTime;

        private bool _highSwept     = false;
        private bool _lowSwept      = false;
        private bool _setupDrawn    = false;
        private int  _sweepBarIndex = -1;

        // ── Per-trade resolution tracking ────────────────────────────────────
        private int    _currentSetId  = -1;
        private int    _nextSetId     = 0;
        private bool   _tradeResolved = false;
        private bool   _tradeIsLong   = false;
        private double _tradeEntry    = double.NaN;
        private double _tradeStop     = double.NaN;
        private double _tradeTarget   = double.NaN;

        // ── Drawing storage (accumulate for backtest) ────────────────────────
        private readonly List<LineToDraw>  _lines  = new();
        private readonly List<BoxToDraw>   _boxes  = new();
        private readonly List<LabelToDraw> _labels = new();

        public IOF_NYOpenSweep()
        {
            Name           = "IOF NY Open Sweep";
            Description    = "ICT NY open first-15m range, sweep detection, and entry/stop/target zones.";
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            // Alpha=1 makes it invisible on the chart but gives the indicator
            // a real legend entry so right-click → Settings / Remove works.
            AddLineSeries("IOF NY Open Sweep", Color.FromArgb(1, 128, 128, 128), 1, LineStyle.Solid);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            SetValue(double.NaN);
            ProcessBar(Count - 1);
        }

        private void ProcessBar(int i)
        {
            var bar = (HistoryItemBar)HistoricalData[i];
            if (bar == null) return;

            DateTime barEt  = TimeZoneInfo.ConvertTime(bar.TimeLeft, EasternTz);
            DateTime barDay = barEt.Date;
            TimeSpan barTod = barEt.TimeOfDay;

            // ── New trading day → reset ──────────────────────────────────────
            if (barDay != _currentDay)
            {
                _currentDay    = barDay;
                _range15High   = double.NaN;
                _range15Low    = double.NaN;
                _range15Built  = false;
                _highSwept     = false;
                _lowSwept      = false;
                _setupDrawn    = false;
                _sweepBarIndex = -1;
                _tradeResolved = false;
                _tradeIsLong   = false;
                _tradeEntry    = double.NaN;
                _tradeStop     = double.NaN;
                _tradeTarget   = double.NaN;
                _currentSetId  = -1;
            }

            // ── Check if active trade resolved (TP or SL hit) ────────────────
            if (_setupDrawn && !_tradeResolved && !_tradeEntry.IsNaN())
            {
                bool slHit = _tradeIsLong ? bar.Low  <= _tradeStop   : bar.High >= _tradeStop;
                bool tpHit = _tradeIsLong ? bar.High >= _tradeTarget : bar.Low  <= _tradeTarget;
                if (slHit || tpHit)
                {
                    _tradeResolved = true;
                    foreach (var b in _boxes)
                        if (b.SetId == _currentSetId) b.Resolved = true;
                }
            }

            // ── Build 15m range: all bars in [9:30, 9:45) ET ────────────────
            if (!_range15Built)
            {
                if (barTod >= NyRangeStart && barTod < NyRangeEnd)
                {
                    if (_range15High.IsNaN()) _rangeStartTime = bar.TimeLeft;
                    _range15High = _range15High.IsNaN() ? bar.High : Math.Max(_range15High, bar.High);
                    _range15Low  = _range15Low.IsNaN()  ? bar.Low  : Math.Min(_range15Low,  bar.Low);
                }
                else if (barTod >= NyRangeEnd && !_range15High.IsNaN())
                {
                    _range15Built = true;
                    DrawRangeLines();
                }
                else
                {
                    return;
                }

                if (!_range15Built) return;
            }

            if (_setupDrawn) return;

            // ── Sweep detection ──────────────────────────────────────────────
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

            // ── Entry candle = bar immediately after sweep ───────────────────
            if ((_highSwept || _lowSwept) && i == _sweepBarIndex + 1)
            {
                double bodyHi = Math.Max(bar.Open, bar.Close);
                double bodyLo = Math.Min(bar.Open, bar.Close);

                _currentSetId = _nextSetId++;

                if (_lowSwept)
                    DrawLongSetup(bar, bodyLo, bar.Low, _range15High);
                else
                    DrawShortSetup(bar, bodyHi, bar.High, _range15Low);

                _setupDrawn = true;
            }
        }

        // ── Drawing helpers ──────────────────────────────────────────────────

        private void DrawRangeLines()
        {
            DateTime end    = _rangeStartTime.AddHours(8);
            DateTime lblOff = _rangeStartTime.AddMinutes(2);

            _lines.Add(new LineToDraw(_range15High, _rangeStartTime, end, RangeColor));
            _lines.Add(new LineToDraw(_range15Low,  _rangeStartTime, end, RangeColor));

            if (ShowLabels)
            {
                _labels.Add(new LabelToDraw(lblOff, _range15High, "15m NY open  ▲", Color.DodgerBlue, -1));
                _labels.Add(new LabelToDraw(lblOff, _range15Low,  "15m NY open  ▼", Color.DodgerBlue, -1));
            }
        }

        private void DrawLongSetup(HistoryItemBar bar, double entry, double stop, double target)
        {
            _tradeIsLong = true;
            _tradeEntry  = entry;
            _tradeStop   = stop;
            _tradeTarget = target;

            DateTime t0   = bar.TimeLeft;
            DateTime tEnd = t0.AddHours(6);
            DateTime lbl  = t0.AddMinutes(3);

            double risk      = Math.Abs(entry - stop);
            double reward    = Math.Abs(target - entry);
            double rr        = risk > 0 ? reward / risk : 0;
            int    contracts = ContractCount(risk);
            double dollarRisk = contracts * risk * DollarsPerPoint;

            // Narrow risk box (entry candle width): stop → entry
            _boxes.Add(new BoxToDraw(t0, t0.AddMinutes(10), stop, entry, ActiveColor, _currentSetId));

            // Wide reward fill: entry → target
            var rewardFill = Color.FromArgb(35, ActiveColor.R, ActiveColor.G, ActiveColor.B);
            _boxes.Add(new BoxToDraw(t0, tEnd, entry, target, rewardFill, _currentSetId));

            // Horizontal lines for each level
            _lines.Add(new LineToDraw(entry,  t0, tEnd, Color.FromArgb(200, 50, 205, 50)));    // lime
            _lines.Add(new LineToDraw(stop,   t0, tEnd, Color.FromArgb(200, 220, 60, 60)));    // red
            _lines.Add(new LineToDraw(target, t0, tEnd, Color.FromArgb(200, 50, 205, 50)));    // lime dashed

            if (ShowLabels)
            {
                _labels.Add(new LabelToDraw(lbl, target, $"15m High  |  TP {target:F2}  |  {rr:F1}R", Color.FromArgb(220, 100, 230, 100), _currentSetId));
                _labels.Add(new LabelToDraw(lbl, entry,  $"Long Entry  {entry:F2}  |  {contracts}ct  (~${dollarRisk:F0} risk)", Color.FromArgb(220, 100, 230, 100), _currentSetId));
                _labels.Add(new LabelToDraw(lbl, stop,   $"5m Sweep & Stop  {stop:F2}", Color.FromArgb(220, 220, 80, 80), _currentSetId));
            }
        }

        private void DrawShortSetup(HistoryItemBar bar, double entry, double stop, double target)
        {
            _tradeIsLong = false;
            _tradeEntry  = entry;
            _tradeStop   = stop;
            _tradeTarget = target;

            DateTime t0   = bar.TimeLeft;
            DateTime tEnd = t0.AddHours(6);
            DateTime lbl  = t0.AddMinutes(3);

            double risk      = Math.Abs(stop - entry);
            double reward    = Math.Abs(entry - target);
            double rr        = risk > 0 ? reward / risk : 0;
            int    contracts = ContractCount(risk);
            double dollarRisk = contracts * risk * DollarsPerPoint;

            // Narrow risk box: entry → stop
            _boxes.Add(new BoxToDraw(t0, t0.AddMinutes(10), entry, stop, ActiveColor, _currentSetId));

            // Wide reward fill: target → entry
            var rewardFill = Color.FromArgb(35, ActiveColor.R, ActiveColor.G, ActiveColor.B);
            _boxes.Add(new BoxToDraw(t0, tEnd, target, entry, rewardFill, _currentSetId));

            // Horizontal lines for each level
            _lines.Add(new LineToDraw(entry,  t0, tEnd, Color.FromArgb(200, 220, 100, 50)));   // orange
            _lines.Add(new LineToDraw(stop,   t0, tEnd, Color.FromArgb(200, 220, 60, 60)));    // red
            _lines.Add(new LineToDraw(target, t0, tEnd, Color.FromArgb(200, 220, 100, 50)));   // orange dashed

            if (ShowLabels)
            {
                _labels.Add(new LabelToDraw(lbl, target, $"15m Low  |  TP {target:F2}  |  {rr:F1}R", Color.FromArgb(220, 230, 130, 60), _currentSetId));
                _labels.Add(new LabelToDraw(lbl, entry,  $"Short Entry  {entry:F2}  |  {contracts}ct  (~${dollarRisk:F0} risk)", Color.FromArgb(220, 230, 130, 60), _currentSetId));
                _labels.Add(new LabelToDraw(lbl, stop,   $"5m Sweep & Stop  {stop:F2}", Color.FromArgb(220, 220, 80, 80), _currentSetId));
            }
        }

        private int ContractCount(double riskPoints)
        {
            if (riskPoints <= 0 || DollarsPerPoint <= 0) return 1;
            int contracts = (int)Math.Floor(RiskDollars / (riskPoints * DollarsPerPoint));
            return Math.Max(1, Math.Min(10, contracts));
        }

        // ── Renderer ─────────────────────────────────────────────────────────
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            var gr    = args.Graphics;
            var chart = this.CurrentChart;
            if (chart == null) return;
            var window = chart.MainWindow;

            foreach (var b in _boxes)
            {
                try
                {
                    Color fill = b.Resolved ? ResolvedColor : b.Color;
                    float x0 = (float)window.CoordinatesConverter.GetChartX(b.T0);
                    float x1 = (float)window.CoordinatesConverter.GetChartX(b.T1);
                    float y0 = (float)window.CoordinatesConverter.GetChartY(b.PriceTop);
                    float y1 = (float)window.CoordinatesConverter.GetChartY(b.PriceBot);
                    float w  = Math.Abs(x1 - x0);
                    float h  = Math.Abs(y1 - y0);
                    float xl = Math.Min(x0, x1);
                    float yt = Math.Min(y0, y1);
                    using var br  = new SolidBrush(fill);
                    gr.FillRectangle(br, xl, yt, w, h);
                    using var pen = new Pen(Color.FromArgb(140, fill.R, fill.G, fill.B), 1f);
                    gr.DrawRectangle(pen, xl, yt, w, h);
                }
                catch { }
            }

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

            foreach (var lbl in _labels)
            {
                try
                {
                    float x = (float)window.CoordinatesConverter.GetChartX(lbl.Time);
                    float y = (float)window.CoordinatesConverter.GetChartY(lbl.Price);
                    using var font = new Font("Arial", 8f, FontStyle.Bold);
                    using var br   = new SolidBrush(lbl.Color);
                    gr.DrawString(lbl.Text, font, br, x, y - 14);
                }
                catch { }
            }
        }

        // ── Data classes ─────────────────────────────────────────────────────
        private class LineToDraw
        {
            public double Price; public DateTime T0, T1; public Color Color;
            public LineToDraw(double price, DateTime t0, DateTime t1, Color color)
            { Price = price; T0 = t0; T1 = t1; Color = color; }
        }
        private class BoxToDraw
        {
            public DateTime T0, T1; public double PriceBot, PriceTop; public Color Color;
            public int SetId; public bool Resolved;
            public BoxToDraw(DateTime t0, DateTime t1, double bot, double top, Color color, int setId)
            { T0 = t0; T1 = t1; PriceBot = bot; PriceTop = top; Color = color; SetId = setId; }
        }
        private class LabelToDraw
        {
            public DateTime Time; public double Price; public string Text; public Color Color; public int SetId;
            public LabelToDraw(DateTime time, double price, string text, Color color, int setId)
            { Time = time; Price = price; Text = text; Color = color; SetId = setId; }
        }
    }

    internal static class DoubleExt
    {
        internal static bool IsNaN(this double d) => double.IsNaN(d);
    }
}
