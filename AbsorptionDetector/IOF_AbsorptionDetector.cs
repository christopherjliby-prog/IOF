// IOF_AbsorptionDetector.cs — Absorption / Exhaustion → Aggression-Flip detector
//                             with Phantoms daily-bias layer (EMA 20/50/200 + VWAP)
//
// Bias layer (Phantoms methodology):
//   STRONG LONG  — close > EMA20 > EMA50 > EMA200 AND close > VWAP  (all 5 aligned)
//   LONG         — close above EMA50 and EMA200, majority aligned
//   NEUTRAL      — mixed signals
//   SHORT        — close below EMA50 and EMA200, majority aligned
//   STRONG SHORT — close < EMA20 < EMA50 < EMA200 AND close < VWAP  (all 5 aligned)
//
// Signal gating:
//   BUY NOW  fires only when bias = LONG or STRONG LONG
//   SELL NOW fires only when bias = SHORT or STRONG SHORT
//   NEUTRAL  fires with "[NEUTRAL BIAS]" tag (suppressed if BiasRequireAligned = true)
//
// Absorption signatures detected:
//   ABSORPTION — elevated volume + delta-close divergence (heavy delta, opposing close)
//   EXHAUSTION — volume drying up into the extreme (move running out of gas)
//
// Detect-only. No orders. Trader decides. Gate panel executes.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace IOF_AbsorptionDetector
{
    public enum AbsorptionSide { Demand, Supply }
    public enum AlertLevel     { Watch, Exhaustion, Confirmed }
    public enum SignatureType  { Absorption, Exhaustion }
    public enum BiasState      { StrongLong, Long, Neutral, Short, StrongShort }

    public class IOF_AbsorptionDetector : Indicator, IVolumeAnalysisIndicator
    {
        // ── Bias / EMA parameters ────────────────────────────────────────────────

        [InputParameter("Bias filter enabled", 5)]
        public bool BiasFilterEnabled = true;

        [InputParameter("Require aligned bias (block Neutral signals)", 6)]
        public bool BiasRequireAligned = false;

        [InputParameter("EMA 20 period", 7, 2, 500, 1, 0)]
        public int EmaPeriod20 = 20;

        [InputParameter("EMA 50 period", 8, 2, 500, 1, 0)]
        public int EmaPeriod50 = 50;

        [InputParameter("EMA 200 period", 9, 2, 500, 1, 0)]
        public int EmaPeriod200 = 200;

        [InputParameter("Show EMA lines on chart", 11)]
        public bool ShowEmaLines = true;

        [InputParameter("Show VWAP line on chart", 12)]
        public bool ShowVwapLine = true;

        [InputParameter("Show bias status box", 13)]
        public bool ShowBiasBox = true;

        // ── Detection parameters ─────────────────────────────────────────────────

        [InputParameter("Effort lookback (bars)", 20, 2, 50, 1, 0)]
        public int EffortLookback = 5;

        [InputParameter("Effort delta threshold (abs)", 25, 1, 20000, 50, 0)]
        public int EffortDeltaThreshold = 800;

        [InputParameter("Effort min heavy bars", 30, 1, 20, 1, 0)]
        public int EffortMinBars = 2;

        [InputParameter("New-extreme lookback (bars)", 35, 2, 50, 1, 0)]
        public int NewExtremeLookback = 5;

        [InputParameter("Divergence mode (primary absorption)", 40)]
        public bool DivergenceMode = true;

        [InputParameter("Exhaustion delta max (abs) — collapse form", 45, 1, 20000, 25, 0)]
        public int ExhaustionDeltaMax = 200;

        [InputParameter("Absorption: volume min (elevated)", 50, 1, 1000000, 100, 0)]
        public int ExhaustionVolMin = 1500;

        [InputParameter("Exhaustion: vol drop ratio (x prior avg)", 55, 0.1, 1.0, 0.05, 2)]
        public double ExhaustionVolDrop = 0.6;

        [InputParameter("Flip delta threshold (abs)", 60, 1, 20000, 50, 0)]
        public int FlipDeltaThreshold = 1000;

        [InputParameter("Flip confirm window (bars)", 65, 1, 10, 1, 0)]
        public int FlipConfirmWindow = 3;

        [InputParameter("Zone proximity (ticks)", 70, 0, 200, 1, 0)]
        public int ZoneProximityTicks = 20;

        [InputParameter("Require zone context", 75)]
        public bool RequireZoneContext = false;

        // ── Alerts ────────────────────────────────────────────────────────────────

        [InputParameter("Enable Watch pre-alert", 80)]
        public bool EnableWatchAlert = true;

        [InputParameter("Sound on Exhaustion alert", 85)]
        public bool SoundOnExhaustion = true;

        [InputParameter("Sound on Confirmed alert", 90)]
        public bool SoundOnConfirmed = true;

        [InputParameter("Log all candidates (tuning mode)", 95)]
        public bool LogAllCandidates = false;

        [InputParameter("Log file path", 96)]
        public string LogFilePath = "IOF_AbsorptionDetector_log.csv";

        // ── Internal state ───────────────────────────────────────────────────────

        private class PendingConfirm
        {
            public AbsorptionSide Side;
            public SignatureType  Type;
            public int            BarsSince;
        }

        private struct MarkerInfo
        {
            public DateTime       Time;
            public double         Price;
            public AlertLevel     Level;
            public AbsorptionSide Side;
            public SignatureType  Type;
            public string         Label;
            public BiasState      Bias;
        }

        private readonly List<PendingConfirm> _pending = new();
        private readonly List<MarkerInfo>     _markers = new();
        private readonly object               _lock    = new();

        // EMA state
        private double _ema20 = 0, _ema50 = 0, _ema200 = 0;
        private bool   _ema20Init = false, _ema50Init = false, _ema200Init = false;
        private int    _barCount  = 0;

        // VWAP state — resets each session day
        private double   _vwapCumTPV = 0, _vwapCumVol = 0, _currentVwap = 0;
        private DateTime _vwapDate   = DateTime.MinValue;

        private BiasState _currentBias = BiasState.Neutral;
        private string    _zoneKey     = "";
        private bool      _volumeAnalysisLoaded = false;

        // Line series indices
        private const int S_EMA20  = 0;
        private const int S_EMA50  = 1;
        private const int S_EMA200 = 2;
        private const int S_VWAP   = 3;

        // ════════════════════════════════════════════════════════════════════════

        public IOF_AbsorptionDetector() : base()
        {
            Name           = "IOF Absorption Detector";
            Description    = "Absorption/exhaustion signal with Phantoms EMA+VWAP bias filter. Detect-only.";
            SeparateWindow = false;

            AddLineSeries("EMA 20",  Color.FromArgb(255, 0,   220, 255), 1, LineStyle.Solid);
            AddLineSeries("EMA 50",  Color.FromArgb(255, 255, 165,   0), 1, LineStyle.Solid);
            AddLineSeries("EMA 200", Color.FromArgb(255, 255,  60,  60), 2, LineStyle.Solid);
            AddLineSeries("VWAP",    Color.FromArgb(255, 255, 255,   0), 1, LineStyle.Dash);
        }

        public bool IsRequirePriceLevelsCalculation => false;
        public void VolumeAnalysisData_Loaded() => _volumeAnalysisLoaded = true;

        protected override void OnInit()
        {
            lock (_lock) { _pending.Clear(); _markers.Clear(); }
            _volumeAnalysisLoaded = false;
            _ema20 = _ema50 = _ema200 = 0;
            _ema20Init = _ema50Init = _ema200Init = false;
            _barCount = 0;
            _vwapCumTPV = _vwapCumVol = _currentVwap = 0;
            _vwapDate   = DateTime.MinValue;
            _currentBias = BiasState.Neutral;

            try
            {
                string sym    = this.Symbol?.Name ?? "UNK";
                string period = this.HistoricalData?.Aggregation?.ToString() ?? "UNK";
                _zoneKey = $"{sym}_{period}";
            }
            catch { _zoneKey = ""; }

            if (LogAllCandidates)
            {
                try
                {
                    if (!File.Exists(LogFilePath))
                        File.AppendAllText(LogFilePath,
                            "Time,Side,SignatureType,Stage,Bias,EffortBars,CurDelta,CurVol,AvgPriorVol,Price,DivHit,NearZone,Fired\n");
                }
                catch { }
            }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (this.HistoricalData == null || this.HistoricalData.Count < 2) return;

            var bar = this.HistoricalData[0] as HistoryItemBar;
            if (bar == null) return;

            // ── Update EMA and VWAP on every tick (drives the line series) ────────
            UpdateBiasIndicators(bar, args.Reason == UpdateReason.NewBar || args.Reason == UpdateReason.HistoricalBar);

            // Set line series values (hide lines if user disabled them)
            SetValue(_ema20Init  && ShowEmaLines  ? _ema20       : double.NaN, S_EMA20);
            SetValue(_ema50Init  && ShowEmaLines  ? _ema50       : double.NaN, S_EMA50);
            SetValue(_ema200Init && ShowEmaLines  ? _ema200      : double.NaN, S_EMA200);
            SetValue(_vwapCumVol > 0 && ShowVwapLine ? _currentVwap : double.NaN, S_VWAP);

            // ── Detection runs only on bar close ──────────────────────────────────
            if (!_volumeAnalysisLoaded) return;
            if (args.Reason != UpdateReason.NewBar && args.Reason != UpdateReason.HistoricalBar) return;

            int need = Math.Max(EffortLookback, NewExtremeLookback) + FlipConfirmWindow + 3;
            if (this.HistoricalData.Count < need) return;

            var closed = this.HistoricalData[1] as HistoryItemBar;
            if (closed == null) return;

            EvaluateBar(closed, AbsorptionSide.Demand);
            EvaluateBar(closed, AbsorptionSide.Supply);
            AdvanceConfirmations(closed);
        }

        // ── Bias / EMA / VWAP ───────────────────────────────────────────────────

        private void UpdateBiasIndicators(HistoryItemBar bar, bool isBarClose)
        {
            double close = bar.Close;

            // EMA updates only on bar close to avoid intrabar drift on the lines
            if (isBarClose)
            {
                _barCount++;

                double alpha20  = 2.0 / (EmaPeriod20  + 1);
                double alpha50  = 2.0 / (EmaPeriod50  + 1);
                double alpha200 = 2.0 / (EmaPeriod200 + 1);

                if (!_ema20Init)  { _ema20  = close; _ema20Init  = _barCount >= EmaPeriod20;  }
                else              { _ema20  = alpha20  * close + (1 - alpha20)  * _ema20;  }

                if (!_ema50Init)  { _ema50  = close; _ema50Init  = _barCount >= EmaPeriod50;  }
                else              { _ema50  = alpha50  * close + (1 - alpha50)  * _ema50;  }

                if (!_ema200Init) { _ema200 = close; _ema200Init = _barCount >= EmaPeriod200; }
                else              { _ema200 = alpha200 * close + (1 - alpha200) * _ema200; }

                // VWAP: reset on new session day
                DateTime barDate = bar.TimeLeft.Date;
                if (barDate != _vwapDate)
                {
                    _vwapCumTPV = 0;
                    _vwapCumVol = 0;
                    _vwapDate   = barDate;
                }
                double tp = (bar.High + bar.Low + close) / 3.0;
                _vwapCumTPV += tp * bar.Volume;
                _vwapCumVol += bar.Volume;
                _currentVwap = _vwapCumVol > 0 ? _vwapCumTPV / _vwapCumVol : close;
            }

            _currentBias = CalcBias(close);
        }

        private BiasState CalcBias(double close)
        {
            if (!_ema200Init) return BiasState.Neutral;

            int bull = 0;
            if (_ema20Init  && close  > _ema20)  bull++;   // price above fast EMA
            if (_ema20Init  && _ema50Init  && _ema20  > _ema50)  bull++;  // EMA20 > EMA50
            if (_ema50Init  && _ema200Init && _ema50  > _ema200) bull++;  // EMA50 > EMA200
            if (close > _ema200)                              bull++;   // price above slow EMA
            if (_vwapCumVol > 0 && close > _currentVwap)    bull++;   // above VWAP

            return bull switch
            {
                5    => BiasState.StrongLong,
                4    => BiasState.Long,
                3    => BiasState.Neutral,
                2    => BiasState.Short,
                1    => BiasState.StrongShort,
                _    => BiasState.StrongShort
            };
        }

        private bool BiasAllows(AbsorptionSide side)
        {
            if (!BiasFilterEnabled) return true;

            if (side == AbsorptionSide.Demand)
            {
                if (_currentBias == BiasState.StrongLong || _currentBias == BiasState.Long) return true;
                if (_currentBias == BiasState.Neutral && !BiasRequireAligned) return true;
                return false;
            }
            else
            {
                if (_currentBias == BiasState.StrongShort || _currentBias == BiasState.Short) return true;
                if (_currentBias == BiasState.Neutral && !BiasRequireAligned) return true;
                return false;
            }
        }

        private string BiasTag()
        {
            if (!BiasFilterEnabled) return "";
            return _currentBias == BiasState.Neutral ? "  [NEUTRAL BIAS]" : "";
        }

        // ── Core detection ───────────────────────────────────────────────────────

        private double Delta(HistoryItemBar b)
        {
            try { return b?.VolumeAnalysisData?.Total?.Delta ?? 0.0; }
            catch { return 0.0; }
        }

        private double Vol(HistoryItemBar b)
        {
            try { return b?.Volume ?? 0.0; }
            catch { return 0.0; }
        }

        private void EvaluateBar(HistoryItemBar cur, AbsorptionSide side)
        {
            if (!BiasAllows(side)) return;

            // Step 1: effort phase (bars BEFORE current: indices [2..EffortLookback+1])
            int    heavyCount    = 0;
            var    effortDeltas  = new List<double>();
            double avgPriorVol   = 0;
            int    priorVolCount = 0;

            for (int i = 2; i <= EffortLookback + 1; i++)
            {
                var b = this.HistoricalData[i] as HistoryItemBar;
                if (b == null) break;
                double d = Delta(b);
                bool heavy = side == AbsorptionSide.Demand
                    ? d <= -EffortDeltaThreshold
                    : d >= EffortDeltaThreshold;
                if (heavy) { heavyCount++; effortDeltas.Add(d); }
                avgPriorVol += Vol(b);
                priorVolCount++;
            }
            if (priorVolCount > 0) avgPriorVol /= priorVolCount;
            bool effortPresent = heavyCount >= EffortMinBars;

            // Step 2: at the extreme (prior bars only — fixes the always-true bug)
            double tickSize = 0.25;
            try { tickSize = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25; } catch { }

            double priorExtreme = side == AbsorptionSide.Demand ? double.MaxValue : double.MinValue;
            for (int i = 2; i <= NewExtremeLookback + 1; i++)
            {
                var b = this.HistoricalData[i] as HistoryItemBar;
                if (b == null) break;
                if (side == AbsorptionSide.Demand) priorExtreme = Math.Min(priorExtreme, b.Low);
                else                               priorExtreme = Math.Max(priorExtreme, b.High);
            }

            bool atExtreme = side == AbsorptionSide.Demand
                ? cur.Low  <= priorExtreme + tickSize
                : cur.High >= priorExtreme - tickSize;

            // Step 3: reward failing
            var prev = this.HistoricalData[2] as HistoryItemBar;
            bool rewardFailing = side == AbsorptionSide.Demand
                ? prev == null || cur.Low  >= prev.Low  - tickSize
                : prev == null || cur.High <= prev.High + tickSize;

            // Step 4+5: signature type
            double curDelta = Delta(cur);
            double curVol   = Vol(cur);

            bool divergenceHit = side == AbsorptionSide.Demand
                ? DivergenceMode && curDelta <= -EffortDeltaThreshold && cur.Close > cur.Open
                : DivergenceMode && curDelta >=  EffortDeltaThreshold && cur.Close < cur.Open;

            bool deltaCollapse = Math.Abs(curDelta) <= ExhaustionDeltaMax
                && (side == AbsorptionSide.Demand ? curDelta <= 0 : curDelta >= 0);

            bool absorptionSignal = (divergenceHit || deltaCollapse) && curVol >= ExhaustionVolMin;
            bool volDryingUp      = priorVolCount > 0 && curVol < ExhaustionVolDrop * avgPriorVol;
            bool deltaWeakening   = side == AbsorptionSide.Demand
                ? curDelta > -EffortDeltaThreshold
                : curDelta <  EffortDeltaThreshold;
            bool exhaustionSignal = volDryingUp && deltaWeakening;

            bool nearZone = CheckZoneProximity(side == AbsorptionSide.Demand ? cur.Low : cur.High);
            bool zoneOk   = !RequireZoneContext || nearZone;

            if (LogAllCandidates && effortPresent)
            {
                string eff = string.Join("|", effortDeltas.ConvertAll(d => d.ToString("F0")));
                TryLog(cur.TimeLeft, side,
                    absorptionSignal ? SignatureType.Absorption : SignatureType.Exhaustion,
                    (absorptionSignal || exhaustionSignal) ? "EXHAUSTION" : "candidate",
                    _currentBias, heavyCount, eff, curDelta, curVol, avgPriorVol,
                    side == AbsorptionSide.Demand ? cur.Low : cur.High,
                    divergenceHit, nearZone,
                    (absorptionSignal || exhaustionSignal) && atExtreme && rewardFailing && zoneOk);
            }

            if (EnableWatchAlert && effortPresent && atExtreme && !absorptionSignal && !exhaustionSignal)
            {
                double wp = side == AbsorptionSide.Demand ? cur.Low : cur.High;
                AddMarker(cur.TimeLeft, wp, AlertLevel.Watch, side, SignatureType.Absorption,
                    $"WATCH: {side} effort {heavyCount}/{EffortLookback} bars, delta {curDelta:F0}",
                    _currentBias);
            }

            if (effortPresent && atExtreme && rewardFailing && zoneOk)
            {
                SignatureType sigType;
                if (absorptionSignal)      sigType = SignatureType.Absorption;
                else if (exhaustionSignal) sigType = SignatureType.Exhaustion;
                else                       return;

                double alertPrice = side == AbsorptionSide.Demand ? cur.Low : cur.High;
                string sigLabel   = sigType == SignatureType.Absorption
                    ? (divergenceHit ? "ABSORPTION (divergence)" : "ABSORPTION (collapse)")
                    : "EXHAUSTION (vol drying)";
                string volInfo    = sigType == SignatureType.Absorption
                    ? $"vol {curVol:F0} elevated"
                    : $"vol {curVol:F0} ({curVol / avgPriorVol:P0} of avg)";
                string biasStr    = BiasLabel(_currentBias);
                string zoneTag    = nearZone ? "" : "  [NO ZONE]";

                string label = $"{sigLabel}: effort {FormatRecentDeltas(side)}, bar {curDelta:F0}, {volInfo}  bias:{biasStr}{zoneTag}{BiasTag()}";

                AddMarker(cur.TimeLeft, alertPrice, AlertLevel.Exhaustion, side, sigType, label, _currentBias);
                lock (_lock) _pending.Add(new PendingConfirm { Side = side, Type = sigType, BarsSince = 0 });
                if (SoundOnExhaustion) PlaySound(AlertLevel.Exhaustion);
                FirePlatformAlert("ABSORPTION_EXHAUSTION", label);
            }
        }

        private void AdvanceConfirmations(HistoryItemBar cur)
        {
            double curDelta = Delta(cur);
            lock (_lock)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var p = _pending[i];
                    p.BarsSince++;

                    bool flip = p.Side == AbsorptionSide.Demand
                        ? curDelta >= FlipDeltaThreshold
                        : curDelta <= -FlipDeltaThreshold;

                    if (flip)
                    {
                        string label = $"{p.Type.ToString().ToUpper()} CONFIRMED: flip {curDelta:F0} ({p.BarsSince} bar(s) after tell)";
                        AddMarker(cur.TimeLeft, cur.Close, AlertLevel.Confirmed, p.Side, p.Type, label, _currentBias);
                        if (SoundOnConfirmed) PlaySound(AlertLevel.Confirmed);
                        FirePlatformAlert("ABSORPTION_CONFIRMED", label);
                        _pending.RemoveAt(i);
                    }
                    else if (p.BarsSince >= FlipConfirmWindow)
                    {
                        _pending.RemoveAt(i);
                    }
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private string FormatRecentDeltas(AbsorptionSide side)
        {
            var parts = new List<string>();
            for (int i = EffortLookback + 1; i >= 2; i--)
            {
                var b = this.HistoricalData[i] as HistoryItemBar;
                if (b == null) continue;
                double d = Delta(b);
                if (side == AbsorptionSide.Demand ? d <= -EffortDeltaThreshold : d >= EffortDeltaThreshold)
                    parts.Add(d.ToString("F0"));
            }
            return parts.Count > 0 ? string.Join("/", parts) : "n/a";
        }

        private static string BiasLabel(BiasState b) => b switch
        {
            BiasState.StrongLong  => "STRONG LONG",
            BiasState.Long        => "LONG",
            BiasState.Neutral     => "NEUTRAL",
            BiasState.Short       => "SHORT",
            BiasState.StrongShort => "STRONG SHORT",
            _                     => "?"
        };

        // ── Zone proximity ───────────────────────────────────────────────────────

        private bool CheckZoneProximity(double price)
        {
            if (string.IsNullOrEmpty(_zoneKey)) return false;
            double tickSize = 0.25;
            try { tickSize = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25; } catch { }
            double tolerance = ZoneProximityTicks * tickSize;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TradePhantoms_IOF_v2") continue;
                    var type   = asm.GetType("TradePhantoms.IOFZoneRegistry");
                    if (type == null) continue;
                    var method = type.GetMethod("IsNearZone", BindingFlags.Public | BindingFlags.Static);
                    if (method == null) continue;
                    return (bool)method.Invoke(null, new object[] { _zoneKey, price, tolerance });
                }
            }
            catch { }
            return false;
        }

        // ── Platform alerts ───────────────────────────────────────────────────────

        private void PlaySound(AlertLevel level)
        {
            try
            {
                switch (level)
                {
                    case AlertLevel.Exhaustion: Console.Beep(1200, 200); break;
                    case AlertLevel.Confirmed:  Console.Beep(1600, 150); Console.Beep(1900, 150); break;
                }
            }
            catch { }
        }

        private void FirePlatformAlert(string eventType, string message)
        {
            try
            {
                var core = Core.Instance;
                if (core == null) return;
                var alertsProp = core.GetType().GetProperty("Alerts");
                var alerts     = alertsProp?.GetValue(core);
                if (alerts == null) return;
                var addAlert   = alerts.GetType().GetMethod("AddAlert", new[] { typeof(string), typeof(string) });
                addAlert?.Invoke(alerts, new object[] { eventType, message });
            }
            catch { }
        }

        private void TryLog(DateTime time, AbsorptionSide side, SignatureType sigType, string stage,
            BiasState bias, int heavyCount, string effortDeltas, double curDelta, double curVol,
            double avgPriorVol, double price, bool divHit, bool nearZone, bool fired)
        {
            try
            {
                string line = string.Join(",",
                    time.ToString("yyyy-MM-dd HH:mm:ss"), side, sigType, stage, bias,
                    heavyCount, effortDeltas, curDelta.ToString("F0"), curVol.ToString("F0"),
                    avgPriorVol.ToString("F0"), price.ToString("F4"), divHit, nearZone, fired);
                File.AppendAllText(LogFilePath, line + "\n");
            }
            catch { }
        }

        // ── Rendering ─────────────────────────────────────────────────────────────

        private void AddMarker(DateTime time, double price, AlertLevel level,
            AbsorptionSide side, SignatureType type, string label, BiasState bias)
        {
            lock (_lock)
            {
                _markers.Add(new MarkerInfo
                {
                    Time = time, Price = price, Level = level,
                    Side = side, Type = type, Label = label, Bias = bias
                });
                if (_markers.Count > 500) _markers.RemoveAt(0);
            }
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            if (this.CurrentChart == null) return;

            var gr   = args.Graphics;
            var win  = this.CurrentChart.MainWindow;
            var rect = (Rectangle)win.ClientRectangle;

            // ── Bias status box (top-left) ────────────────────────────────────────
            if (ShowBiasBox)
                DrawBiasBox(gr, rect);

            // ── Signal markers ────────────────────────────────────────────────────
            List<MarkerInfo> snap;
            lock (_lock) { snap = new List<MarkerInfo>(_markers); }
            if (snap.Count == 0) return;

            using var labelFont  = new Font("Arial",    11f, FontStyle.Bold);
            using var detailFont = new Font("Consolas",  7f, FontStyle.Regular);

            foreach (var m in snap)
            {
                int x, y;
                try
                {
                    x = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(m.Time));
                    y = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(m.Price));
                }
                catch { continue; }

                if (x < rect.Left - 100 || x > rect.Right + 100) continue;

                bool isDemand = m.Side == AbsorptionSide.Demand;

                Color bubbleColor;
                if (m.Level == AlertLevel.Watch)
                    bubbleColor = isDemand ? Color.FromArgb(100, 0, 200, 80) : Color.FromArgb(100, 220, 40, 40);
                else
                    bubbleColor = isDemand ? Color.FromArgb(220, 0, 210, 80) : Color.FromArgb(220, 230, 30, 30);

                int radius  = m.Level == AlertLevel.Exhaustion ? 12 : m.Level == AlertLevel.Confirmed ? 10 : 6;
                int gap     = radius + 6;
                int centerY = isDemand ? y + gap : y - gap;

                using var brush     = new SolidBrush(bubbleColor);
                using var rimPen    = new Pen(Color.White, 1.5f);
                using var textBrush = new SolidBrush(Color.White);

                gr.FillEllipse(brush,  x - radius, centerY - radius, radius * 2, radius * 2);
                gr.DrawEllipse(rimPen, x - radius, centerY - radius, radius * 2, radius * 2);

                if (m.Level == AlertLevel.Exhaustion || m.Level == AlertLevel.Confirmed)
                {
                    string callout = isDemand ? "BUY NOW" : "SELL NOW";
                    var    sz      = gr.MeasureString(callout, labelFont);
                    int    textX   = x - (int)(sz.Width / 2);
                    int    textY   = isDemand
                        ? centerY + radius + 3
                        : centerY - radius - (int)sz.Height - 3;

                    using var shadowBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                    gr.DrawString(callout, labelFont, shadowBrush, textX + 1, textY + 1);
                    gr.DrawString(callout, labelFont, textBrush,   textX,     textY);

                    if (m.Level == AlertLevel.Exhaustion)
                    {
                        var detailSz = gr.MeasureString(m.Label, detailFont);
                        int detailX  = x - (int)(detailSz.Width / 2);
                        int detailY  = isDemand
                            ? textY + (int)sz.Height + 1
                            : textY - (int)detailSz.Height - 1;
                        using var detailBrush = new SolidBrush(Color.FromArgb(200, 220, 220, 220));
                        gr.DrawString(m.Label, detailFont, detailBrush, detailX, detailY);
                    }
                }
            }
        }

        private void DrawBiasBox(Graphics gr, Rectangle rect)
        {
            string biasText = BiasLabel(_currentBias);

            Color biasColor = _currentBias switch
            {
                BiasState.StrongLong  => Color.FromArgb(255, 0,  210, 80),
                BiasState.Long        => Color.FromArgb(255, 0,  170, 60),
                BiasState.Neutral     => Color.FromArgb(255, 160,160,160),
                BiasState.Short       => Color.FromArgb(255, 210, 50, 50),
                BiasState.StrongShort => Color.FromArgb(255, 230, 20, 20),
                _                     => Color.Gray
            };

            string arrow = _currentBias switch
            {
                BiasState.StrongLong  => " ▲▲",
                BiasState.Long        => " ▲",
                BiasState.Neutral     => " —",
                BiasState.Short       => " ▼",
                BiasState.StrongShort => " ▼▼",
                _                     => ""
            };

            string ema200Str = _ema200Init ? _ema200.ToString("F2") : "…";
            string ema50Str  = _ema50Init  ? _ema50.ToString("F2")  : "…";
            string ema20Str  = _ema20Init  ? _ema20.ToString("F2")  : "…";
            string vwapStr   = _vwapCumVol > 0 ? _currentVwap.ToString("F2") : "…";

            var lines = new[]
            {
                $"BIAS: {biasText}{arrow}",
                $"EMA20:  {ema20Str}",
                $"EMA50:  {ema50Str}",
                $"EMA200: {ema200Str}",
                $"VWAP:   {vwapStr}"
            };

            using var boxFont  = new Font("Consolas", 8f, FontStyle.Bold);
            using var valFont  = new Font("Consolas", 8f, FontStyle.Regular);

            float lineH  = boxFont.GetHeight(gr) + 2;
            float boxW   = 160f;
            float boxH   = lineH * lines.Length + 10;
            float boxX   = rect.Left + 8;
            float boxY   = rect.Top  + 8;

            using var bgBrush   = new SolidBrush(Color.FromArgb(180, 10, 10, 20));
            using var borderPen = new Pen(biasColor, 1.5f);
            gr.FillRectangle(bgBrush,   boxX, boxY, boxW, boxH);
            gr.DrawRectangle(borderPen, boxX, boxY, boxW, boxH);

            // First line = bias headline in bias color, rest in white
            using var biasBrush  = new SolidBrush(biasColor);
            using var whiteBrush = new SolidBrush(Color.FromArgb(220, 220, 220, 220));

            for (int i = 0; i < lines.Length; i++)
            {
                var brush = i == 0 ? biasBrush : whiteBrush;
                var font  = i == 0 ? boxFont   : valFont;
                gr.DrawString(lines[i], font, brush, boxX + 6, boxY + 5 + i * lineH);
            }
        }
    }
}
