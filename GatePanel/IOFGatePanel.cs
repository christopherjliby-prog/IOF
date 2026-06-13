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
        // ── Settings (right-click → Settings) ────────────────────────
        [InputParameter("Account ID (leave blank for first account)", 10)]
        public string AccountId = "";

        // Emergency manual overrides — leave 0 to auto-sync from DOM
        [InputParameter("Manual Entry price  (0 = auto-sync from DOM)", 20)]
        public double ManualEntryPrice = 0.0;

        [InputParameter("Manual Stop price   (0 = auto-sync from DOM)", 30)]
        public double ManualStopPrice = 0.0;

        // ── Instruments / grades ──────────────────────────────────────
        private static readonly string[] Instruments = { "MES", "MNQ", "GC", "CL" }; // MGC excluded
        private static readonly string[] Grades      = { "A", "B", "C" };

        private int  _instIdx  = 0;
        private bool _isLong   = true;
        private int  _gradeIdx = 0;

        // ── Live prices (internal — never stored in InputParameters) ──
        private double _entryPrice  = 0;
        private double _stopPrice   = 0;
        private double _targetPrice = 0;

        // ── Checklist — only 3 critical pre-trade items ───────────────
        private readonly bool[] _check = new bool[3];
        private static readonly string[] CheckLabels =
        {
            "Zone marked pre-session  (4H IBI)",
            "1H reaction confirmed",
            "15M trigger valid  (read tape first)"
        };

        // ── Daily tracking ────────────────────────────────────────────
        private int      _tradesToday       = 0;
        private double   _sessionHighEquity = 0;
        private double   _dailyPnL          = 0;
        private DateTime _lastTrackDate     = DateTime.MinValue;

        // ── Calculated ────────────────────────────────────────────────
        private int    _contracts   = 1;
        private double _rrRatio     = 0;
        private double _riskDollars = 0;
        private int    _stopTicks   = 0;

        // ── UI state ──────────────────────────────────────────────────
        private bool       _mouseSubscribed = false;
        private bool       _submitFlash     = false;
        private DateTime   _flashEnd        = DateTime.MinValue;
        private string     _blockReason     = "";
        private bool       _hardBlock       = false;
        private RectangleF _panelRect;

        // Hit-test rects
        private RectangleF   _btnSubmit;
        private RectangleF   _btnInstPrev,  _btnInstNext;
        private RectangleF   _btnDirToggle;
        private RectangleF   _btnGradePrev, _btnGradeNext;
        private readonly RectangleF[] _cbRects = new RectangleF[3];

        // ── Pacific Time ──────────────────────────────────────────────
        private static readonly TimeZoneInfo PacificTz = BuildPacificTz();
        private static TimeZoneInfo BuildPacificTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        }

        // ─────────────────────────────────────────────────────────────
        public IOFGatePanel()
        {
            Name           = "IOF Gate Panel";
            Description    = "Pre-trade gate — enforces all IOF hard rules before order submission.";
            SeparateWindow = true;
        }

        protected override void OnInit()
        {
            // Alpha=1 invisible line — keeps indicator in legend so right-click Settings/Remove works
            AddLineSeries("IOF Gate Panel", Color.FromArgb(1, 128, 128, 128), 1, LineStyle.Solid);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            SetValue(double.NaN);
            UpdateDailyTracking();
            AutoSyncFromPendingOrders();
            UpdateCalculations();
        }

        // ── Daily tracking ────────────────────────────────────────────

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

        // ── Auto-sync prices from pending DOM limit order ─────────────

        private void AutoSyncFromPendingOrders()
        {
            // Manual overrides take priority
            if (ManualEntryPrice > 0) _entryPrice = ManualEntryPrice;
            if (ManualStopPrice  > 0) _stopPrice  = ManualStopPrice;

            try
            {
                string inst  = Instruments[_instIdx];
                var    acct  = GetAccount();

                Order newest = null;
                DateTime newestTime = DateTime.MinValue;
                foreach (var o in Core.Instance.Orders)
                {
                    if (o == null || o.Symbol == null) continue;
                    if (!o.Symbol.Name.StartsWith(inst, StringComparison.OrdinalIgnoreCase)) continue;
                    if (acct != null && o.Account?.Id != acct.Id) continue;
                    if (o.Status != OrderStatus.Opened && o.Status != OrderStatus.Unspecified) continue;
                    if (!string.Equals(o.OrderTypeId, "Limit", StringComparison.OrdinalIgnoreCase)) continue;
                    if (o.LastUpdateTime > newestTime) { newestTime = o.LastUpdateTime; newest = o; }
                }

                if (newest == null) return;

                if (newest.Price > 0 && ManualEntryPrice <= 0)
                {
                    _entryPrice = newest.Price;
                    _isLong     = newest.Side == Side.Buy;
                }

                if (newest.StopLoss?.Price > 0 && ManualStopPrice <= 0)
                    _stopPrice = newest.StopLoss.Price;

                if (newest.TotalQuantity > 0)
                    _contracts = Math.Max(1, Math.Min(10, (int)newest.TotalQuantity));
            }
            catch { }
        }

        // ── Position sizing ───────────────────────────────────────────

        private void UpdateCalculations()
        {
            _rrRatio     = 0;
            _riskDollars = 0;
            _stopTicks   = 0;

            if (_entryPrice <= 0 || _stopPrice <= 0) { _contracts = 1; return; }

            double tickSz  = GetTickSize();
            double tickVal = GetTickValue();
            if (tickSz <= 0 || tickVal <= 0) return;

            double riskPts = Math.Abs(_entryPrice - _stopPrice);

            // Auto 2:1 target — always calculated, never stored as InputParameter
            _targetPrice = _isLong
                ? _entryPrice + 2.0 * riskPts
                : _entryPrice - 2.0 * riskPts;

            double rewardPts = Math.Abs(_targetPrice - _entryPrice);

            _stopTicks   = (int)Math.Round(riskPts / tickSz);
            _rrRatio     = riskPts > 0 ? rewardPts / riskPts : 0;

            double riskPct    = _gradeIdx == 0 ? 0.01 : 0.005; // A=1%, B=0.5%
            var    acct       = GetAccount();
            double balance    = acct?.Balance ?? 25000;
            double riskTarget = balance * riskPct;
            double riskPerCt  = _stopTicks * tickVal;

            _contracts   = riskPerCt > 0
                ? Math.Max(1, Math.Min(10, (int)Math.Floor(riskTarget / riskPerCt)))
                : 1;
            _riskDollars = _contracts * riskPerCt;
        }

        // ── Gate evaluation ───────────────────────────────────────────

        private (bool passed, bool hardBlock, string reason) EvaluateAllGates()
        {
            var pt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificTz);
            var tod = pt.TimeOfDay;

            if (tod < new TimeSpan(6, 0, 0) || tod > new TimeSpan(8, 30, 0))
                return (false, true, $"Outside session  {pt:HH:mm} PT  (6:00–8:30AM)");

            if (_tradesToday >= 2)
                return (false, true, "2-trade daily cap reached");

            if (_sessionHighEquity > 0 && (-_dailyPnL / _sessionHighEquity * 100) >= 2.0)
                return (false, true, "Daily 2% loss limit hit");

            if (_gradeIdx == 2)
                return (false, true, "C-grade — no trade");

            for (int i = 0; i < _check.Length; i++)
                if (!_check[i])
                    return (false, false, CheckLabels[i]);

            if (_entryPrice <= 0 || _stopPrice <= 0)
                return (false, false, "Place limit order in DOM first");

            if (_isLong  && _stopPrice >= _entryPrice)
                return (false, false, "Stop must be below entry for long");
            if (!_isLong && _stopPrice <= _entryPrice)
                return (false, false, "Stop must be above entry for short");

            if (_rrRatio < 2.0)
                return (false, false, $"R:R {_rrRatio:F2} — minimum 2.0 required");

            return (true, false, "");
        }

        // ── Order submission ──────────────────────────────────────────

        private void SubmitOrder()
        {
            var (passed, _, _) = EvaluateAllGates();
            if (!passed) return;

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
                    Symbol      = sym,
                    Account     = acct,
                    Side        = entrySide,
                    OrderTypeId = "Limit",
                    Price       = _entryPrice,
                    Quantity    = _contracts,
                    TimeInForce = TimeInForce.Day
                });
                if (r.Status != TradingOperationResultStatus.Success) return;
            }

            Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol       = sym,
                Account      = acct,
                Side         = exitSide,
                OrderTypeId  = "Stop",
                TriggerPrice = _stopPrice,
                Quantity     = _contracts,
                TimeInForce  = TimeInForce.GTC
            });

            Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol      = sym,
                Account     = acct,
                Side        = exitSide,
                OrderTypeId = "Limit",
                Price       = _targetPrice,
                Quantity    = _contracts,
                TimeInForce = TimeInForce.GTC
            });

            _tradesToday++;
            _submitFlash = true;
            _flashEnd    = DateTime.UtcNow.AddSeconds(3);
            for (int i = 0; i < _check.Length; i++) _check[i] = false;
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

        // ── Paint ─────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            if (!_mouseSubscribed && CurrentChart != null)
            {
                CurrentChart.MouseClick += OnMouseClick;
                _mouseSubscribed = true;
            }

            var   g  = args.Graphics;
            _panelRect = args.Rectangle;
            float x  = _panelRect.X + 8;
            float y  = _panelRect.Y + 8;
            float w  = _panelRect.Width - 16;

            var (gatePass, hardBlock, blockReason) = EvaluateAllGates();
            _blockReason = blockReason;
            _hardBlock   = hardBlock;

            // Background
            try
            {
                using var bg = new SolidBrush(Color.FromArgb(245, 12, 12, 17));
                g.FillRectangle(bg, _panelRect.X, _panelRect.Y, _panelRect.Width, _panelRect.Height);
            }
            catch { }

            var  ptNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificTz);
            var  tod   = ptNow.TimeOfDay;
            bool inWin = tod >= new TimeSpan(6, 0, 0) && tod <= new TimeSpan(8, 30, 0);

            y = PaintStatus(g, x, y, w, ptNow, inWin);
            y = PaintGateChips(g, x, y, w, inWin);
            y = PaintChecklist(g, x, y, w);
            y = PaintOrder(g, x, y, w);
            PaintSubmit(g, x, y, w, gatePass, hardBlock, blockReason);
        }

        // ─────────────────────────────────────────────────────────────

        private float PaintStatus(Graphics g, float x, float y, float w, DateTime ptNow, bool inWin)
        {
            PaintHeader(g, x, y, w, "STATUS");
            y += 17;

            try
            {
                using var bf  = new Font("Arial", 8.5f, FontStyle.Bold);
                using var sf2 = new Font("Arial", 7.5f);

                using var timeBr = new SolidBrush(Color.FromArgb(170, 170, 170));
                using var winBr  = new SolidBrush(inWin ? Color.LimeGreen : Color.OrangeRed);
                using var capBr  = new SolidBrush(_tradesToday < 2 ? Color.FromArgb(130, 210, 130) : Color.OrangeRed);

                g.DrawString($"{ptNow:HH:mm:ss} PT", bf, timeBr, x, y);
                g.DrawString(inWin ? "■ WINDOW OPEN" : "■ CLOSED", bf, winBr, x + 105, y);
                g.DrawString($"TRADES  {_tradesToday}/2", bf, capBr, x + w - 75, y);
                y += 15;

                var    acct  = GetAccount();
                double bal   = acct?.Balance ?? 0;
                double maxL  = _sessionHighEquity > 0 ? _sessionHighEquity * 0.02 : bal * 0.02;

                using var grayBr = new SolidBrush(Color.FromArgb(130, 130, 130));
                using var pnlBr  = new SolidBrush(_dailyPnL >= 0 ? Color.FromArgb(110, 190, 110) : Color.FromArgb(210, 110, 110));

                g.DrawString($"${bal:N0}", sf2, grayBr, x, y);
                g.DrawString($"P&L  {_dailyPnL:+$0;-$0}", sf2, pnlBr, x + 75, y);
                g.DrawString($"Max Loss  -${maxL:N0}", sf2, grayBr, x + 165, y);
            }
            catch { }

            return y + 17;
        }

        private float PaintGateChips(Graphics g, float x, float y, float w, bool inWin)
        {
            PaintHeader(g, x, y, w, "AUTO GATES");
            y += 17;

            var    acct  = GetAccount();
            double bal   = acct?.Balance ?? 0;
            bool   lossOk = _sessionHighEquity <= 0 || (-_dailyPnL / _sessionHighEquity * 100) < 2.0;

            (bool ok, string label)[] chips =
            {
                (inWin,            "Window"),
                (_tradesToday < 2, "Trade Cap"),
                (lossOk,           "Loss 2%"),
                (_gradeIdx < 2,    "Grade")
            };

            float chipW = (w - (chips.Length - 1) * 4) / chips.Length;
            float cx    = x;

            foreach (var (ok, label) in chips)
            {
                try
                {
                    using var fb  = new SolidBrush(ok ? Color.FromArgb(28, 0, 80, 0)  : Color.FromArgb(45, 80, 0, 0));
                    using var bp  = new Pen(ok ? Color.FromArgb(55, 0, 150, 0) : Color.FromArgb(90, 180, 0, 0), 1f);
                    using var tb  = new SolidBrush(ok ? Color.FromArgb(90, 190, 90) : Color.OrangeRed);
                    using var fnt = new Font("Arial", 7f, FontStyle.Bold);
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    var r = new RectangleF(cx, y, chipW, 16);
                    g.FillRectangle(fb, r.X, r.Y, r.Width, r.Height);
                    g.DrawRectangle(bp, r.X, r.Y, r.Width, r.Height);
                    g.DrawString((ok ? "✓ " : "✗ ") + label, fnt, tb, r, sf);
                }
                catch { }
                cx += chipW + 4;
            }

            return y + 22;
        }

        private float PaintChecklist(Graphics g, float x, float y, float w)
        {
            PaintHeader(g, x, y, w, "PRE-TRADE — CHECK ALL 3 TO UNLOCK");
            y += 17;

            for (int i = 0; i < CheckLabels.Length; i++)
            {
                var row = new RectangleF(x - 4, y, w + 8, 28);
                _cbRects[i] = row;
                PaintCheckRow(g, row, _check[i], CheckLabels[i]);
                y += 32;
            }

            return y + 4;
        }

        private void PaintCheckRow(Graphics g, RectangleF r, bool chk, string label)
        {
            try
            {
                Color bg     = chk ? Color.FromArgb(38, 0, 120, 0)  : Color.FromArgb(48, 100, 0, 0);
                Color border = chk ? Color.FromArgb(80, 0, 200, 0)  : Color.FromArgb(100, 210, 40, 40);
                Color accent = chk ? Color.FromArgb(210, 0, 210, 0) : Color.FromArgb(210, 220, 40, 40);
                Color txt    = chk ? Color.FromArgb(155, 230, 155)  : Color.FromArgb(240, 165, 165);

                using var bgBr  = new SolidBrush(bg);
                using var bPen  = new Pen(border, 1f);
                using var acBr  = new SolidBrush(accent);
                using var tBr   = new SolidBrush(txt);
                using var font  = new Font("Arial", 8.5f, FontStyle.Bold);
                var sf = new StringFormat { LineAlignment = StringAlignment.Center };

                g.FillRectangle(bgBr, r.X, r.Y, r.Width, r.Height);
                g.DrawRectangle(bPen, r.X, r.Y, r.Width, r.Height);
                g.FillRectangle(acBr, r.X, r.Y, 4, r.Height);   // left accent bar

                // Checkbox square
                float cbX = r.X + 10;
                float cbY = r.Y + (r.Height - 14) / 2f;
                using var cbFill = new SolidBrush(chk ? Color.FromArgb(80, 0, 200, 0) : Color.FromArgb(25, 100, 0, 0));
                using var cbPen  = new Pen(border, 1f);
                g.FillRectangle(cbFill, cbX, cbY, 14, 14);
                g.DrawRectangle(cbPen, cbX, cbY, 14, 14);

                if (chk)
                {
                    using var ckBr = new SolidBrush(Color.LimeGreen);
                    using var ckF  = new Font("Arial", 8f, FontStyle.Bold);
                    g.DrawString("✓", ckF, ckBr, cbX + 1, cbY);
                }

                g.DrawString(label, font, tBr, new RectangleF(r.X + 30, r.Y, r.Width - 34, r.Height), sf);
            }
            catch { }
        }

        private float PaintOrder(Graphics g, float x, float y, float w)
        {
            PaintHeader(g, x, y, w, "ORDER");
            y += 17;

            float third = (w - 8) / 3f;

            // Instrument | Direction | Grade — three equal columns
            PaintSmallSelector(g, x,                    y, third, "INSTRUMENT", Instruments[_instIdx], Color.Cyan,
                out _btnInstPrev, out _btnInstNext);
            PaintDirToggle(g, x + third + 4,            y, third, _isLong, out _btnDirToggle);
            PaintSmallSelector(g, x + (third + 4) * 2f, y, third, "GRADE",
                Grades[_gradeIdx],
                _gradeIdx == 0 ? Color.Gold : _gradeIdx == 1 ? Color.Orange : Color.OrangeRed,
                out _btnGradePrev, out _btnGradeNext);
            y += 36;

            // Prices
            bool hasPrices = _entryPrice > 0 && _stopPrice > 0;

            if (!hasPrices)
            {
                try
                {
                    using var hf = new Font("Arial", 8f, FontStyle.Italic);
                    using var hb = new SolidBrush(Color.FromArgb(130, 130, 130));
                    g.DrawString("↑  Place limit order in DOM — prices auto-fill here", hf, hb, x, y);
                    g.DrawString("   or: right-click panel → Settings to set manually", hf, hb, x, y + 14);
                }
                catch { }
                y += 34;
            }
            else
            {
                PaintPriceLine(g, x, y, w, "ENTRY ", _entryPrice, Color.FromArgb(100, 220, 100), "");   y += 22;
                PaintPriceLine(g, x, y, w, "STOP  ", _stopPrice,  Color.FromArgb(220, 80,  80),  "");   y += 22;
                PaintPriceLine(g, x, y, w, "TARGET", _targetPrice, Color.FromArgb(80, 170, 255), "auto 2:1"); y += 22;

                try
                {
                    Color rrCol = _rrRatio >= 2.0 ? Color.FromArgb(100, 220, 100) : Color.OrangeRed;
                    using var mf = new Font("Arial", 8f);
                    using var mb = new SolidBrush(rrCol);
                    g.DrawString($"{_contracts} contracts  ·  ${_riskDollars:F0} risk  ·  {_rrRatio:F2}R",
                        mf, mb, x, y);
                }
                catch { }
                y += 18;
            }

            return y + 6;
        }

        private void PaintPriceLine(Graphics g, float x, float y, float w,
            string label, double price, Color priceCol, string suffix)
        {
            try
            {
                using var lf  = new Font("Arial", 7.5f, FontStyle.Bold);
                using var pf  = new Font("Arial", 10f, FontStyle.Bold);
                using var sf2 = new Font("Arial", 6.5f, FontStyle.Italic);
                using var lBr = new SolidBrush(Color.FromArgb(130, 130, 130));
                using var pBr = new SolidBrush(price > 0 ? priceCol : Color.FromArgb(70, 110, 110, 110));
                using var sBr = new SolidBrush(Color.FromArgb(90, 90, 90));

                g.DrawString(label, lf, lBr, x, y + 3);
                g.DrawString(price > 0 ? price.ToString("F2") : "—", pf, pBr, x + 55, y);
                if (!string.IsNullOrEmpty(suffix) && price > 0)
                    g.DrawString(suffix, sf2, sBr, x + 150, y + 6);
            }
            catch { }
        }

        private void PaintSubmit(Graphics g, float x, float y, float w,
            bool gatePass, bool hardBlock, string blockReason)
        {
            bool flashDone = _submitFlash && DateTime.UtcNow > _flashEnd;
            if (flashDone) _submitFlash = false;

            // Block reason line
            if (!_submitFlash && !string.IsNullOrEmpty(blockReason))
            {
                try
                {
                    using var rf = new Font("Arial", 7.5f, FontStyle.Bold);
                    using var rb = new SolidBrush(hardBlock ? Color.OrangeRed : Color.FromArgb(230, 200, 80));
                    g.DrawString($"⊘  {blockReason}", rf, rb, x, y);
                }
                catch { }
                y += 14;
            }

            Color  fill, border, textCol;
            string label;

            if (_submitFlash)
            {
                fill    = Color.FromArgb(200, 0, 150, 0);
                border  = Color.LimeGreen;
                textCol = Color.White;
                label   = "✓  ORDER SUBMITTED — WATCH 15M ONLY";
            }
            else if (hardBlock)
            {
                fill    = Color.FromArgb(55, 80, 0, 0);
                border  = Color.FromArgb(90, 160, 0, 0);
                textCol = Color.FromArgb(130, 170, 55, 55);
                label   = "SUBMIT BLOCKED — HARD RULE VIOLATION";
            }
            else if (!gatePass)
            {
                fill    = Color.FromArgb(25, 50, 50, 50);
                border  = Color.FromArgb(55, 95, 95, 95);
                textCol = Color.FromArgb(85, 140, 140, 140);
                label   = $"SUBMIT {(_isLong ? "LONG" : "SHORT")} {Instruments[_instIdx]}";
            }
            else
            {
                fill    = Color.FromArgb(160, 110, 75, 0);
                border  = Color.Gold;
                textCol = Color.Gold;
                label   = $"SUBMIT {(_isLong ? "LONG" : "SHORT")} {Instruments[_instIdx]}" +
                          $"  ·  {_contracts}ct  ·  ${_riskDollars:F0}  ·  {_rrRatio:F1}R";
            }

            var rect = new RectangleF(x, y, w, 36);
            _btnSubmit = rect;

            try
            {
                using var fb  = new SolidBrush(fill);
                using var bp  = new Pen(border, gatePass && !_submitFlash ? 2f : 1f);
                using var tb  = new SolidBrush(textCol);
                using var fnt = new Font("Arial", 9f, FontStyle.Bold);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.FillRectangle(fb, rect.X, rect.Y, rect.Width, rect.Height);
                g.DrawRectangle(bp, rect.X, rect.Y, rect.Width, rect.Height);
                g.DrawString(label, fnt, tb, rect, sf);
            }
            catch { }
        }

        // ── Mouse handler ─────────────────────────────────────────────

        private void OnMouseClick(object sender, ChartMouseNativeEventArgs e)
        {
            if (e == null) return;
            if (e.Button != NativeMouseButtons.Left) return;
            e.NeedRedraw = true;
            var pt = new PointF(e.X, e.Y);

            if (_btnSubmit.Contains(pt)) { SubmitOrder(); return; }

            if (_btnInstPrev.Contains(pt))
            {
                _instIdx    = (_instIdx - 1 + Instruments.Length) % Instruments.Length;
                _entryPrice = _stopPrice = _targetPrice = 0;
                return;
            }
            if (_btnInstNext.Contains(pt))
            {
                _instIdx    = (_instIdx + 1) % Instruments.Length;
                _entryPrice = _stopPrice = _targetPrice = 0;
                return;
            }

            if (_btnDirToggle.Contains(pt))  { _isLong   = !_isLong;  return; }
            if (_btnGradePrev.Contains(pt))  { _gradeIdx = (_gradeIdx - 1 + Grades.Length) % Grades.Length; return; }
            if (_btnGradeNext.Contains(pt))  { _gradeIdx = (_gradeIdx + 1) % Grades.Length; return; }

            for (int i = 0; i < _cbRects.Length; i++)
            {
                if (_cbRects[i].Contains(pt)) { _check[i] = !_check[i]; return; }
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────

        private void PaintHeader(Graphics g, float x, float y, float w, string title)
        {
            try
            {
                using var br  = new SolidBrush(Color.FromArgb(32, 42, 62));
                using var ln  = new Pen(Color.FromArgb(45, 65, 95), 1f);
                using var fnt = new Font("Arial", 6.5f, FontStyle.Bold);
                using var tbr = new SolidBrush(Color.FromArgb(110, 125, 145));
                g.FillRectangle(br, x - 4, y, w + 8, 14);
                g.DrawLine(ln, x - 4, y + 14, x + w + 4, y + 14);
                g.DrawString(title, fnt, tbr, x, y + 1);
            }
            catch { }
        }

        private void PaintSmallSelector(Graphics g, float x, float y, float w,
            string label, string value, Color valueCol,
            out RectangleF prev, out RectangleF next)
        {
            float bw = 15;
            prev = new RectangleF(x,      y + 15, bw, 14);
            next = new RectangleF(x + w - bw, y + 15, bw, 14);

            try
            {
                using var lf  = new Font("Arial", 6f,   FontStyle.Bold);
                using var vf  = new Font("Arial", 9f,   FontStyle.Bold);
                using var bf  = new Font("Arial", 6.5f, FontStyle.Bold);
                using var lBr = new SolidBrush(Color.FromArgb(105, 105, 105));
                using var vBr = new SolidBrush(valueCol);
                using var bBr = new SolidBrush(Color.FromArgb(35, 70, 70, 70));
                using var pen = new Pen(Color.FromArgb(55, 100, 100, 100), 1f);
                using var tBr = new SolidBrush(Color.LightGray);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.DrawString(label, lf, lBr, x, y);
                g.DrawString(value, vf, vBr, x + bw + 2, y + 14);

                g.FillRectangle(bBr, prev.X, prev.Y, prev.Width, prev.Height);
                g.DrawRectangle(pen, prev.X, prev.Y, prev.Width, prev.Height);
                g.DrawString("◀", bf, tBr, new RectangleF(prev.X, prev.Y, prev.Width, prev.Height), sf);

                g.FillRectangle(bBr, next.X, next.Y, next.Width, next.Height);
                g.DrawRectangle(pen, next.X, next.Y, next.Width, next.Height);
                g.DrawString("▶", bf, tBr, new RectangleF(next.X, next.Y, next.Width, next.Height), sf);
            }
            catch { }
        }

        private void PaintDirToggle(Graphics g, float x, float y, float w,
            bool isLong, out RectangleF toggleRect)
        {
            toggleRect = new RectangleF(x, y, w, 29);
            try
            {
                Color col = isLong ? Color.LimeGreen : Color.OrangeRed;
                using var fb  = new SolidBrush(Color.FromArgb(50, col.R, col.G, col.B));
                using var bp  = new Pen(Color.FromArgb(100, col.R, col.G, col.B), 1.5f);
                using var tb  = new SolidBrush(col);
                using var fnt = new Font("Arial", 9f, FontStyle.Bold);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.FillRectangle(fb, toggleRect.X, toggleRect.Y, toggleRect.Width, toggleRect.Height);
                g.DrawRectangle(bp, toggleRect.X, toggleRect.Y, toggleRect.Width, toggleRect.Height);
                g.DrawString(isLong ? "▲ LONG" : "▼ SHORT", fnt, tb, toggleRect, sf);
            }
            catch { }
        }
    }

    internal static class DoubleExtGate
    {
        internal static bool IsNaN(this double d) => double.IsNaN(d);
    }
}
