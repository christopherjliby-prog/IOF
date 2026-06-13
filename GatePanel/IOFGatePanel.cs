using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;
using TradingPlatform.BusinessLayer.Native;

namespace IOF_GatePanel
{
    public class IOFGatePanel : Indicator
    {
        // ── Settings (set via right-click → Settings) ────────────────
        [InputParameter("Entry price", 10)]
        public double EntryPrice = 0.0;

        [InputParameter("Stop price", 20)]
        public double StopPrice = 0.0;

        [InputParameter("Target price", 30)]
        public double TargetPrice = 0.0;

        [InputParameter("Account ID (leave blank for first account)", 40)]
        public string AccountId = "";

        // ── Instrument / direction / grade ───────────────────────────
        private static readonly string[] Instruments  = { "MES", "MNQ", "GC", "CL" }; // MGC excluded (hard rule)
        private static readonly string[] Grades       = { "A", "B", "C" };
        private static readonly string[] ZoneTypes    = { "RBR", "DBR" };
        private static readonly string[] ExitArchs    = { "Single→LVN", "Split", "Scale", "Trail" };

        private int  _instIdx     = 0;
        private bool _isLong      = true;
        private int  _gradeIdx    = 0;
        private int  _zoneIdx     = 0;
        private int  _exitArchIdx = 0;
        private bool _isPreNY     = false;
        private bool _isReEntry   = false;

        // ── Checklist ────────────────────────────────────────────────
        private readonly bool[] _check = new bool[11];
        private static readonly string[] CheckLabels = {
            "Zone marked pre-session (4H IBI)",
            "HVN / POC alignment present",
            "LVN target → min 2R confirmed",
            "1H reaction confirmed",
            "15M trigger valid (read tape first)",
            "5M stacked imbalances present",
            "Bias alignment confirmed",
            "Phase 3 exhaustion visible",
            "Phase 4 aggression flip on 1M CLOSE",
            "Absorption failure checked",
            "Exit architecture declared"
        };
        private static readonly bool[] CheckCritical = {
            true, false, false, true, true, false, false, false, false, false, false
        };

        // ── Daily tracking ───────────────────────────────────────────
        private int      _tradesToday      = 0;
        private double   _sessionHighEquity = 0;
        private double   _dailyPnL         = 0;
        private DateTime _lastTrackDate    = DateTime.MinValue;

        // ── Auto-calculated ──────────────────────────────────────────
        private int    _contracts   = 1;
        private double _rrRatio     = 0;
        private double _riskDollars = 0;
        private int    _stopTicks   = 0;

        // ── UI state ─────────────────────────────────────────────────
        private bool     _mouseSubscribed = false;
        private bool     _submitFlash     = false;
        private DateTime _flashEnd        = DateTime.MinValue;
        private string   _blockReason     = "";
        private RectangleF _panelRect;

        // ── Hit-test rectangles (set during paint) ───────────────────
        private RectangleF   _btnSubmit;
        private RectangleF   _btnInstPrev,  _btnInstNext;
        private RectangleF   _btnDirToggle;
        private RectangleF   _btnGradePrev, _btnGradeNext;
        private RectangleF   _btnZonePrev,  _btnZoneNext;
        private RectangleF   _btnArchPrev,  _btnArchNext;
        private RectangleF   _btnWinToggle;
        private RectangleF   _btnReEntry;
        private RectangleF   _btnEMinus,  _btnEPlus;
        private RectangleF   _btnSLMinus, _btnSLPlus;
        private RectangleF   _btnTPMinus, _btnTPPlus;
        private readonly RectangleF[] _cbRects = new RectangleF[11];

        // ── Pacific Time zone ────────────────────────────────────────
        private static readonly TimeZoneInfo PacificTz = GetPacificTz();
        private static TimeZoneInfo GetPacificTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        }

        // ── Constructor ──────────────────────────────────────────────
        public IOFGatePanel()
        {
            Name           = "IOF Gate Panel";
            Description    = "Pre-trade gate — enforces all IOF hard rules before order submission.";
            SeparateWindow = true;
        }

        protected override void OnInit()
        {
            // Alpha=1: invisible but registers legend entry so right-click Settings/Remove works
            AddLineSeries("IOF Gate Panel", Color.FromArgb(1, 128, 128, 128), 1, LineStyle.Solid);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            SetValue(double.NaN);
            UpdateDailyTracking();
            UpdateCalculations();
        }

        // ── Daily tracking ───────────────────────────────────────────

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

            var account = GetAccount();
            if (account == null) return;
            if (_sessionHighEquity <= 0) _sessionHighEquity = account.Balance;
            _dailyPnL = account.Balance - _sessionHighEquity;
        }

        // ── Position sizing ──────────────────────────────────────────

        private void UpdateCalculations()
        {
            _contracts   = 1;
            _rrRatio     = 0;
            _riskDollars = 0;
            _stopTicks   = 0;

            if (EntryPrice <= 0 || StopPrice <= 0 || TargetPrice <= 0) return;

            double tickSz  = GetTickSize();
            double tickVal = GetTickValue();
            if (tickSz <= 0 || tickVal <= 0) return;

            double riskPts   = Math.Abs(EntryPrice - StopPrice);
            double rewardPts = Math.Abs(TargetPrice - EntryPrice);

            _stopTicks = (int)Math.Round(riskPts / tickSz);
            _rrRatio   = riskPts > 0 ? rewardPts / riskPts : 0;

            // Grade A = 1%, Grade B = 0.5%
            double riskPct = _gradeIdx == 0 ? 0.01 : 0.005;
            var    account  = GetAccount();
            double balance  = account?.Balance ?? 25406;

            double riskTarget      = balance * riskPct;
            double riskPerContract = _stopTicks * tickVal;

            _contracts   = riskPerContract > 0
                ? Math.Max(1, Math.Min(10, (int)Math.Floor(riskTarget / riskPerContract)))
                : 1;
            _riskDollars = _contracts * riskPerContract;
        }

        // ── Gate evaluation ──────────────────────────────────────────

        private (bool passed, bool hardBlock, string reason) EvaluateAllGates()
        {
            var pt  = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificTz);
            var tod = pt.TimeOfDay;

            // 1. Session window 6:00–8:30AM PT
            if (tod < new TimeSpan(6, 0, 0) || tod > new TimeSpan(8, 30, 0))
                return (false, true, $"Outside session window — PT {pt:HH:mm}");

            // 2. Daily trade cap
            if (_tradesToday >= 2)
                return (false, true, "2-trade daily cap reached.");

            // 3. Daily loss limit 2%
            if (_sessionHighEquity > 0)
            {
                double ddPct = -_dailyPnL / _sessionHighEquity * 100;
                if (ddPct >= 2.0)
                    return (false, true, $"Daily loss limit hit: {ddPct:F2}% drawdown.");
            }

            // 4. C-grade blocked
            if (_gradeIdx == 2)
                return (false, true, "C-grade setup — no trade.");

            // 5. Re-entry time gate (6:45AM PT minimum)
            if (_isReEntry && tod < new TimeSpan(6, 45, 0))
                return (false, true, $"Re-entry blocked until 6:45AM PT — now {pt:HH:mm} PT.");

            // 6. Critical checklist items
            for (int i = 0; i < _check.Length; i++)
            {
                if (CheckCritical[i] && !_check[i])
                    return (false, false, $"Critical: {CheckLabels[i]}");
            }

            // 7. Prices required
            if (EntryPrice <= 0 || StopPrice <= 0 || TargetPrice <= 0)
                return (false, false, "Entry, stop, and target required.");

            // 8. Stop direction
            if (_isLong  && StopPrice >= EntryPrice)
                return (false, false, "Stop must be BELOW entry for long.");
            if (!_isLong && StopPrice <= EntryPrice)
                return (false, false, "Stop must be ABOVE entry for short.");

            // 9. Target direction
            if (_isLong  && TargetPrice <= EntryPrice)
                return (false, false, "Target must be ABOVE entry for long.");
            if (!_isLong && TargetPrice >= EntryPrice)
                return (false, false, "Target must be BELOW entry for short.");

            // 10. Minimum 2:1 R:R
            if (_rrRatio < 2.0)
                return (false, false, $"R:R is {_rrRatio:F2} — minimum 1:2 required.");

            return (true, false, "");
        }

        // ── Order submission ─────────────────────────────────────────

        private void SubmitOrder()
        {
            var (passed, _, reason) = EvaluateAllGates();
            if (!passed) return;

            var account = GetAccount();
            if (account == null) return;

            var sym = GetSymbol();
            if (sym == null) return;

            var entrySide = _isLong ? Side.Buy : Side.Sell;
            var exitSide  = _isLong ? Side.Sell : Side.Buy;

            // Entry — limit order at EntryPrice
            var entryResult = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol      = sym,
                Account     = account,
                Side        = entrySide,
                OrderTypeId = "Limit",
                Price       = EntryPrice,
                Quantity    = _contracts,
                TimeInForce = TimeInForce.Day
            });

            if (entryResult.Status != TradingOperationResultStatus.Success) return;

            // Stop loss — GTC stop order
            Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol        = sym,
                Account       = account,
                Side          = exitSide,
                OrderTypeId   = "Stop",
                TriggerPrice  = StopPrice,
                Quantity      = _contracts,
                TimeInForce   = TimeInForce.GTC
            });

            // Target — GTC limit order
            Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
            {
                Symbol      = sym,
                Account     = account,
                Side        = exitSide,
                OrderTypeId = "Limit",
                Price       = TargetPrice,
                Quantity    = _contracts,
                TimeInForce = TimeInForce.GTC
            });

            _tradesToday++;
            _submitFlash = true;
            _flashEnd    = DateTime.UtcNow.AddSeconds(3);

            // Reset checklist for next trade
            for (int i = 0; i < _check.Length; i++) _check[i] = false;
        }

        // ── Helpers ──────────────────────────────────────────────────

        private Account GetAccount()
        {
            if (!string.IsNullOrWhiteSpace(AccountId))
            {
                var match = Core.Instance.Accounts
                    .FirstOrDefault(a => a.Id == AccountId || a.Name == AccountId);
                if (match != null) return match;
            }
            return Core.Instance.Accounts.FirstOrDefault();
        }

        private Symbol GetSymbol()
        {
            string inst = Instruments[_instIdx];
            return Core.Instance.Symbols
                .FirstOrDefault(s => s.Name != null && s.Name.StartsWith(inst, StringComparison.OrdinalIgnoreCase));
        }

        private double GetTickSize() => Instruments[_instIdx] switch
        {
            "MES" => 0.25,
            "MNQ" => 0.25,
            "GC"  => 0.10,
            "CL"  => 0.01,
            _     => 0.25
        };

        private double GetTickValue() => Instruments[_instIdx] switch
        {
            "MES" => 1.25,
            "MNQ" => 0.50,
            "GC"  => 10.00,
            "CL"  => 10.00,
            _     => 1.25
        };

        // ── Renderer ─────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            // Subscribe to mouse clicks once via IChart
            if (!_mouseSubscribed && CurrentChart != null)
            {
                CurrentChart.MouseClick += OnMouseClick;
                _mouseSubscribed = true;
            }

            var g    = args.Graphics;
            _panelRect = args.Rectangle;
            float x  = _panelRect.X + 8;
            float y  = _panelRect.Y + 8;
            float w  = _panelRect.Width - 16;

            var (gatePass, hardBlock, blockReason) = EvaluateAllGates();
            _blockReason = gatePass ? "" : blockReason;

            // ── Background ───────────────────────────────────────────
            try
            {
                using var bgBr = new SolidBrush(Color.FromArgb(245, 15, 15, 20));
                g.FillRectangle(bgBr, _panelRect.X, _panelRect.Y, _panelRect.Width, _panelRect.Height);
            }
            catch { }

            var ptNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificTz);
            var tod   = ptNow.TimeOfDay;
            bool inWindow = tod >= new TimeSpan(6, 0, 0) && tod <= new TimeSpan(8, 30, 0);

            // ── STATUS BAR ───────────────────────────────────────────
            DrawSection(g, x, y, w, "STATUS");
            y += 20;

            try
            {
                using var f  = new Font("Arial", 8.5f, FontStyle.Bold);
                using var br1 = new SolidBrush(Color.Silver);
                using var br2 = new SolidBrush(inWindow ? Color.LimeGreen : Color.OrangeRed);
                using var br3 = new SolidBrush(_tradesToday < 2 ? Color.LimeGreen : Color.OrangeRed);
                g.DrawString($"PT  {ptNow:HH:mm:ss}", f, br1, x, y);
                g.DrawString(inWindow ? "■ WINDOW OPEN" : "■ CLOSED", f, br2, x + 110, y);
                g.DrawString($"TRADES  {_tradesToday}/2", f, br3, x + 240, y);
            }
            catch { }
            y += 16;

            try
            {
                var account = GetAccount();
                double balance = account?.Balance ?? 0;
                double maxLoss = _sessionHighEquity > 0 ? _sessionHighEquity * 0.02 : balance * 0.02;
                DrawText(g, x, y, $"Balance  ${balance:N0}    Daily P&L  ${_dailyPnL:+0.00;-0.00}    Max Loss  -${maxLoss:N0}", Color.Silver, 7.5f);
            }
            catch { }
            y += 18;

            // ── AUTOMATIC GATES ───────────────────────────────────────
            DrawSection(g, x, y, w, "AUTOMATIC GATES");
            y += 20;

            var account2 = GetAccount();
            double balance2 = account2?.Balance ?? 0;
            double maxLoss2 = _sessionHighEquity > 0 ? _sessionHighEquity * 0.02 : balance2 * 0.02;
            bool lossOk = _sessionHighEquity <= 0 || (-_dailyPnL / _sessionHighEquity * 100) < 2.0;

            DrawGateRow(g, x, ref y, "Session Window",  inWindow,         !inWindow,           $"6:00–8:30AM PT — {(inWindow ? "OPEN" : "CLOSED")}");
            DrawGateRow(g, x, ref y, "Daily Trade Cap", _tradesToday < 2, _tradesToday >= 2,   $"{_tradesToday}/2 trades used today");
            DrawGateRow(g, x, ref y, "Daily Loss 2%",   lossOk,           !lossOk,             $"P&L  ${_dailyPnL:+0;-0} of max -${maxLoss2:N0}");
            DrawGateRow(g, x, ref y, "Grade Gate",      _gradeIdx < 2,    _gradeIdx == 2,      _gradeIdx == 2 ? "C-grade — no trade" : "A or B grade selected");
            if (_isReEntry)
                DrawGateRow(g, x, ref y, "Re-Entry Gate", tod >= new TimeSpan(6, 45, 0), tod < new TimeSpan(6, 45, 0), $"Min 6:45AM PT — now {ptNow:HH:mm}");
            y += 4;

            // ── MANUAL CHECKLIST ──────────────────────────────────────
            DrawSection(g, x, y, w, "MANUAL CHECKLIST  ←  click each row to check");
            y += 20;

            for (int i = 0; i < CheckLabels.Length; i++)
            {
                var cbRect = new RectangleF(x + 2, y + 2, 13, 13);
                _cbRects[i] = cbRect;
                DrawCheckbox(g, cbRect, _check[i], CheckCritical[i], CheckLabels[i]);
                y += 17;
            }
            y += 6;

            // ── ORDER PARAMETERS ──────────────────────────────────────
            DrawSection(g, x, y, w, "ORDER PARAMETERS");
            y += 20;

            float half = w / 2f - 4;

            // Row: Instrument | Direction
            DrawSelector(g, x,        y, half, "INSTRUMENT", Instruments[_instIdx], Color.Cyan,
                out _btnInstPrev, out _btnInstNext);
            DrawToggle(g, x + half + 8, y, half, "DIRECTION", _isLong ? "LONG" : "SHORT",
                _isLong ? Color.LimeGreen : Color.OrangeRed, out _btnDirToggle);
            y += 28;

            // Row: Grade | Zone Type
            DrawSelector(g, x,        y, half, "GRADE", Grades[_gradeIdx],
                _gradeIdx == 0 ? Color.Gold : _gradeIdx == 1 ? Color.Orange : Color.OrangeRed,
                out _btnGradePrev, out _btnGradeNext);
            DrawSelector(g, x + half + 8, y, half, "ZONE", ZoneTypes[_zoneIdx], Color.Plum,
                out _btnZonePrev, out _btnZoneNext);
            y += 28;

            // Price rows: Entry / Stop / Target
            DrawPriceRow(g, x, y, w, "ENTRY ", EntryPrice, Color.FromArgb(120, 230, 120), GetTickSize(), out _btnEMinus,  out _btnEPlus);  y += 22;
            DrawPriceRow(g, x, y, w, "STOP  ", StopPrice,  Color.FromArgb(230, 80,  80),  GetTickSize(), out _btnSLMinus, out _btnSLPlus); y += 22;
            DrawPriceRow(g, x, y, w, "TARGET", TargetPrice, Color.FromArgb(80, 180, 255),  GetTickSize(), out _btnTPMinus, out _btnTPPlus); y += 22;

            // Computed row
            try
            {
                DrawText(g, x, y,
                    $"Ticks: {_stopTicks}    Contracts: {_contracts}    Risk: ${_riskDollars:F0}    R:R: {_rrRatio:F2}",
                    _rrRatio >= 2.0 ? Color.FromArgb(120, 230, 120) : Color.OrangeRed, 8.5f);
            }
            catch { }
            y += 18;

            // Row: Exit arch | Session window
            DrawSelector(g, x,        y, half, "EXIT ARCH", ExitArchs[_exitArchIdx], Color.LightSteelBlue,
                out _btnArchPrev, out _btnArchNext);
            DrawToggle(g, x + half + 8, y, half, "WINDOW", _isPreNY ? "Pre-NY" : "NY Session",
                Color.LightSteelBlue, out _btnWinToggle);
            y += 28;

            // Re-entry checkbox
            var reRect = new RectangleF(x + 2, y + 2, 13, 13);
            _btnReEntry = reRect;
            DrawCheckbox(g, reRect, _isReEntry, false, "This is a re-entry after stop-out");
            y += 20;

            // ── SUBMIT AREA ───────────────────────────────────────────
            y += 6;
            try
            {
                using var pen = new Pen(Color.FromArgb(50, 80, 80, 80), 1f);
                g.DrawLine(pen, x, y, x + w, y);
            }
            catch { }
            y += 8;

            // Block reason
            if (!string.IsNullOrEmpty(_blockReason))
            {
                try
                {
                    using var font = new Font("Arial", 7.5f, FontStyle.Bold);
                    using var br   = new SolidBrush(hardBlock ? Color.OrangeRed : Color.Gold);
                    g.DrawString($"⊘  {_blockReason}", font, br, x, y);
                }
                catch { }
                y += 15;
            }

            // Submit button
            bool flashDone = _submitFlash && DateTime.UtcNow > _flashEnd;
            if (flashDone) _submitFlash = false;

            Color   btnFill, btnBorder, btnText;
            string  btnLabel;
            bool    btnEnabled;

            if (_submitFlash)
            {
                btnFill    = Color.FromArgb(200, 0, 160, 0);
                btnBorder  = Color.LimeGreen;
                btnText    = Color.White;
                btnLabel   = "✓  ORDER SUBMITTED — CLOSE THE 1M  |  WATCH 15M ONLY";
                btnEnabled = false;
            }
            else if (hardBlock)
            {
                btnFill    = Color.FromArgb(80, 100, 0, 0);
                btnBorder  = Color.FromArgb(120, 180, 0, 0);
                btnText    = Color.FromArgb(150, 200, 80, 80);
                btnLabel   = $"SUBMIT BLOCKED — HARD RULE VIOLATION";
                btnEnabled = false;
            }
            else if (!gatePass)
            {
                btnFill    = Color.FromArgb(40, 50, 50, 50);
                btnBorder  = Color.FromArgb(60, 100, 100, 100);
                btnText    = Color.FromArgb(100, 160, 160, 160);
                btnLabel   = $"SUBMIT {(_isLong ? "LONG" : "SHORT")} {Instruments[_instIdx]}  —  checklist incomplete";
                btnEnabled = false;
            }
            else
            {
                btnFill    = Color.FromArgb(160, 120, 80, 0);
                btnBorder  = Color.Gold;
                btnText    = Color.Gold;
                btnLabel   = $"SUBMIT {(_isLong ? "LONG" : "SHORT")} {Instruments[_instIdx]}  —  {_contracts}ct  @  ${_riskDollars:F0} risk  |  {_rrRatio:F1}R";
                btnEnabled = true;
            }

            var submitRect = new RectangleF(x, y, w, 34);
            _btnSubmit = submitRect;

            try
            {
                using var fillBr  = new SolidBrush(btnFill);
                using var borderPen = new Pen(btnBorder, btnEnabled ? 1.5f : 1f);
                using var textBr  = new SolidBrush(btnText);
                using var font    = new Font("Arial", 9f, FontStyle.Bold);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.FillRectangle(fillBr, submitRect.X, submitRect.Y, submitRect.Width, submitRect.Height);
                g.DrawRectangle(borderPen, submitRect.X, submitRect.Y, submitRect.Width, submitRect.Height);
                g.DrawString(btnLabel, font, textBr, new RectangleF(submitRect.X, submitRect.Y, submitRect.Width, submitRect.Height), sf);
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

            // Submit
            if (_btnSubmit.Contains(pt)) { SubmitOrder(); return; }

            // Instrument selector
            if (_btnInstPrev.Contains(pt)) { _instIdx = (_instIdx - 1 + Instruments.Length) % Instruments.Length; return; }
            if (_btnInstNext.Contains(pt)) { _instIdx = (_instIdx + 1) % Instruments.Length; return; }

            // Direction toggle
            if (_btnDirToggle.Contains(pt)) { _isLong = !_isLong; return; }

            // Grade selector
            if (_btnGradePrev.Contains(pt)) { _gradeIdx = (_gradeIdx - 1 + Grades.Length) % Grades.Length; return; }
            if (_btnGradeNext.Contains(pt)) { _gradeIdx = (_gradeIdx + 1) % Grades.Length; return; }

            // Zone type selector
            if (_btnZonePrev.Contains(pt)) { _zoneIdx = (_zoneIdx - 1 + ZoneTypes.Length) % ZoneTypes.Length; return; }
            if (_btnZoneNext.Contains(pt)) { _zoneIdx = (_zoneIdx + 1) % ZoneTypes.Length; return; }

            // Exit architecture selector
            if (_btnArchPrev.Contains(pt)) { _exitArchIdx = (_exitArchIdx - 1 + ExitArchs.Length) % ExitArchs.Length; return; }
            if (_btnArchNext.Contains(pt)) { _exitArchIdx = (_exitArchIdx + 1) % ExitArchs.Length; return; }

            // Window toggle
            if (_btnWinToggle.Contains(pt)) { _isPreNY = !_isPreNY; return; }

            // Re-entry toggle
            if (_btnReEntry.Contains(pt)) { _isReEntry = !_isReEntry; return; }

            // Price adjustment buttons
            double ts = GetTickSize();
            if (_btnEMinus.Contains(pt))  { EntryPrice  = Math.Max(0, EntryPrice  - ts); UpdateCalculations(); return; }
            if (_btnEPlus.Contains(pt))   { EntryPrice  += ts;                            UpdateCalculations(); return; }
            if (_btnSLMinus.Contains(pt)) { StopPrice   = Math.Max(0, StopPrice   - ts); UpdateCalculations(); return; }
            if (_btnSLPlus.Contains(pt))  { StopPrice   += ts;                            UpdateCalculations(); return; }
            if (_btnTPMinus.Contains(pt)) { TargetPrice  = Math.Max(0, TargetPrice - ts); UpdateCalculations(); return; }
            if (_btnTPPlus.Contains(pt))  { TargetPrice  += ts;                            UpdateCalculations(); return; }

            // Checklist checkboxes — expand hit area to full row width for easier clicking
            for (int i = 0; i < _cbRects.Length; i++)
            {
                var rowRect = new RectangleF(_panelRect.X + 4, _cbRects[i].Y - 2, _panelRect.Width - 8, 17);
                if (rowRect.Contains(pt))
                {
                    _check[i] = !_check[i];
                    return;
                }
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────

        private void DrawSection(Graphics g, float x, float y, float w, string title)
        {
            try
            {
                using var br   = new SolidBrush(Color.FromArgb(45, 55, 75));
                using var font = new Font("Arial", 7f, FontStyle.Bold);
                using var tbr  = new SolidBrush(Color.FromArgb(160, 160, 160));
                g.FillRectangle(br, x - 4, y, w + 8, 16);
                g.DrawString(title, font, tbr, x, y + 2);
            }
            catch { }
        }

        private void DrawGateRow(Graphics g, float x, ref float y, string label, bool passed, bool fail, string detail)
        {
            try
            {
                Color  ic  = passed ? Color.LimeGreen : (fail ? Color.OrangeRed : Color.Gold);
                string ico = passed ? "✓" : "✗";
                using var ifont = new Font("Arial", 8.5f, FontStyle.Bold);
                using var lfont = new Font("Arial", 7.5f);
                using var iBr   = new SolidBrush(ic);
                using var lBr   = new SolidBrush(Color.Silver);
                using var dBr   = new SolidBrush(passed ? Color.FromArgb(100, 200, 100) : ic);
                g.DrawString(ico,    ifont, iBr, x,       y);
                g.DrawString(label,  lfont, lBr, x + 14,  y + 1);
                g.DrawString(detail, lfont, dBr, x + 155, y + 1);
            }
            catch { }
            y += 15;
        }

        private void DrawCheckbox(Graphics g, RectangleF r, bool isChecked, bool critical, string label)
        {
            try
            {
                if (critical)
                {
                    using var critBr = new SolidBrush(isChecked ? Color.FromArgb(70, 0, 200, 0) : Color.FromArgb(70, 200, 30, 30));
                    g.FillRectangle(critBr, r.X - 3, r.Y - 1, 3, r.Height + 2);
                }

                using var fillBr = new SolidBrush(isChecked ? Color.FromArgb(50, 0, 200, 0) : Color.FromArgb(20, 60, 60, 60));
                using var pen    = new Pen(critical ? (isChecked ? Color.LimeGreen : Color.FromArgb(160, 200, 60, 60)) : Color.FromArgb(100, 120, 120, 120), 1f);
                g.FillRectangle(fillBr, r.X, r.Y, r.Width, r.Height);
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);

                if (isChecked)
                {
                    using var ckFont = new Font("Arial", 7f, FontStyle.Bold);
                    using var ckBr   = new SolidBrush(Color.LimeGreen);
                    g.DrawString("✓", ckFont, ckBr, r.X + 1, r.Y);
                }

                Color lblColor = critical ? (isChecked ? Color.FromArgb(180, 230, 180) : Color.FromArgb(230, 180, 180)) : Color.Silver;
                using var lFont = new Font("Arial", 7.5f, critical ? FontStyle.Bold : FontStyle.Regular);
                using var lBr   = new SolidBrush(lblColor);
                g.DrawString(label + (critical ? "  ←" : ""), lFont, lBr, r.Right + 5, r.Y);
            }
            catch { }
        }

        private void DrawSelector(Graphics g, float x, float y, float w, string label, string value, Color valueColor,
            out RectangleF prevBtn, out RectangleF nextBtn)
        {
            float bw = 18;
            prevBtn = new RectangleF(x,           y + 13, bw, 14);
            nextBtn = new RectangleF(x + w - bw,  y + 13, bw, 14);

            try
            {
                using var lf  = new Font("Arial", 6.5f, FontStyle.Bold);
                using var vf  = new Font("Arial", 9f,   FontStyle.Bold);
                using var bf  = new Font("Arial", 7f,   FontStyle.Bold);
                using var lBr = new SolidBrush(Color.FromArgb(120, 120, 120));
                using var vBr = new SolidBrush(valueColor);
                using var bBr = new SolidBrush(Color.FromArgb(50, 80, 80, 80));
                using var pen = new Pen(Color.FromArgb(70, 110, 110, 110), 1f);
                using var tBr = new SolidBrush(Color.LightGray);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.DrawString(label, lf, lBr, x, y);
                g.DrawString(value, vf, vBr, x + bw + 4, y + 12);

                g.FillRectangle(bBr, prevBtn.X, prevBtn.Y, prevBtn.Width, prevBtn.Height);
                g.DrawRectangle(pen, prevBtn.X, prevBtn.Y, prevBtn.Width, prevBtn.Height);
                g.DrawString("◀", bf, tBr, new RectangleF(prevBtn.X, prevBtn.Y, prevBtn.Width, prevBtn.Height), sf);

                g.FillRectangle(bBr, nextBtn.X, nextBtn.Y, nextBtn.Width, nextBtn.Height);
                g.DrawRectangle(pen, nextBtn.X, nextBtn.Y, nextBtn.Width, nextBtn.Height);
                g.DrawString("▶", bf, tBr, new RectangleF(nextBtn.X, nextBtn.Y, nextBtn.Width, nextBtn.Height), sf);
            }
            catch { }
        }

        private void DrawToggle(Graphics g, float x, float y, float w, string label, string value, Color valueColor,
            out RectangleF toggleRect)
        {
            toggleRect = new RectangleF(x, y + 12, w - 2, 15);

            try
            {
                using var lf  = new Font("Arial", 6.5f, FontStyle.Bold);
                using var vf  = new Font("Arial", 9f,   FontStyle.Bold);
                using var lBr = new SolidBrush(Color.FromArgb(120, 120, 120));
                using var vBr = new SolidBrush(valueColor);
                using var fBr = new SolidBrush(Color.FromArgb(40, valueColor.R, valueColor.G, valueColor.B));
                using var pen = new Pen(Color.FromArgb(90, valueColor.R, valueColor.G, valueColor.B), 1f);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.DrawString(label, lf, lBr, x, y);
                g.FillRectangle(fBr, toggleRect.X, toggleRect.Y, toggleRect.Width, toggleRect.Height);
                g.DrawRectangle(pen, toggleRect.X, toggleRect.Y, toggleRect.Width, toggleRect.Height);
                g.DrawString(value, vf, vBr, new RectangleF(toggleRect.X, toggleRect.Y, toggleRect.Width, toggleRect.Height), sf);
            }
            catch { }
        }

        private void DrawPriceRow(Graphics g, float x, float y, float w, string label,
            double price, Color priceColor, double tickSize,
            out RectangleF minusBtn, out RectangleF plusBtn)
        {
            float bw = 26;
            minusBtn = new RectangleF(x + 55,     y, bw, 18);
            plusBtn  = new RectangleF(x + w - bw, y, bw, 18);

            try
            {
                using var lf  = new Font("Arial", 7.5f, FontStyle.Bold);
                using var pf  = new Font("Arial", 9.5f, FontStyle.Bold);
                using var bf  = new Font("Arial", 9f,   FontStyle.Bold);
                using var lBr = new SolidBrush(Color.FromArgb(130, 130, 130));
                using var pBr = new SolidBrush(price > 0 ? priceColor : Color.FromArgb(80, 120, 120, 120));
                using var bBr = new SolidBrush(Color.FromArgb(40, 80, 80, 80));
                using var pen = new Pen(Color.FromArgb(70, 110, 110, 110), 1f);
                using var tBr = new SolidBrush(Color.LightGray);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

                g.DrawString(label, lf, lBr, x, y + 3);

                g.FillRectangle(bBr, minusBtn.X, minusBtn.Y, minusBtn.Width, minusBtn.Height);
                g.DrawRectangle(pen, minusBtn.X, minusBtn.Y, minusBtn.Width, minusBtn.Height);
                g.DrawString("−", bf, tBr, new RectangleF(minusBtn.X, minusBtn.Y, minusBtn.Width, minusBtn.Height), sf);

                string priceStr = price > 0 ? price.ToString("F2") : "  —  ";
                g.DrawString(priceStr, pf, pBr, x + 88, y + 2);

                g.FillRectangle(bBr, plusBtn.X, plusBtn.Y, plusBtn.Width, plusBtn.Height);
                g.DrawRectangle(pen, plusBtn.X, plusBtn.Y, plusBtn.Width, plusBtn.Height);
                g.DrawString("+", bf, tBr, new RectangleF(plusBtn.X, plusBtn.Y, plusBtn.Width, plusBtn.Height), sf);
            }
            catch { }
        }

        private void DrawText(Graphics g, float x, float y, string text, Color color, float size = 8f)
        {
            try
            {
                using var f  = new Font("Arial", size);
                using var br = new SolidBrush(color);
                g.DrawString(text, f, br, x, y);
            }
            catch { }
        }
    }

    internal static class DoubleExtGate
    {
        internal static bool IsNaN(this double d) => double.IsNaN(d);
    }
}
