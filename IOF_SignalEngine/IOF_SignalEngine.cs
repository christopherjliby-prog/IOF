// =============================================================================
// IOF_SignalEngine.cs — Ultimate Order Flow Confluence Signal Indicator
// =============================================================================
// Platform : Quantower C# SDK (net8.0, TradingPlatform.BusinessLayer v1.145.x)
// Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF_SignalEngine\
//
// STANDALONE — no dependency on TradePhantoms_IOF_v2 or IOFZoneRegistry.
// Detects its own supply/demand zones from price action (pivot swing method)
// and fires confluence-scored signals on zone-touch events.
//
// SCORE COMPONENTS (100 pts):
//   ZoneQ   (0-25) Zone rubric: departure strength, base tightness, approach.
//   DeltaQ  (0-20) Current-bar delta (10) + multi-bar CVD slope (10).
//   AbsQ    (0-20) Absorption: heavy opposing delta + price stalling.
//   DayQ    (0-15) IB acceptance direction (8) + session CVD alignment (7).
//   FreshQ  (0-20) Touch freshness: first=20, second=10, third+=0.
//
// GRADES: A+(≥80)  A(65-79)  B(50-64)  C(35-49). Below MinDisplayScore hidden.
//
// PHASE 2: Enable CSV log → every signal written to disk for
//          Python post-processor (phase2_analyze.py) statistical discovery.
//
// -----------------------------------------------------------------------------
// PATCH NOTES
// -----------------------------------------------------------------------------
// 2026-06-09: v1 — IOFZoneRegistry-based (required IOF v2 on same chart)
// 2026-06-09: v2 — Standalone. Built-in pivot zone scanner, no external deps.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    public class IOF_SignalEngine : Indicator, IVolumeAnalysisIndicator
    {
        // ── Inputs: Signal Thresholds ─────────────────────────────────────────────

        [InputParameter("Min score to display (0-100)", 0, 0, 100, 5, 0)]
        public int MinDisplayScore = 20;

        [InputParameter("Min score to alert", 1, 0, 100, 5, 0)]
        public int MinAlertScore = 65;

        [InputParameter("Zone proximity (ticks)", 2, 1, 50, 1, 0)]
        public int ProximityTicks = 6;

        // ── Inputs: Zone Detection ────────────────────────────────────────────────

        [InputParameter("Zone lookback (bars)", 3, 20, 500, 10, 0)]
        public int ZoneLookback = 150;

        [InputParameter("Zone pivot strength (bars each side)", 4, 1, 10, 1, 0)]
        public int ZoneStrength = 3;

        [InputParameter("Zone rescan interval (bars)", 5, 1, 50, 1, 0)]
        public int ZoneRescanInterval = 5;

        // ── Inputs: Session / IB ─────────────────────────────────────────────────

        [InputParameter("Session open hour ET (RTH=9)", 10, 0, 23, 1, 0)]
        public int SessionHour = 9;

        [InputParameter("Session open minute ET (RTH=30)", 11, 0, 59, 1, 0)]
        public int SessionMin = 30;

        [InputParameter("Initial balance period (minutes)", 12, 15, 240, 15, 0)]
        public int IbMinutes = 60;

        // ── Inputs: Delta / Absorption ───────────────────────────────────────────

        [InputParameter("CVD slope lookback (bars)", 20, 2, 30, 1, 0)]
        public int CvdLookback = 5;

        [InputParameter("Absorption: |delta| × avg threshold", 21, 1.0, 5.0, 0.1, 1)]
        public double AbsMultiplier = 1.5;

        [InputParameter("Absorption: bar range stall (× ATR)", 22, 0.1, 1.0, 0.05, 2)]
        public double AbsRangeStall = 0.55;

        [InputParameter("ATR period (bars)", 23, 5, 50, 1, 0)]
        public int AtrPeriod = 14;

        [InputParameter("Avg delta period (bars)", 24, 5, 50, 1, 0)]
        public int AvgDeltaPeriod = 20;

        // ── Inputs: Visual ───────────────────────────────────────────────────────

        [InputParameter("Show grade label", 30)]
        public bool ShowLabels = true;

        [InputParameter("Show component breakdown (Z/D/A/S/F)", 31)]
        public bool ShowBreakdown = false;

        [InputParameter("Marker size", 32, 4, 24, 1, 0)]
        public int MarkerSize = 10;

        [InputParameter("A+ color", 40)]
        public Color AplusColor = Color.Gold;

        [InputParameter("A color", 41)]
        public Color AColor = Color.FromArgb(0, 210, 255);

        [InputParameter("B color", 42)]
        public Color BColor = Color.FromArgb(80, 210, 80);

        [InputParameter("C color", 43)]
        public Color CColor = Color.FromArgb(200, 130, 50);

        // ── Inputs: Phase 2 Logging ──────────────────────────────────────────────

        [InputParameter("Phase 2: enable CSV log", 50)]
        public bool LogEnabled = false;

        [InputParameter("Phase 2: CSV path", 51)]
        public string CsvPath = @"D:\Custom\iof_discovery\out\signals_raw.csv";

        // ── IVolumeAnalysisIndicator ──────────────────────────────────────────────

        public bool IsRequirePriceLevelsCalculation => false;

        public void VolumeAnalysisData_Loaded()
        {
            _vaLoaded = true;
        }

        // ── Internal zone type ────────────────────────────────────────────────────

        private sealed class LocalZone
        {
            public double Top;
            public double Bottom;
            public bool   IsDemand;   // true = demand (support), false = supply (resistance)
            public double Score;      // 0-21, maps to ZoneQ via score/21*25
            public int    TouchCount;

            public string Key => $"{(IsDemand ? "D" : "S")}_{Top:F4}_{Bottom:F4}";
        }

        // ── Private state ─────────────────────────────────────────────────────────

        private double _tickSize = 0.25;
        private bool   _vaLoaded = false;
        private int    _barsSinceZoneScan = 0;

        private List<LocalZone> _localZones = new();
        private readonly object _zoneLock   = new();

        // Transition-gate: zoneKey → was inside proximity last closed bar
        private readonly Dictionary<string, bool> _prevInProx  = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int>  _touchCounts = new(StringComparer.Ordinal);

        // Session / IB state
        private DateTime _sessionDate  = DateTime.MinValue;
        private double   _ibHigh       = double.NaN;
        private double   _ibLow        = double.NaN;
        private bool     _ibFormed     = false;
        private int      _ibBarCount   = 0;
        private int      _ibTargetBars = 4;

        // Signals stored for paint pass
        private readonly List<SeSignal> _signals = new();
        private readonly object         _sigLock = new();

        // Phase 2 CSV
        private StreamWriter _csvWriter    = null;
        private string       _openCsvPath  = "";

        // ── Constructor ───────────────────────────────────────────────────────────

        public IOF_SignalEngine() : base()
        {
            Name           = "IOF_SignalEngine";
            Description    = "IOF standalone confluence signal engine — built-in zone detection + six-component scorer";
            SeparateWindow = false;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        protected override void OnInit()
        {
            _tickSize = Symbol?.TickSize > 0 ? Symbol.TickSize : 0.25;

            _prevInProx.Clear();
            _touchCounts.Clear();
            ResetSession();

            lock (_sigLock)  _signals.Clear();
            lock (_zoneLock) _localZones.Clear();

            _barsSinceZoneScan = 0;
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (Count < ZoneStrength * 2 + 3) return;

            var currentBar = HistoricalData[0] as HistoryItemBar;
            if (currentBar == null) return;

            UpdateIB(currentBar);

            if (args.Reason != UpdateReason.NewBar && args.Reason != UpdateReason.HistoricalBar)
                return;

            // Rescan zones periodically
            _barsSinceZoneScan++;
            if (_barsSinceZoneScan >= ZoneRescanInterval || _localZones.Count == 0)
            {
                ScanLocalZones();
                _barsSinceZoneScan = 0;
            }

            if (Count == ZoneStrength * 2 + 3)
            {
                SeedProximity(currentBar);
                return;
            }

            EvaluateZoneTouches(currentBar);
        }

        protected override void OnClear()
        {
            CloseCsv();
            lock (_sigLock)  _signals.Clear();
            lock (_zoneLock) _localZones.Clear();
            _prevInProx.Clear();
            _touchCounts.Clear();
        }

        // ── Zone Scanner (pivot swing method) ────────────────────────────────────

        private void ScanLocalZones()
        {
            if (Count < ZoneStrength * 2 + 3) return;

            var zones = new List<LocalZone>();
            int limit = Math.Min(ZoneLookback, Count - ZoneStrength - 2);

            for (int i = ZoneStrength; i < limit; i++)
            {
                var bar = HistoricalData[i] as HistoryItemBar;
                if (bar == null) continue;

                // ── Pivot High → supply zone ──────────────────────────────────
                bool isPivotHigh = true;
                for (int j = 1; j <= ZoneStrength && isPivotHigh; j++)
                {
                    var l = HistoricalData[i + j] as HistoryItemBar;
                    var r = HistoricalData[i - j] as HistoryItemBar;
                    if (l == null || r == null || l.High >= bar.High || r.High >= bar.High)
                        isPivotHigh = false;
                }
                if (isPivotHigh)
                {
                    double top    = bar.High;
                    double bottom = Math.Max(bar.Open, bar.Close);
                    if (top > bottom + _tickSize)
                        zones.Add(new LocalZone
                        {
                            Top      = top,
                            Bottom   = bottom,
                            IsDemand = false,
                            Score    = ComputeZoneScore(i, isDemand: false),
                        });
                }

                // ── Pivot Low → demand zone ───────────────────────────────────
                bool isPivotLow = true;
                for (int j = 1; j <= ZoneStrength && isPivotLow; j++)
                {
                    var l = HistoricalData[i + j] as HistoryItemBar;
                    var r = HistoricalData[i - j] as HistoryItemBar;
                    if (l == null || r == null || l.Low <= bar.Low || r.Low <= bar.Low)
                        isPivotLow = false;
                }
                if (isPivotLow)
                {
                    double bottom = bar.Low;
                    double top    = Math.Min(bar.Open, bar.Close);
                    if (top > bottom + _tickSize)
                        zones.Add(new LocalZone
                        {
                            Top      = top,
                            Bottom   = bottom,
                            IsDemand = true,
                            Score    = ComputeZoneScore(i, isDemand: true),
                        });
                }
            }

            // Preserve touch counts for zones that survived the rescan
            lock (_zoneLock)
            {
                foreach (var z in zones)
                {
                    _touchCounts.TryGetValue(z.Key, out int tc);
                    z.TouchCount = tc;
                }
                _localZones = zones;
            }
        }

        // Zone quality score 0-21 used by ZoneQ formula (score/21*25)
        private double ComputeZoneScore(int barIndex, bool isDemand)
        {
            var pivot = HistoricalData[barIndex] as HistoryItemBar;
            if (pivot == null) return 10.0;

            double atr = GetAtr();

            // Departure (0-10): measure bars JUST AFTER zone formation
            // (lower index = more recent; bars at barIndex-1 to barIndex-6 are the
            //  bars that formed AFTER the pivot, showing how far price moved away)
            double departure = 0;
            for (int j = 1; j <= Math.Min(6, barIndex - 1); j++)
            {
                var b = HistoricalData[barIndex - j] as HistoryItemBar;
                if (b == null) break;
                double dist = isDemand
                    ? b.Close - pivot.Low
                    : pivot.High - b.Close;
                if (dist > departure) departure = dist;
            }
            double depScore = atr > 0 ? Math.Min(10.0, departure / atr * 4.0) : 5.0;

            // Tightness (0-6): narrow body relative to range = clean base
            double range    = pivot.High - pivot.Low;
            double bodySize = Math.Abs(pivot.Close - pivot.Open);
            double tightScore = range > 0 ? (1.0 - bodySize / range) * 6.0 : 3.0;

            // Approach momentum (0-5): strong move INTO the zone = meaningful level
            var prev = HistoricalData[barIndex + 1] as HistoryItemBar;
            double momScore = 0;
            if (prev != null)
            {
                bool movingIn = isDemand
                    ? prev.Close < prev.Open
                    : prev.Close > prev.Open;
                double prevBody = Math.Abs(prev.Close - prev.Open);
                momScore = movingIn ? (atr > 0 ? Math.Min(5.0, prevBody / atr * 3.0) : 3.0) : 1.0;
            }

            return Math.Max(1.0, Math.Min(21.0, depScore + tightScore + momScore));
        }

        // ── Zone Touch Engine ─────────────────────────────────────────────────────

        private void EvaluateZoneTouches(HistoryItemBar bar)
        {
            List<LocalZone> zones;
            lock (_zoneLock) zones = new List<LocalZone>(_localZones);

            if (zones.Count == 0) return;

            double price = bar.Close;
            double prox  = ProximityTicks * _tickSize;

            foreach (var zone in zones)
            {
                string zid = zone.Key;

                bool inProx = price >= zone.Bottom - prox && price <= zone.Top + prox;

                _prevInProx.TryGetValue(zid, out bool wasInProx);
                _prevInProx[zid] = inProx;

                if (!inProx || wasInProx) continue;

                // Transition: outside → inside proximity. Fire touch event.
                _touchCounts.TryGetValue(zid, out int prevTouches);
                _touchCounts[zid] = prevTouches + 1;

                var result = Score(bar, zone, prevTouches);
                if (result.Total < MinDisplayScore) continue;

                double sigPrice = zone.IsDemand ? zone.Bottom : zone.Top;

                var sig = new SeSignal
                {
                    BarOffset  = 0,
                    BarTime    = bar.TimeLeft,
                    Price      = sigPrice,
                    IsDemand   = zone.IsDemand,
                    ZoneId     = zid,
                    Total      = result.Total,
                    Grade      = result.Grade,
                    ZoneQ      = result.ZoneQ,
                    DeltaQ     = result.DeltaQ,
                    AbsQ       = result.AbsQ,
                    DayQ       = result.DayQ,
                    FreshQ     = result.FreshQ,
                    TouchNum   = prevTouches + 1,
                    ZoneScoreRaw = zone.Score,
                };

                lock (_sigLock) _signals.Add(sig);

                if (result.Total >= MinAlertScore)
                    FireAlert(sig);

                if (LogEnabled)
                    TryLog(sig, bar);
            }
        }

        private void SeedProximity(HistoryItemBar bar)
        {
            List<LocalZone> zones;
            lock (_zoneLock) zones = new List<LocalZone>(_localZones);

            double price = bar.Close;
            double prox  = ProximityTicks * _tickSize;
            foreach (var zone in zones)
                _prevInProx[zone.Key] = price >= zone.Bottom - prox && price <= zone.Top + prox;
        }

        // ── Six-Component Scorer ──────────────────────────────────────────────────

        private ScoreResult Score(HistoryItemBar bar, LocalZone zone, int prevTouches)
        {
            // 1 — Zone quality (0-25): rubric score /21 scaled
            double fresh = prevTouches == 0 ? 1.0 : prevTouches == 1 ? 0.75 : 0.40;
            int zoneQ = Clamp((int)Math.Round(zone.Score / 21.0 * 25.0 * fresh), 0, 25);

            // 2 — Delta alignment (0-20)
            int deltaQ = ScoreDelta(bar, zone.IsDemand);

            // 3 — Absorption (0-20)
            int absQ = ScoreAbsorption(bar, zone.IsDemand);

            // 4 — Day structure / IB + session CVD (0-15)
            int dayQ = ScoreDay(bar, zone.IsDemand);

            // 5 — Freshness (0-20): standalone engine uses wider freshness range
            int freshQ = prevTouches == 0 ? 20 : prevTouches == 1 ? 10 : 0;

            int total = zoneQ + deltaQ + absQ + dayQ + freshQ;

            string grade = total >= 80 ? "A+"
                         : total >= 65 ? "A"
                         : total >= 50 ? "B"
                         : total >= 35 ? "C"
                         : "X";

            return new ScoreResult(total, grade, zoneQ, deltaQ, absQ, dayQ, freshQ);
        }

        // Delta alignment: current-bar delta (10) + rolling CVD slope (10) = max 20
        private int ScoreDelta(HistoryItemBar bar, bool isDemand)
        {
            if (!_vaLoaded) return 0;
            int score = 0;

            double barDelta = GetBarDelta(bar);
            if (barDelta != 0)
            {
                bool aligned = isDemand ? barDelta > 0 : barDelta < 0;
                if (aligned) score += 10;
            }

            double cvd = 0;
            int n = Math.Min(CvdLookback, Count - 1);
            for (int i = 1; i <= n; i++)
            {
                var b = HistoricalData[i] as HistoryItemBar;
                if (b == null) continue;
                cvd += GetBarDelta(b);
            }
            if (cvd != 0)
            {
                bool aligned = isDemand ? cvd > 0 : cvd < 0;
                if (aligned) score += 10;
            }

            return score;
        }

        // Absorption: heavy opposing delta + price stalling = max 20
        private int ScoreAbsorption(HistoryItemBar bar, bool isDemand)
        {
            if (!_vaLoaded) return 0;

            double barDelta = GetBarDelta(bar);
            if (barDelta == 0) return 0;

            bool opposingDelta = isDemand ? barDelta < 0 : barDelta > 0;
            if (!opposingDelta) return 0;

            bool priceHeld = isDemand
                ? bar.Close >= bar.Open
                : bar.Close <= bar.Open;

            double atr = GetAtr();
            bool rangeStalled = atr > 0 && (bar.High - bar.Low) < AbsRangeStall * atr;

            double sumAbsDelta = 0;
            int cnt = 0;
            for (int i = 1; i <= Math.Min(AvgDeltaPeriod, Count - 1); i++)
            {
                var b = HistoricalData[i] as HistoryItemBar;
                if (b == null) continue;
                sumAbsDelta += Math.Abs(GetBarDelta(b));
                cnt++;
            }
            double avgAbsDelta = cnt > 0 ? sumAbsDelta / cnt : 0;
            bool heavyDelta = avgAbsDelta > 0 && Math.Abs(barDelta) > AbsMultiplier * avgAbsDelta;

            if (rangeStalled && heavyDelta && priceHeld) return 20;
            if (heavyDelta && priceHeld)                 return 12;
            if (rangeStalled && priceHeld)               return 8;
            if (priceHeld)                               return 4;
            return 0;
        }

        // Day structure: IB acceptance (8) + session CVD direction (7) = max 15
        private int ScoreDay(HistoryItemBar bar, bool isDemand)
        {
            int score = 0;

            if (_ibFormed && !double.IsNaN(_ibHigh) && !double.IsNaN(_ibLow))
            {
                if (isDemand  && bar.Close > _ibHigh) score += 8;
                if (!isDemand && bar.Close < _ibLow)  score += 8;
            }
            else if (!_ibFormed)
            {
                score += 4;
            }

            if (_vaLoaded)
            {
                double sessionCvd = 0;
                int lookback = Math.Min(CvdLookback * 3, Count - 1);
                for (int i = 1; i <= lookback; i++)
                {
                    var b = HistoricalData[i] as HistoryItemBar;
                    if (b == null) continue;
                    sessionCvd += GetBarDelta(b);
                }
                bool cvdAligned = isDemand ? sessionCvd > 0 : sessionCvd < 0;
                if (cvdAligned) score += 7;
            }

            return Clamp(score, 0, 15);
        }

        // ── Initial Balance Tracker ───────────────────────────────────────────────

        private void UpdateIB(HistoryItemBar bar)
        {
            try
            {
                DateTime et  = ToEasternTime(bar.TimeLeft);
                DateTime day = et.Date;

                if (day != _sessionDate)
                {
                    _sessionDate = day;
                    ResetSession();
                    _ibTargetBars = ComputeIbTargetBars();
                }

                DateTime openTime = day + new TimeSpan(SessionHour, SessionMin, 0);
                if (et < openTime) return;

                if (_ibFormed) return;

                _ibBarCount++;
                if (double.IsNaN(_ibHigh) || bar.High > _ibHigh) _ibHigh = bar.High;
                if (double.IsNaN(_ibLow)  || bar.Low  < _ibLow)  _ibLow  = bar.Low;

                if (_ibBarCount >= _ibTargetBars)
                    _ibFormed = true;
            }
            catch { }
        }

        private void ResetSession()
        {
            _ibHigh     = double.NaN;
            _ibLow      = double.NaN;
            _ibFormed   = false;
            _ibBarCount = 0;
        }

        private int ComputeIbTargetBars()
        {
            try
            {
                string agg = HistoricalData?.Aggregation?.ToString() ?? "";
                if (agg.IndexOf("Min", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var parts = agg.Split(' ');
                    if (parts.Length > 0 && int.TryParse(parts[0], out int m) && m > 0)
                        return Math.Max(1, IbMinutes / m);
                }
                if (agg.IndexOf("Hour", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var parts = agg.Split(' ');
                    if (parts.Length > 0 && int.TryParse(parts[0], out int h) && h > 0)
                        return Math.Max(1, IbMinutes / (h * 60));
                }
            }
            catch { }
            return 4;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static double GetBarDelta(HistoryItemBar bar)
        {
            try
            {
                if (bar?.VolumeAnalysisData?.Total != null)
                    return bar.VolumeAnalysisData.Total.Delta;
            }
            catch { }
            return 0;
        }

        private double GetAtr()
        {
            if (Count < 3) return 0;
            double sum = 0;
            int cnt = 0;
            int limit = Math.Min(AtrPeriod, Count - 1);
            for (int i = 1; i <= limit; i++)
            {
                var bar  = HistoricalData[i]     as HistoryItemBar;
                var prev = HistoricalData[i + 1] as HistoryItemBar;
                if (bar == null) continue;
                double prevClose = prev?.Close ?? bar.Open;
                double tr = Math.Max(bar.High - bar.Low,
                            Math.Max(Math.Abs(bar.High - prevClose),
                                     Math.Abs(bar.Low  - prevClose)));
                sum += tr;
                cnt++;
            }
            return cnt > 0 ? sum / cnt : 0;
        }

        private static DateTime ToEasternTime(DateTime utc)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
            }
            catch
            {
                try
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                    return TimeZoneInfo.ConvertTimeFromUtc(
                        DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
                }
                catch { return utc; }
            }
        }

        // ── Rendering ─────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            var gr  = args.Graphics;
            var win = CurrentChart.MainWindow;

            // Status overlay — always visible so you can see the engine is running
            try
            {
                int    zoneCount = 0;
                lock (_zoneLock) zoneCount = _localZones.Count;
                int    sigCount  = 0;
                lock (_sigLock)  sigCount  = _signals.Count;

                using var sf = new Font("Consolas", 8f, FontStyle.Bold);
                string status = $"IOF SE | Zones: {zoneCount} | Signals: {sigCount} | Min: {MinDisplayScore} | VA: {(_vaLoaded ? "ON" : "OFF")}";
                gr.DrawString(status, sf, Brushes.Cyan, 6f, 22f);
            }
            catch { }

            List<SeSignal> snapshot;
            lock (_sigLock) snapshot = new List<SeSignal>(_signals);
            if (snapshot.Count == 0) return;

            using var labelFont = new Font("Consolas", 8f, FontStyle.Bold);
            using var detFont   = new Font("Consolas", 7f, FontStyle.Regular);

            var clientRect = (System.Drawing.Rectangle)win.ClientRectangle;

            foreach (var sig in snapshot)
            {
                try
                {
                    int x = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(sig.BarTime));
                    int y = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(sig.Price));
                    if (x < clientRect.Left - 20 || x > clientRect.Right + 20) continue;

                    Color c = sig.Grade switch
                    {
                        "A+" => AplusColor,
                        "A"  => AColor,
                        "B"  => BColor,
                        _    => CColor
                    };

                    DrawDiamond(gr, x, y, sig.IsDemand, c);

                    if (ShowLabels)
                    {
                        int hs = MarkerSize / 2;
                        int lx = x + hs + 3;
                        int ly = sig.IsDemand ? y + hs - 8 : y - hs - 14;

                        using var tb = new SolidBrush(c);
                        gr.DrawString($"{sig.Grade} {sig.Total}", labelFont, tb, lx, ly);
                        if (ShowBreakdown)
                        {
                            using var db = new SolidBrush(Color.FromArgb(170, c));
                            string det = $"Z{sig.ZoneQ} D{sig.DeltaQ} A{sig.AbsQ} S{sig.DayQ} F{sig.FreshQ}";
                            gr.DrawString(det, detFont, db, lx, ly + 11);
                        }
                    }
                }
                catch { }
            }
        }

        private void DrawDiamond(System.Drawing.Graphics gr, int x, int y, bool isDemand, Color c)
        {
            int h = MarkerSize;
            int w = MarkerSize / 2;
            Point[] pts = isDemand
                ? new[] { new Point(x,     y),
                          new Point(x + w, y + h),
                          new Point(x,     y + h * 2),
                          new Point(x - w, y + h) }
                : new[] { new Point(x,     y),
                          new Point(x + w, y - h),
                          new Point(x,     y - h * 2),
                          new Point(x - w, y - h) };

            using var fill = new SolidBrush(Color.FromArgb(210, c));
            using var edge = new Pen(Color.FromArgb(255, 255, 255), 1f);
            gr.FillPolygon(fill, pts);
            gr.DrawPolygon(edge, pts);
        }

        // ── Alert ─────────────────────────────────────────────────────────────────

        private void FireAlert(SeSignal sig)
        {
            try
            {
                string dir = sig.IsDemand ? "DEMAND" : "SUPPLY";
                string msg = $"[IOF Signal] {sig.Grade} {sig.Total}/100 | {dir} @ {sig.Price:F2} | " +
                             $"Touch#{sig.TouchNum} | Z:{sig.ZoneQ} D:{sig.DeltaQ} A:{sig.AbsQ} " +
                             $"S:{sig.DayQ} F:{sig.FreshQ}";
                Core.Instance.Loggers.Log(msg, LoggingLevel.Trading);
            }
            catch { }
        }

        // ── Phase 2 CSV ───────────────────────────────────────────────────────────

        private void EnsureCsv()
        {
            if (_csvWriter != null && _openCsvPath == CsvPath) return;
            CloseCsv();
            try
            {
                string dir = Path.GetDirectoryName(CsvPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                bool exists = File.Exists(CsvPath);
                _csvWriter   = new StreamWriter(CsvPath, append: true) { AutoFlush = true };
                _openCsvPath = CsvPath;
                if (!exists)
                    _csvWriter.WriteLine(
                        "ts,symbol,tf,price,zone_id,is_demand,zone_score_raw,touch_num," +
                        "total,grade,zone_q,delta_q,abs_q,day_q,fresh_q," +
                        "ib_formed,ib_high,ib_low," +
                        "bar_delta,bar_close,bar_open,bar_high,bar_low,bar_volume");
            }
            catch { }
        }

        private void TryLog(SeSignal sig, HistoryItemBar bar)
        {
            try
            {
                EnsureCsv();
                if (_csvWriter == null) return;
                double delta = GetBarDelta(bar);
                string sym = Symbol?.Name ?? "";
                string tf  = HistoricalData?.Aggregation?.ToString() ?? "";
                _csvWriter.WriteLine(
                    $"{sig.BarTime:yyyy-MM-dd HH:mm:ss}," +
                    $"{sym},{tf}," +
                    $"{sig.Price:F2}," +
                    $"{sig.ZoneId}," +
                    $"{(sig.IsDemand ? 1 : 0)}," +
                    $"{sig.ZoneScoreRaw:F2}," +
                    $"{sig.TouchNum}," +
                    $"{sig.Total}," +
                    $"{sig.Grade}," +
                    $"{sig.ZoneQ},{sig.DeltaQ},{sig.AbsQ},{sig.DayQ},{sig.FreshQ}," +
                    $"{(_ibFormed ? 1 : 0)}," +
                    $"{(_ibFormed ? _ibHigh.ToString("F2") : "")}," +
                    $"{(_ibFormed ? _ibLow.ToString("F2")  : "")}," +
                    $"{delta:F0},{bar.Close:F2},{bar.Open:F2},{bar.High:F2},{bar.Low:F2},{bar.Volume:F0}");
            }
            catch { }
        }

        private void CloseCsv()
        {
            try { _csvWriter?.Flush(); _csvWriter?.Dispose(); } catch { }
            _csvWriter   = null;
            _openCsvPath = "";
        }

        // ── Utility ───────────────────────────────────────────────────────────────

        private static int Clamp(int v, int lo, int hi)
            => v < lo ? lo : v > hi ? hi : v;

        // ── Supporting types ──────────────────────────────────────────────────────

        private sealed class SeSignal
        {
            public int      BarOffset;
            public DateTime BarTime;
            public double   Price;
            public bool     IsDemand;
            public string   ZoneId;
            public int      Total;
            public string   Grade;
            public int      ZoneQ, DeltaQ, AbsQ, DayQ, FreshQ;
            public int      TouchNum;
            public double   ZoneScoreRaw;
        }

        private readonly record struct ScoreResult(
            int Total, string Grade,
            int ZoneQ, int DeltaQ, int AbsQ, int DayQ, int FreshQ);
    }
}
