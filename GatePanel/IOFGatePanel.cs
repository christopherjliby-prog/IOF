using System;
using System.Drawing;
using System.Linq;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;
using TradingPlatform.BusinessLayer.Native;

namespace IOF_GatePanel
{
    public class IOFGatePanel : Indicator
    {
        [InputParameter("Account ID (leave blank for first account)", 10)]
        public string AccountId = "";

        // Emergency price overrides — leave 0 to use DOM auto-sync
        [InputParameter("Manual Entry price  (0 = auto-sync from DOM)", 20)]
        public double ManualEntryPrice = 0.0;

        [InputParameter("Manual Stop price   (0 = auto-sync from DOM)", 30)]
        public double ManualStopPrice = 0.0;

        // ── State ─────────────────────────────────────────────────────
        private static readonly string[] Instruments = { "MES", "MNQ", "GC", "CL" };
        private static readonly string[] Grades      = { "A", "B", "C" };
        private static readonly string[] ExitArchs   = { "Single→LVN", "Split", "Scale", "Trail" };

        private int  _instIdx  = 0;
        private bool _isLong   = true;
        private int  _gradeIdx = 0;
        private int  _archIdx  = 0;

        private double _entryPrice  = 0;
        private double _stopPrice   = 0;
        private double _targetPrice = 0;

        // Daily tracking
        private int      _tradesToday       = 0;
        private double   _sessionHighEquity = 0;
        private double   _dailyPnL          = 0;
        private DateTime _lastTrackDate     = DateTime.MinValue;

        // Calculated
        private int    _contracts   = 1;
        private double _rrRatio     = 0;
        private double _riskDollars = 0;
        private int    _stopTicks   = 0;

        // UI
        private bool       _mouseSubscribed = false;
        private bool       _submitFlash     = false;
        private DateTime   _flashEnd        = DateTime.MinValue;
        private RectangleF _panelRect;

        private RectangleF _btnSubmit;
        private RectangleF _btnInstPrev,  _btnInstNext;
        private RectangleF _btnDirToggle;
        private RectangleF _btnGradePrev, _btnGradeNext;
        private RectangleF _btnArchPrev,  _btnArchNext;

        private static readonly TimeZoneInfo PacificTz = BuildPacificTz();
        private static TimeZoneInfo BuildPacificTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        }

        public IOFGatePanel()
        {
            Name           = "IOF Gate Panel";
            Description    = "IOF pre-trade safety gate.";
            SeparateWindow = true;
        }

        protected override void OnInit()
        {
            AddLineSeries("IOF Gate Panel", Color.FromArgb(1, 128, 128, 128), 1, LineStyle.Solid);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            SetValue(double.NaN);
            UpdateDailyTracking();
            AutoSyncFromPendingOrders();
            UpdateCalculations();
        }

        // ─────────────────────────────────────────────────────────────

        private void UpdateDailyTracking()
        {
            var today = DateTime.UtcNow.Date;
            if (_lastTrackDate != today)
            {
                _tradesToday       = 0;
                _dailyPnL          = 0;
                _sessionHighEquity = 0;
                _lastTrackDate     = today;
            }
            var acct = GetAccount();
            if (acct == null) return;
            if (_sessionHighEquity <= 0) _sessionHighEquity = acct.Balance;
            _dailyPnL = acct.Balance - _sessionHighEquity;
        }

        private void AutoSyncFromPendingOrders()
        {
            if (ManualEntryPrice > 0) _entryPrice = ManualEntryPrice;
            if (ManualStopPrice  > 0) _stopPrice  = ManualStopPrice;

            try
            {
                string inst = Instruments[_instIdx];
                var    acct = GetAccount();

                Order    best     = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (var o in Core.Instance.Orders)
                {
                    if (o == null || o.Symbol == null) continue;
                    if (!o.Symbol.Name.StartsWith(inst, StringComparison.OrdinalIgnoreCase)) continue;
                    if (acct != null && o.Account?.Id != acct.Id) continue;
                    if (o.Status != OrderStatus.Opened && o.Status != OrderStatus.Unspecified) continue;
                    if (!string.Equals(o.OrderTypeId, "Limit", StringComparison.OrdinalIgnoreCase)) continue;
                    if (o.LastUpdateTime > bestTime) { bestTime = o.LastUpdateTime; best = o; }
                }

                if (best == null) return;

                if (best.Price > 0 && ManualEntryPrice <= 0)
                {
                    _entryPrice = best.Price;
                    _isLong     = best.Side == Side.Buy;
                }
                if (best.StopLoss?.Price > 0 && ManualStopPrice <= 0)
                    _stopPrice = best.StopLoss.Price;
                if (best.TotalQuantity > 0)
                    _contracts = Math.Max(1, Math.Min(10, (int)best.TotalQuantity));
            }
            catch { }
        }

        private void UpdateCalculations()
        {
            _rrRatio = 0; _riskDollars = 0; _stopTicks = 0;

            if (_entryPrice <= 0 || _stopPrice <= 0) { _contracts = 1; return; }

            double tSz  = GetTickSize();
            double tVal = GetTickValue();
            if (tSz <= 0 || tVal <= 0) return;

            double risk   = Math.Abs(_entryPrice - _stopPrice);
            _targetPrice  = _isLong ? _entryPrice + 2.0 * risk : _entryPrice - 2.0 * risk;
            double reward = Math.Abs(_targetPrice - _entryPrice);

            _stopTicks   = (int)Math.Round(risk / tSz);
            _rrRatio     = risk > 0 ? reward / risk : 0;

            double riskPct = _gradeIdx == 0 ? 0.01 : 0.005;
            var    acct    = GetAccount();
            double balance = acct?.Balance ?? 25000;
            double riskPerCt = _stopTicks * tVal;

            _contracts   = riskPerCt > 0
                ? Math.Max(1, Math.Min(10, (int)Math.Floor(balance * riskPct / riskPerCt)))
                : 1;
            _riskDollars = _contracts * riskPerCt;
        }

        private (bool ok, bool hard, string reason) EvaluateGates()
        {
            var pt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificTz);
            var tod = pt.TimeOfDay;

            if (tod < new TimeSpan(6, 0, 0) || tod > new TimeSpan(8, 30, 0))
                return (false, true, $"Outside session  {pt:HH:mm} PT");

            if (_tradesToday >= 2)
                return (false, true, "2-trade daily cap reached");

            if (_sessionHighEquity > 0 && (-_dailyPnL / _sessionHighEquity * 100) >= 2.0)
                return (false, true, "Daily 2% loss limit hit");

            if (_gradeIdx == 2)
                return (false, true, "C-grade — no trade");

            if (_entryPrice <= 0 || _stopPrice <= 0)
                return (false, false, "Place limit in DOM — prices auto-fill");

            if (_isLong  && _stopPrice >= _entryPrice)
                return (false, false, "Stop must be below entry for long");
            if (!_isLong && _stopPrice <= _entryPrice)
                return (false, false, "Stop must be above entry for short");

            if (_rrRatio < 2.0)
                return (false, false, $"R:R {_rrRatio:F2} — need 2.0+");

            return (true, false, "");
        }

        private void SubmitOrder()
        {
            var (ok, _, _) = EvaluateGates();
            if (!ok) return;

            var acct = GetAccount();
            if (acct == null) return;
            var sym = GetSymbol();
            if (sym == null) return;

            var entrySide = _isLong ? Side.Buy  : Side.Sell;
            var exitSide  = _isLong ? Side.Sell : Side.Buy;

            bool alreadyPending = Core.Instance.Orders.Any(o =>
                o != null && o.Symbol != null &&
                o.Symbol.Name.StartsWith(Instruments[_instIdx], StringComparison.OrdinalIgnoreCase) &&
                o.Account?.Id == acct.Id &&
                o.Status == OrderStatus.Opened &&
                string.Equals(o.OrderTypeId, "Limit", StringComparison.OrdinalIgnoreCase) &&
                o.Side == entrySide);

            if (!alreadyPending)
            {
                var r = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Symbol = sym, Account = acct, Side = entrySide,
                    OrderTypeId = "Limit", Price = _entryPrice,
                    Quantity = _contracts, TimeInForce = TimeInForce.Day
                });
                if (r.Status != TradingOperationResultStatus.Success) return;
            }

            Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = sym, Account = acct, Side = exitSide,
                OrderTypeId = "Stop", TriggerPrice = _stopPrice,
                Quantity = _contracts, TimeInForce = TimeInForce.GTC
            });

            Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol = sym, Account = acct, Side = exitSide,
                OrderTypeId = "Limit", Price = _targetPrice,
                Quantity = _contracts, TimeInForce = TimeInForce.GTC
            });

            _tradesToday++;
            _submitFlash = true;
            _flashEnd    = DateTime.UtcNow.AddSeconds(4);
        }

        // ── Paint ─────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            if (!_mouseSubscribed && CurrentChart != null)
            {
                CurrentChart.MouseClick += OnMouseClick;
                _mouseSubscribed = true;
            }

            var   g = args.Graphics;
            _panelRect = args.Rectangle;
            float x = _panelRect.X + 8;
            float y = _panelRect.Y + 6;
            float w = _panelRect.Width - 16;

            try
            {
                using var bg = new SolidBrush(Color.FromArgb(245, 12, 12, 17));
                g.FillRectangle(bg, _panelRect.X, _panelRect.Y, _panelRect.Width, _panelRect.Height);
            }
            catch { }

            var  ptNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificTz);
            var  tod   = ptNow.TimeOfDay;
            bool inWin = tod >= new TimeSpan(6, 0, 0) && tod <= new TimeSpan(8, 30, 0);
            bool preSes = tod < new TimeSpan(6, 0, 0);

            y = PaintStatusLine(g, x, y, w, ptNow, inWin);

            if (preSes)
                PaintPreSession(g, x, y, w, ptNow);
            else
                PaintSession(g, x, y, w);
        }

        // ── Status line ───────────────────────────────────────────────

        private float PaintStatusLine(Graphics g, float x, float y, float w,
            DateTime ptNow, bool inWin)
        {
            try
            {
                var acct  = GetAccount();
                double bal = acct?.Balance ?? 0;

                using var f    = new Font("Arial", 8f, FontStyle.Bold);
                using var timB = new SolidBrush(Color.FromArgb(155, 155, 155));
                using var winB = new SolidBrush(inWin ? Color.LimeGreen : Color.OrangeRed);
                using var capB = new SolidBrush(_tradesToday < 2 ? Color.FromArgb(120, 195, 120) : Color.OrangeRed);
                using var balB = new SolidBrush(Color.FromArgb(95, 95, 95));

                g.DrawString($"{ptNow:HH:mm} PT", f, timB, x, y);
                g.DrawString(inWin ? "■ OPEN" : "■ CLOSED", f, winB, x + 70, y);
                g.DrawString($"{_tradesToday}/2", f, capB, x + 145, y);
                g.DrawString($"${bal:N0}", f, balB, x + 178, y);
            }
            catch { }

            return y + 16;
        }

        // ── Pre-session view (before 6:00 AM PT) ─────────────────────

        private void PaintPreSession(Graphics g, float x, float y, float w, DateTime ptNow)
        {
            Divider(g, x, y, w); y += 7;

            try
            {
                using var lf = new Font("Arial", 7f, FontStyle.Bold);
                using var lb = new SolidBrush(Color.FromArgb(80, 80, 90));
                g.DrawString("PRE-SESSION", lf, lb, x, y);
            }
            catch { }
            y += 13;

            PaintSelector(g, x, y, w, "EXIT ARCH", ExitArchs[_archIdx], Color.LightSteelBlue,
                out _btnArchPrev, out _btnArchNext);
            y += 30;

            // Countdown
            var remaining = new TimeSpan(6, 0, 0) - ptNow.TimeOfDay;
            if (remaining.TotalSeconds > 0)
            {
                try
                {
                    using var cf = new Font("Arial", 7.5f);
                    using var cb = new SolidBrush(Color.FromArgb(85, 85, 95));
                    g.DrawString($"Opens in  {remaining:mm\\:ss}", cf, cb, x, y);
                }
                catch { }
            }

            // Clear session-mode rects so they don't mis-fire
            _btnInstPrev = _btnInstNext = _btnDirToggle =
                _btnGradePrev = _btnGradeNext = _btnSubmit = default;
        }

        // ── Session view (6:00–8:30 AM PT) ───────────────────────────

        private void PaintSession(Graphics g, float x, float y, float w)
        {
            Divider(g, x, y, w); y += 7;

            // Row: Instrument | Direction | Grade
            float third = (w - 8) / 3f;
            PaintSelector(g, x,                    y, third, "INST",
                Instruments[_instIdx], Color.Cyan,
                out _btnInstPrev, out _btnInstNext);
            PaintDirToggle(g, x + third + 4,       y, third, _isLong, out _btnDirToggle);
            PaintSelector(g, x + (third + 4) * 2f, y, third, "GRADE",
                Grades[_gradeIdx],
                _gradeIdx == 0 ? Color.Gold : _gradeIdx == 1 ? Color.Orange : Color.OrangeRed,
                out _btnGradePrev, out _btnGradeNext);
            y += 34;

            // Clear pre-session rects
            _btnArchPrev = _btnArchNext = default;

            // Prices
            bool hasPrices = _entryPrice > 0 && _stopPrice > 0;
            if (!hasPrices)
            {
                try
                {
                    using var hf = new Font("Arial", 7.5f, FontStyle.Italic);
                    using var hb = new SolidBrush(Color.FromArgb(100, 100, 100));
                    g.DrawString("Place limit order in DOM — prices auto-fill", hf, hb, x, y);
                }
                catch { }
                y += 15;
            }
            else
            {
                PaintPriceLine(g, x, y, "ENTRY  ", _entryPrice, Color.FromArgb(100, 220, 100));  y += 17;
                PaintPriceLine(g, x, y, "STOP   ", _stopPrice,  Color.FromArgb(220, 80,  80));   y += 17;
                PaintPriceLine(g, x, y, "TARGET ", _targetPrice, Color.FromArgb(80, 170, 255));  y += 17;

                try
                {
                    Color rc = _rrRatio >= 2.0 ? Color.FromArgb(100, 200, 100) : Color.OrangeRed;
                    using var mf = new Font("Arial", 7.5f);
                    using var mb = new SolidBrush(rc);
                    g.DrawString($"{_contracts}ct  ·  ${_riskDollars:F0} risk  ·  {_rrRatio:F2}R", mf, mb, x, y);
                }
                catch { }
                y += 15;
            }

            y += 4;
            PaintSubmit(g, x, y, w);
        }

        // ── Submit button ─────────────────────────────────────────────

        private void PaintSubmit(Graphics g, float x, float y, float w)
        {
            bool flashDone = _submitFlash && DateTime.UtcNow > _flashEnd;
            if (flashDone) _submitFlash = false;

            var (ok, hard, reason) = EvaluateGates();

            if (!_submitFlash && !string.IsNullOrEmpty(reason))
            {
                try
                {
                    using var rf = new Font("Arial", 7.5f);
                    using var rb = new SolidBrush(hard ? Color.OrangeRed : Color.FromArgb(205, 180, 65));
                    g.DrawString($"⊘  {reason}", rf, rb, x, y);
                }
                catch { }
                y += 13;
            }

            Color  fill, border, textCol;
            string label;

            if (_submitFlash)
            {
                fill    = Color.FromArgb(200, 0, 130, 0);
                border  = Color.LimeGreen;
                textCol = Color.White;
                label   = "✓  SUBMITTED — CLOSE 1M · WATCH 15M ONLY";
            }
            else if (hard)
            {
                fill    = Color.FromArgb(45, 65, 0, 0);
                border  = Color.FromArgb(85, 130, 0, 0);
                textCol = Color.FromArgb(110, 150, 45, 45);
                label   = "BLOCKED — HARD RULE VIOLATION";
            }
            else if (!ok)
            {
                fill    = Color.FromArgb(18, 42, 42, 42);
                border  = Color.FromArgb(45, 85, 85, 85);
                textCol = Color.FromArgb(70, 125, 125, 125);
                label   = $"SUBMIT {(_isLong ? "LONG" : "SHORT")} {Instruments[_instIdx]}";
            }
            else
            {
                fill    = Color.FromArgb(150, 105, 70, 0);
                border  = Color.Gold;
                textCol = Color.Gold;
                label   = $"SUBMIT {(_isLong ? "LONG" : "SHORT")} {Instruments[_instIdx]}" +
                          $"  ·  {_contracts}ct  ·  ${_riskDollars:F0}";
            }

            var rect = new RectangleF(x, y, w, 32);
            _btnSubmit = rect;

            try
            {
                using var fb  = new SolidBrush(fill);
                using var bp  = new Pen(border, ok && !_submitFlash ? 2f : 1f);
                using var tb  = new SolidBrush(textCol);
                using var fnt = new Font("Arial", 9f, FontStyle.Bold);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.FillRectangle(fb, rect.X, rect.Y, rect.Width, rect.Height);
                g.DrawRectangle(bp, rect.X, rect.Y, rect.Width, rect.Height);
                g.DrawString(label, fnt, tb, rect, sf);
            }
            catch { }
        }

        // ── Mouse ─────────────────────────────────────────────────────

        private void OnMouseClick(object sender, ChartMouseNativeEventArgs e)
        {
            if (e == null) return;
            if (e.Button != NativeMouseButtons.Left) return;
            e.NeedRedraw = true;
            var pt = new PointF(e.X, e.Y);

            if (_btnSubmit.Contains(pt))    { SubmitOrder(); return; }
            if (_btnDirToggle.Contains(pt)) { _isLong = !_isLong; return; }

            if (_btnInstPrev.Contains(pt))
            { _instIdx = (_instIdx - 1 + Instruments.Length) % Instruments.Length; _entryPrice = _stopPrice = _targetPrice = 0; return; }
            if (_btnInstNext.Contains(pt))
            { _instIdx = (_instIdx + 1) % Instruments.Length; _entryPrice = _stopPrice = _targetPrice = 0; return; }

            if (_btnGradePrev.Contains(pt)) { _gradeIdx = (_gradeIdx - 1 + Grades.Length) % Grades.Length; return; }
            if (_btnGradeNext.Contains(pt)) { _gradeIdx = (_gradeIdx + 1) % Grades.Length; return; }
            if (_btnArchPrev.Contains(pt))  { _archIdx  = (_archIdx  - 1 + ExitArchs.Length) % ExitArchs.Length; return; }
            if (_btnArchNext.Contains(pt))  { _archIdx  = (_archIdx  + 1) % ExitArchs.Length; return; }
        }

        // ── Drawing helpers ───────────────────────────────────────────

        private void Divider(Graphics g, float x, float y, float w)
        {
            try
            {
                using var p = new Pen(Color.FromArgb(38, 55, 55, 55), 1f);
                g.DrawLine(p, x - 4, y, x + w + 4, y);
            }
            catch { }
        }

        private void PaintPriceLine(Graphics g, float x, float y, string label, double price, Color col)
        {
            try
            {
                using var lf = new Font("Arial", 7.5f, FontStyle.Bold);
                using var pf = new Font("Arial", 9.5f, FontStyle.Bold);
                using var lb = new SolidBrush(Color.FromArgb(115, 115, 115));
                using var pb = new SolidBrush(col);
                g.DrawString(label, lf, lb, x, y + 2);
                g.DrawString(price.ToString("F2"), pf, pb, x + 52, y);
            }
            catch { }
        }

        private void PaintSelector(Graphics g, float x, float y, float w,
            string label, string value, Color col,
            out RectangleF prev, out RectangleF next)
        {
            float bw = 15f;
            prev = new RectangleF(x,          y + 14, bw, 14);
            next = new RectangleF(x + w - bw, y + 14, bw, 14);

            try
            {
                using var lf  = new Font("Arial", 5.5f, FontStyle.Bold);
                using var vf  = new Font("Arial", 8.5f, FontStyle.Bold);
                using var bf  = new Font("Arial", 6.5f);
                using var lb  = new SolidBrush(Color.FromArgb(88, 88, 88));
                using var vb  = new SolidBrush(col);
                using var bb  = new SolidBrush(Color.FromArgb(28, 62, 62, 62));
                using var bp  = new Pen(Color.FromArgb(48, 88, 88, 88), 1f);
                using var arB = new SolidBrush(Color.FromArgb(155, 155, 155));
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.DrawString(label, lf, lb, x, y);
                g.DrawString(value, vf, vb, x + bw + 2, y + 13);

                g.FillRectangle(bb, prev.X, prev.Y, prev.Width, prev.Height);
                g.DrawRectangle(bp, prev.X, prev.Y, prev.Width, prev.Height);
                g.DrawString("◀", bf, arB, new RectangleF(prev.X, prev.Y, prev.Width, prev.Height), sf);

                g.FillRectangle(bb, next.X, next.Y, next.Width, next.Height);
                g.DrawRectangle(bp, next.X, next.Y, next.Width, next.Height);
                g.DrawString("▶", bf, arB, new RectangleF(next.X, next.Y, next.Width, next.Height), sf);
            }
            catch { }
        }

        private void PaintDirToggle(Graphics g, float x, float y, float w,
            bool isLong, out RectangleF rect)
        {
            rect = new RectangleF(x, y, w, 28);
            try
            {
                Color c = isLong ? Color.LimeGreen : Color.OrangeRed;
                using var fb  = new SolidBrush(Color.FromArgb(42, c.R, c.G, c.B));
                using var bp  = new Pen(Color.FromArgb(88, c.R, c.G, c.B), 1.5f);
                using var tb  = new SolidBrush(c);
                using var fnt = new Font("Arial", 8.5f, FontStyle.Bold);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.FillRectangle(fb, rect.X, rect.Y, rect.Width, rect.Height);
                g.DrawRectangle(bp, rect.X, rect.Y, rect.Width, rect.Height);
                g.DrawString(isLong ? "▲ LONG" : "▼ SHORT", fnt, tb, rect, sf);
            }
            catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private Account GetAccount()
        {
            if (!string.IsNullOrWhiteSpace(AccountId))
            {
                var m = Core.Instance.Accounts.FirstOrDefault(a => a.Id == AccountId || a.Name == AccountId);
                if (m != null) return m;
            }
            return Core.Instance.Accounts.FirstOrDefault();
        }

        private Symbol GetSymbol()
        {
            string inst = Instruments[_instIdx];
            return Core.Instance.Symbols.FirstOrDefault(s =>
                s.Name != null && s.Name.StartsWith(inst, StringComparison.OrdinalIgnoreCase));
        }

        private double GetTickSize() => Instruments[_instIdx] switch
        {
            "MES" => 0.25, "MNQ" => 0.25, "GC" => 0.10, "CL" => 0.01, _ => 0.25
        };

        private double GetTickValue() => Instruments[_instIdx] switch
        {
            "MES" => 1.25, "MNQ" => 0.50, "GC" => 10.00, "CL" => 10.00, _ => 1.25
        };
    }

    internal static class DoubleExtGate
    {
        internal static bool IsNaN(this double d) => double.IsNaN(d);
    }
}
