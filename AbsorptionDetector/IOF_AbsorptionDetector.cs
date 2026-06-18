// IOF_AbsorptionDetector.cs — Absorption / Exhaustion → Aggression-Flip detector
//
// Bias layer (Phantoms methodology — EMA 20/50/200 + session VWAP):
//   STRONG LONG  — close > EMA20 > EMA50 > EMA200 AND close > VWAP
//   LONG         — majority above key EMAs
//   NEUTRAL      — mixed
//   SHORT / STRONG SHORT — mirror
//   BUY NOW fires only on LONG/STRONG LONG. SELL NOW on SHORT/STRONG SHORT.
//
// Detection — two distinct signatures (labeled separately):
//   ABSORPTION — elevated volume + delta-close divergence (heavy delta, opposing close)
//   EXHAUSTION — volume drying up into the extreme
//
// Signal strength score (★ per confirming factor, max ★★★★):
//   ★ CVD divergence confirmed (multi-bar cumulative delta diverging from price)
//   ★ Near an active IOF zone
//   ★ Bias fully aligned (not neutral)
//   ★ Delta-close divergence form (strongest absorption pattern)
//   First touch of price level adds +1 to score (virgin areas highest probability)
//
// CVD divergence (from volume spread analysis):
//   CVD making lower lows (sellers accumulating) while price holds = passive buyers absorbing.
//   Tracked over CvdLookback bars as a multi-bar confirmation layer.
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

        // ── CVD divergence ───────────────────────────────────────────────────────

        [InputParameter("CVD lookback (bars)", 14, 2, 50, 1, 0)]
        public int CvdLookback = 5;

        [InputParameter("Require CVD divergence to fire", 15)]
        public bool RequireCvdDivergence = false;

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

        // ── Touch tracking ────────────────────────────────────────────────────────

        [InputParameter("Touch tracking window (ticks)", 76, 1, 100, 1, 0)]
        public int TouchTrackingTicks = 10;

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

        [InputParameter("Use volume proxy when cluster data not loaded", 97)]
        public bool UseVolumeProxy = true;

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
            public int            Score;   // 0-4 stars
        }

        private readonly List<PendingConfirm> _pending = new();
        private readonly List<MarkerInfo>     _markers = new();
        private readonly object               _lock    = new();

        // Level touch tracking: price level (rounded to TouchTrackingTicks) → touch count
        private readonly Dictionary<long, int> _touchCounts = new();

        // EMA state
        private double _ema20 = 0, _ema50 = 0, _ema200 = 0;
        private double _ema20Sum = 0, _ema50Sum = 0, _ema200Sum = 0;  // SMA accumulators for proper seeding
        private bool   _ema20Init = false, _ema50Init = false, _ema200Init = false;
        private int    _barCount  = 0;
        private int    _prevCount = -1;  // Count-based bar detection

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
            Description    = "Absorption/exhaustion + CVD divergence + bias filter + strength scoring. Detect-only.";
            SeparateWindow = false;

            AddLineSeries("EMA 20",  Color.FromArgb(255,   0, 220, 255), 1, LineStyle.Solid);
            AddLineSeries("EMA 50",  Color.FromArgb(255, 255, 165,   0), 1, LineStyle.Solid);
            AddLineSeries("EMA 200", Color.FromArgb(255, 255,  60,  60), 2, LineStyle.Solid);
            AddLineSeries("VWAP",    Color.FromArgb(255, 255, 255,   0), 1, LineStyle.Dash);
        }

        public bool IsRequirePriceLevelsCalculation => false;
        public void VolumeAnalysisData_Loaded() => _volumeAnalysisLoaded = true;

        protected override void OnInit()
        {
            lock (_lock) { _pending.Clear(); _markers.Clear(); }
            _touchCounts.Clear();
            _volumeAnalysisLoaded = false;
            _ema20 = _ema50 = _ema200 = 0;
            _ema20Init = _ema50Init = _ema200Init = false;
            _barCount = 0;
            _vwapCumTPV = _vwapCumVol = _currentVwap = 0;
            _vwapDate   = DateTime.MinValue;
            _currentBias = BiasState.Neutral;
            _ema20Sum = _ema50Sum = _ema200Sum = 0;
            _prevCount = -1;

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
                            "Time,Side,SigType,Stage,Bias,Score,EffortBars,CurDelta,CurVol,CvdDiv,NearZone,Fired\n");
                }
                catch { }
            }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (this.HistoricalData == null || this.HistoricalData.Count < 2) return;

            var bar = this.HistoricalData[0] as HistoryItemBar;
            if (bar == null) return;

            // Reliable bar-close detection: Count increments exactly once per completed bar
            int currentCount = this.Count;
            bool isClose = currentCount != _prevCount;
            if (isClose) _prevCount = currentCount;

            UpdateBiasIndicators(bar, isClose);

            // Show EMA lines even during warmup (SMA value tracks close, then switches to EMA)
            SetValue(ShowEmaLines  && _barCount > 0 ? _ema20        : double.NaN, S_EMA20);
            SetValue(ShowEmaLines  && _barCount > 0 ? _ema50        : double.NaN, S_EMA50);
            SetValue(ShowEmaLines  && _barCount > 0 ? _ema200       : double.NaN, S_EMA200);
            SetValue(ShowVwapLine  && _vwapCumVol > 0 ? _currentVwap : double.NaN, S_VWAP);

            // Allow detection with proxy delta when cluster data not loaded
            if (!isClose) return;
            bool hasRealDelta = _volumeAnalysisLoaded;
            if (!hasRealDelta && !UseVolumeProxy) return;

            int need = Math.Max(Math.Max(EffortLookback, NewExtremeLookback), CvdLookback) + FlipConfirmWindow + 3;
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

            if (isBarClose)
            {
                _barCount++;

                double a20  = 2.0 / (EmaPeriod20  + 1);
                double a50  = 2.0 / (EmaPeriod50  + 1);
                double a200 = 2.0 / (EmaPeriod200 + 1);

                // Accumulate SMA sum during warmup, then seed EMA from SMA average
                _ema20Sum  += close;
                _ema50Sum  += close;
                _ema200Sum += close;

                if (!_ema20Init)
                {
                    _ema20 = _ema20Sum / _barCount;  // running SMA seed
                    if (_barCount >= EmaPeriod20) _ema20Init = true;
                }
                else { _ema20  = a20  * close + (1 - a20)  * _ema20; }

                if (!_ema50Init)
                {
                    _ema50 = _ema50Sum / _barCount;
                    if (_barCount >= EmaPeriod50) _ema50Init = true;
                }
                else { _ema50  = a50  * close + (1 - a50)  * _ema50; }

                if (!_ema200Init)
                {
                    _ema200 = _ema200Sum / _barCount;
                    if (_barCount >= EmaPeriod200) _ema200Init = true;
                }
                else { _ema200 = a200 * close + (1 - a200) * _ema200; }

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
            if (_ema20Init  && close > _ema20)                      bull++;
            if (_ema20Init  && _ema50Init  && _ema20  > _ema50)     bull++;
            if (_ema50Init  && _ema200Init && _ema50  > _ema200)    bull++;
            if (close > _ema200)                                     bull++;
            if (_vwapCumVol > 0 && close > _currentVwap)            bull++;

            return bull switch
            {
                5 => BiasState.StrongLong,
                4 => BiasState.Long,
                3 => BiasState.Neutral,
                2 => BiasState.Short,
                _ => BiasState.StrongShort
            };
        }

        private bool BiasAllows(AbsorptionSide side)
        {
            if (!BiasFilterEnabled) return true;
            if (side == AbsorptionSide.Demand)
                return _currentBias == BiasState.StrongLong || _currentBias == BiasState.Long
                    || (_currentBias == BiasState.Neutral && !BiasRequireAligned);
            else
                return _currentBias == BiasState.StrongShort || _currentBias == BiasState.Short
                    || (_currentBias == BiasState.Neutral && !BiasRequireAligned);
        }

        // ── Touch tracking ───────────────────────────────────────────────────────

        private long LevelKey(double price)
        {
            double tickSize = 0.25;
            try { tickSize = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25; } catch { }
            return (long)Math.Round(price / (TouchTrackingTicks * tickSize));
        }

        private int GetAndIncrementTouches(double price)
        {
            long key = LevelKey(price);
            _touchCounts.TryGetValue(key, out int count);
            _touchCounts[key] = count + 1;
            return count; // return BEFORE increment — 0 = first touch (virgin)
        }

        // ── CVD divergence ────────────────────────────────────────────────────────

        private bool CheckCvdDivergence(HistoryItemBar cur, AbsorptionSide side)
        {
            double tickSize = 0.25;
            try { tickSize = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25; } catch { }

            double cvdSum = 0;
            for (int i = 1; i <= CvdLookback; i++)
            {
                var b = this.HistoricalData[i] as HistoryItemBar;
                if (b == null) break;
                cvdSum += Delta(b);
            }

            var oldest = this.HistoricalData[CvdLookback] as HistoryItemBar;
            if (oldest == null) return false;

            double priceChange = cur.Close - oldest.Close;

            // Demand: CVD trending negative (sellers aggressive) but price not falling = divergence
            // Supply: CVD trending positive (buyers aggressive) but price not rising = divergence
            return side == AbsorptionSide.Demand
                ? cvdSum < -EffortDeltaThreshold && priceChange >= -tickSize
                : cvdSum >  EffortDeltaThreshold && priceChange <=  tickSize;
        }

        // ── Signal strength score (0-4) ──────────────────────────────────────────

        private int CalcScore(bool cvdDivergence, bool nearZone, bool divergenceHit, int priorTouches)
        {
            int score = 0;
            if (cvdDivergence)                                                   score++; // ★ CVD divergence
            if (nearZone)                                                        score++; // ★ Near zone
            if (BiasFilterEnabled && _currentBias != BiasState.Neutral)         score++; // ★ Bias fully aligned
            if (divergenceHit)                                                   score++; // ★ Divergence form
            if (priorTouches == 0) score = Math.Min(4, score + 1);                      // ★ Virgin level bonus
            return Math.Min(score, 4);
        }

        // ── Core detection ───────────────────────────────────────────────────────

        private double Delta(HistoryItemBar b)
        {
            if (b == null) return 0.0;
            try
            {
                double d = b.VolumeAnalysisData?.Total?.Delta ?? double.NaN;
                if (!double.IsNaN(d)) return d;
            }
            catch { }

            // Volume proxy: estimate delta from bar shape
            // Positive (buyers) when close is in upper half; negative (sellers) when lower half
            if (!UseVolumeProxy) return 0.0;
            double range = b.High - b.Low;
            if (range < 1e-10) return 0.0;
            double position = (b.Close - b.Low) / range;  // 0=closed at low, 1=closed at high
            return (position - 0.5) * 2.0 * b.Volume * 0.35;  // scale: ±35% of volume as proxy delta
        }

        private double Vol(HistoryItemBar b)
        {
            try { return b?.Volume ?? 0.0; }
            catch { return 0.0; }
        }

        private void EvaluateBar(HistoryItemBar cur, AbsorptionSide side)
        {
            if (!BiasAllows(side)) return;

            // Step 1: effort phase
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

            // Step 2: at the extreme (prior bars only)
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

            bool deltaCollapse    = Math.Abs(curDelta) <= ExhaustionDeltaMax
                && (side == AbsorptionSide.Demand ? curDelta <= 0 : curDelta >= 0);
            bool absorptionSignal = (divergenceHit || deltaCollapse) && curVol >= ExhaustionVolMin;
            bool volDryingUp      = priorVolCount > 0 && curVol < ExhaustionVolDrop * avgPriorVol;
            bool deltaWeakening   = side == AbsorptionSide.Demand
                ? curDelta > -EffortDeltaThreshold
                : curDelta <  EffortDeltaThreshold;
            bool exhaustionSignal = volDryingUp && deltaWeakening;

            // CVD divergence
            bool cvdDiv = CheckCvdDivergence(cur, side);
            if (RequireCvdDivergence && !cvdDiv) return;

            // Zone + touch
            double alertPrice = side == AbsorptionSide.Demand ? cur.Low : cur.High;
            bool nearZone = CheckZoneProximity(alertPrice);
            bool zoneOk   = !RequireZoneContext || nearZone;

            if (LogAllCandidates && effortPresent)
            {
                TryLog(cur.TimeLeft, side,
                    absorptionSignal ? SignatureType.Absorption : SignatureType.Exhaustion,
                    (absorptionSignal || exhaustionSignal) ? "EXHAUSTION" : "candidate",
                    _currentBias, 0, heavyCount, curDelta, curVol, cvdDiv, nearZone,
                    (absorptionSignal || exhaustionSignal) && atExtreme && rewardFailing && zoneOk);
            }

            if (EnableWatchAlert && effortPresent && atExtreme && !absorptionSignal && !exhaustionSignal)
            {
                AddMarker(cur.TimeLeft, alertPrice, AlertLevel.Watch, side, SignatureType.Absorption,
                    $"WATCH: {side} effort {heavyCount}/{EffortLookback} bars  delta {curDelta:F0}",
                    _currentBias, 0);
            }

            if (!effortPresent || !atExtreme || !rewardFailing || !zoneOk) return;

            SignatureType sigType;
            if (absorptionSignal)      sigType = SignatureType.Absorption;
            else if (exhaustionSignal) sigType = SignatureType.Exhaustion;
            else                       return;

            int priorTouches = GetAndIncrementTouches(alertPrice);
            int score = CalcScore(cvdDiv, nearZone, divergenceHit, priorTouches);

            string sigLabel  = sigType == SignatureType.Absorption
                ? (divergenceHit ? "ABSORPTION (divergence)" : "ABSORPTION (collapse)")
                : "EXHAUSTION (vol drying)";
            string volInfo   = sigType == SignatureType.Absorption
                ? $"vol {curVol:F0} elevated"
                : $"vol {curVol:F0} ({curVol / avgPriorVol:P0} of avg)";
            string cvdTag    = cvdDiv ? "  CVD✓" : "";
            string touchTag  = priorTouches == 0 ? "  [VIRGIN]" : $"  [touch #{priorTouches + 1}]";
            string biasTag   = _currentBias == BiasState.Neutral ? "  [NEUTRAL BIAS]" : "";
            string zoneTag   = nearZone ? "" : "  [NO ZONE]";

            string label = $"{sigLabel}: effort {FormatRecentDeltas(side)}, bar {curDelta:F0}, {volInfo}{cvdTag}{touchTag}{biasTag}{zoneTag}";

            AddMarker(cur.TimeLeft, alertPrice, AlertLevel.Exhaustion, side, sigType, label, _currentBias, score);
            lock (_lock) _pending.Add(new PendingConfirm { Side = side, Type = sigType, BarsSince = 0 });
            if (SoundOnExhaustion) PlaySound(AlertLevel.Exhaustion, score);
            FirePlatformAlert("ABSORPTION_EXHAUSTION", label);
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
                        string label = $"{p.Type.ToString().ToUpper()} CONFIRMED: flip {curDelta:F0} ({p.BarsSince} bar(s))";
                        AddMarker(cur.TimeLeft, cur.Close, AlertLevel.Confirmed, p.Side, p.Type, label, _currentBias, 0);
                        if (SoundOnConfirmed) PlaySound(AlertLevel.Confirmed, 0);
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

        private static string Stars(int score) =>
            new string('★', score) + new string('☆', 4 - score);

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

        // ── Platform alerts / sound ───────────────────────────────────────────────

        private void PlaySound(AlertLevel level, int score)
        {
            try
            {
                if (level == AlertLevel.Exhaustion)
                {
                    int freq = 1000 + score * 100; // higher score = higher pitch
                    Console.Beep(freq, 200);
                }
                else if (level == AlertLevel.Confirmed)
                {
                    Console.Beep(1600, 150);
                    Console.Beep(1900, 150);
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
            BiasState bias, int score, int heavyCount, double curDelta, double curVol,
            bool cvdDiv, bool nearZone, bool fired)
        {
            try
            {
                string line = string.Join(",",
                    time.ToString("yyyy-MM-dd HH:mm:ss"), side, sigType, stage, bias, score,
                    heavyCount, curDelta.ToString("F0"), curVol.ToString("F0"),
                    cvdDiv, nearZone, fired);
                File.AppendAllText(LogFilePath, line + "\n");
            }
            catch { }
        }

        // ── Rendering ─────────────────────────────────────────────────────────────

        private void AddMarker(DateTime time, double price, AlertLevel level,
            AbsorptionSide side, SignatureType type, string label, BiasState bias, int score)
        {
            lock (_lock)
            {
                _markers.Add(new MarkerInfo
                {
                    Time = time, Price = price, Level = level,
                    Side = side, Type = type, Label = label, Bias = bias, Score = score
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

            if (ShowBiasBox) DrawBiasBox(gr, rect);

            List<MarkerInfo> snap;
            lock (_lock) { snap = new List<MarkerInfo>(_markers); }
            if (snap.Count == 0) return;

            using var labelFont  = new Font("Arial",    11f, FontStyle.Bold);
            using var starsFont  = new Font("Arial",     9f, FontStyle.Bold);
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

                Color bubbleColor = m.Level == AlertLevel.Watch
                    ? (isDemand ? Color.FromArgb(100, 0, 200, 80) : Color.FromArgb(100, 220, 40, 40))
                    : (isDemand ? Color.FromArgb(220, 0, 210, 80) : Color.FromArgb(220, 230, 30, 30));

                // Bubble size scales with score on Exhaustion markers
                int radius = m.Level == AlertLevel.Exhaustion ? 10 + m.Score
                           : m.Level == AlertLevel.Confirmed  ? 10
                           : 6;
                int gap     = radius + 6;
                int centerY = isDemand ? y + gap : y - gap;

                using var brush     = new SolidBrush(bubbleColor);
                using var rimPen    = new Pen(Color.White, 1.5f);
                using var textBrush = new SolidBrush(Color.White);

                gr.FillEllipse(brush,  x - radius, centerY - radius, radius * 2, radius * 2);
                gr.DrawEllipse(rimPen, x - radius, centerY - radius, radius * 2, radius * 2);

                if (m.Level == AlertLevel.Exhaustion || m.Level == AlertLevel.Confirmed)
                {
                    // BUY NOW / SELL NOW
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
                        // Stars rating below BUY/SELL NOW
                        string starsStr = Stars(m.Score);
                        var    starsSz  = gr.MeasureString(starsStr, starsFont);
                        int    starsX   = x - (int)(starsSz.Width / 2);
                        int    starsY   = isDemand
                            ? textY + (int)sz.Height
                            : textY - (int)starsSz.Height;

                        // Star color: gold for high score, dimmer for low
                        Color starColor = m.Score >= 3
                            ? Color.FromArgb(255, 255, 215, 0)
                            : m.Score == 2
                                ? Color.FromArgb(255, 200, 160, 0)
                                : Color.FromArgb(180, 150, 150, 150);
                        using var starBrush = new SolidBrush(starColor);
                        gr.DrawString(starsStr, starsFont, starBrush, starsX, starsY);

                        // Detail line
                        var  detailSz = gr.MeasureString(m.Label, detailFont);
                        int  detailX  = x - (int)(detailSz.Width / 2);
                        int  detailY  = isDemand
                            ? starsY + (int)starsSz.Height + 1
                            : starsY - (int)detailFont.GetHeight(gr) - 1;
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
                BiasState.StrongLong  => Color.FromArgb(255, 0,  210,  80),
                BiasState.Long        => Color.FromArgb(255, 0,  170,  60),
                BiasState.Neutral     => Color.FromArgb(255, 160,160, 160),
                BiasState.Short       => Color.FromArgb(255, 210, 50,  50),
                BiasState.StrongShort => Color.FromArgb(255, 230, 20,  20),
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

            string ema20str  = _barCount > 0  ? $"{_ema20.ToString("F2")}{(_ema20Init  ? "" : "*")}" : "…";
            string ema50str  = _barCount > 0  ? $"{_ema50.ToString("F2")}{(_ema50Init  ? "" : "*")}" : "…";
            string ema200str = _barCount > 0  ? $"{_ema200.ToString("F2")}{(_ema200Init ? "" : "*")}" : "…";
            string vwapStr   = _vwapCumVol > 0 ? _currentVwap.ToString("F2") : "…";
            string clusterTag = _volumeAnalysisLoaded ? "" : " [proxy]";

            var lines = new[]
            {
                $"BIAS: {biasText}{arrow}{clusterTag}",
                $"EMA20:  {ema20str}",
                $"EMA50:  {ema50str}",
                $"EMA200: {ema200str}",
                $"VWAP:   {vwapStr}",
                $"Bars: {_barCount}"
            };

            using var boxFont  = new Font("Consolas", 8f, FontStyle.Bold);
            using var valFont  = new Font("Consolas", 8f, FontStyle.Regular);

            float lineH = boxFont.GetHeight(gr) + 2;
            float boxW  = 180f;
            float boxH  = lineH * lines.Length + 10;
            float boxX  = rect.Left + 8;
            float boxY  = rect.Top  + 8;

            using var bgBrush   = new SolidBrush(Color.FromArgb(180, 10, 10, 20));
            using var borderPen = new Pen(biasColor, 1.5f);
            gr.FillRectangle(bgBrush,   boxX, boxY, boxW, boxH);
            gr.DrawRectangle(borderPen, boxX, boxY, boxW, boxH);

            using var biasBrush  = new SolidBrush(biasColor);
            using var whiteBrush = new SolidBrush(Color.FromArgb(220, 220, 220, 220));

            for (int i = 0; i < lines.Length; i++)
            {
                gr.DrawString(lines[i],
                    i == 0 ? boxFont : valFont,
                    i == 0 ? biasBrush : whiteBrush,
                    boxX + 6, boxY + 5 + i * lineH);
            }
        }
    }
}
