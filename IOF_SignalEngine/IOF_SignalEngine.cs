// =============================================================================
// IOF_SignalEngine.cs — Ultimate Order Flow Confluence Signal Indicator
// =============================================================================
// Platform : Quantower C# SDK (net8.0, TradingPlatform.BusinessLayer v1.145.x)
// Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF
//
// Reads IOFZoneRegistry (populated by TradePhantoms_IOF_v2) and synthesizes
// all six order-flow confluence dimensions into a single scored signal at
// every zone-touch event. No zone = no signal. Order flow is confirmation at
// a level, not a standalone signal.
//
// SCORE COMPONENTS (100 pts):
//   ZoneQ   (0-25) Zone rubric score (/21 scaled). First-test freshness
//                  modifier applied on top (×1.0 / ×0.75 / ×0.4).
//   DeltaQ  (0-20) Current-bar delta direction (10) + multi-bar CVD slope (10).
//   AbsQ    (0-20) Absorption: heavy |delta| opposing zone + price stalling.
//                  Full signal=20, partial=10, range-only=6.
//   DayQ    (0-15) IB acceptance direction (8) + session CVD alignment (7).
//   MetQ    (0-10) ZoneMetricsRegistry: MTFC overlap (5) + HVN confluence (5).
//   FreshQ  (0-10) Touch-count freshness: 0 touches=10, 1=5, 2+=0.
//
// GRADES: A+(≥80) A(65-79) B(50-64) C(35-49). Events below MinDisplayScore hidden.
//
// PHASE 2: Enable CSV log → every zone-touch event written to disk for
//          Python post-processor statistical discovery.
//
// REQUIRES: TradePhantoms_IOF_v2 loaded on the same chart (populates
//           IOFZoneRegistry + ZoneMetricsRegistry). Works without it —
//           ZoneQ and MetQ components return 0 and no signals fire.
//
// -----------------------------------------------------------------------------
// PATCH NOTES
// -----------------------------------------------------------------------------
// 2026-06-09: Initial build.
//   - Six-component confluence scorer wired end-to-end
//   - IVolumeAnalysisIndicator for real delta access (not tick-rule synthetic)
//   - Initial Balance tracker: session-aware, period-aware bar count
//   - Absorption detector: mirrors ComputeAbsorptionFlag from IOF v2
//   - Zone touch de-duplication: transition-gated (outside→inside fires once)
//   - Touch count tracked per zone ID — freshness degrades over retests
//   - Colored diamond markers (demand=upward, supply=downward) + grade label
//   - Optional breakdown line: Z/D/A/S/M/F component scores
//   - Core.Instance.Loggers alert on events >= MinAlertScore
//   - Phase 2 CSV: one row per signal, all components + bar context
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
        public int MinDisplayScore = 35;

        [InputParameter("Min score to alert", 1, 0, 100, 5, 0)]
        public int MinAlertScore = 65;

        [InputParameter("Zone proximity (ticks)", 2, 1, 50, 1, 0)]
        public int ProximityTicks = 6;

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

        [InputParameter("Show component breakdown (Z/D/A/S/M/F)", 31)]
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

        // ── Private state ─────────────────────────────────────────────────────────

        private string _regKey   = "";
        private double _tickSize = 0.25;
        private bool   _vaLoaded = false;

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
            Description    = "IOF zone-touch confluence scorer — six-component order flow signal engine";
            SeparateWindow = false;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        protected override void OnInit()
        {
            _regKey   = BuildRegKey();
            _tickSize = Symbol?.TickSize > 0 ? Symbol.TickSize : 0.25;

            _prevInProx.Clear();
            _touchCounts.Clear();
            ResetSession();

            lock (_sigLock) _signals.Clear();
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (Count < 3) return;

            // Update IB on every tick using the current bar
            var currentBar = HistoricalData[0] as HistoryItemBar;
            if (currentBar == null) return;

            UpdateIB(currentBar);

            // Zone touches only on closed bars
            if (args.Reason != UpdateReason.NewBar && args.Reason != UpdateReason.HistoricalBar)
                return;

            // On the very first historical bar, initialize proximity state without firing
            if (Count == 3)
            {
                SeedProximity(currentBar);
                return;
            }

            EvaluateZoneTouches(currentBar);
        }

        protected override void OnClear()
        {
            CloseCsv();
            lock (_sigLock) _signals.Clear();
            _prevInProx.Clear();
            _touchCounts.Clear();
        }

        // ── Zone Touch Engine ─────────────────────────────────────────────────────

        private void EvaluateZoneTouches(HistoryItemBar bar)
        {
            var zones = IOFZoneRegistry.GetZones(_regKey);
            if (zones == null || zones.Count == 0)
            {
                foreach (var k in _prevInProx.Keys.ToList())
                    _prevInProx[k] = false;
                return;
            }

            double price = bar.Close;
            double prox  = ProximityTicks * _tickSize;

            foreach (var zone in zones)
            {
                if (!zone.IsTradeable) continue;

                string zid      = ZoneKey(zone);
                bool   isDemand = zone.Type == ZoneType.RBR || zone.Type == ZoneType.DBR;

                bool inProx = price >= zone.Bottom - prox && price <= zone.Top + prox;

                _prevInProx.TryGetValue(zid, out bool wasInProx);
                _prevInProx[zid] = inProx;

                if (!inProx || wasInProx) continue;

                // Transition: outside → inside. Fire touch event.
                _touchCounts.TryGetValue(zid, out int prevTouches);
                _touchCounts[zid] = prevTouches + 1;

                var result = Score(bar, zone, isDemand, prevTouches);
                if (result.Total < MinDisplayScore) continue;

                double sigPrice = isDemand ? zone.Bottom : zone.Top;

                var sig = new SeSignal
                {
                    BarOffset = 0,
                    BarTime   = bar.TimeLeft,
                    Price     = sigPrice,
                    IsDemand  = isDemand,
                    ZoneId    = zid,
                    Total     = result.Total,
                    Grade     = result.Grade,
                    ZoneQ     = result.ZoneQ,
                    DeltaQ    = result.DeltaQ,
                    AbsQ      = result.AbsQ,
                    DayQ      = result.DayQ,
                    MetQ      = result.MetQ,
                    FreshQ    = result.FreshQ,
                    TouchNum  = prevTouches + 1,
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
            var zones = IOFZoneRegistry.GetZones(_regKey);
            if (zones == null) return;
            double price = bar.Close;
            double prox  = ProximityTicks * _tickSize;
            foreach (var zone in zones)
                _prevInProx[ZoneKey(zone)] = price >= zone.Bottom - prox && price <= zone.Top + prox;
        }

        // ── Six-Component Scorer ──────────────────────────────────────────────────

        private ScoreResult Score(HistoryItemBar bar, ZoneSnapshot zone, bool isDemand, int prevTouches)
        {
            // 1 — Zone quality (0-25): rubric score /21 scaled, freshness-weighted
            double rawZoneQ = zone.Score / 21.0 * 25.0;
            double fresh = prevTouches == 0 ? 1.0 : prevTouches == 1 ? 0.75 : 0.40;
            int zoneQ = Clamp((int)Math.Round(rawZoneQ * fresh), 0, 25);

            // 2 — Delta alignment (0-20)
            int deltaQ = ScoreDelta(bar, isDemand);

            // 3 — Absorption (0-20)
            int absQ = ScoreAbsorption(bar, isDemand);

            // 4 — Day structure / IB + session CVD (0-15)
            int dayQ = ScoreDay(bar, isDemand);

            // 5 — Zone metrics: MTFC + HVN (0-10)
            int metQ = ScoreMetrics(zone);

            // 6 — Freshness bonus (0-10)
            int freshQ = prevTouches == 0 ? 10 : prevTouches == 1 ? 5 : 0;

            int total = zoneQ + deltaQ + absQ + dayQ + metQ + freshQ;

            string grade = total >= 80 ? "A+"
                         : total >= 65 ? "A"
                         : total >= 50 ? "B"
                         : total >= 35 ? "C"
                         : "X";

            return new ScoreResult(total, grade, zoneQ, deltaQ, absQ, dayQ, metQ, freshQ);
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

            // CVD slope = sum of last CvdLookback closed bars' delta
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
        // Pattern mirrors ComputeAbsorptionFlag in TradePhantoms_IOF_v2.cs
        private int ScoreAbsorption(HistoryItemBar bar, bool isDemand)
        {
            if (!_vaLoaded) return 0;

            double barDelta = GetBarDelta(bar);
            if (barDelta == 0) return 0;

            // For demand zones: expect SELLING delta (negative) that fails to push price down
            // For supply zones: expect BUYING delta (positive) that fails to push price up
            bool opposingDelta = isDemand ? barDelta < 0 : barDelta > 0;
            if (!opposingDelta) return 0;

            // The close should not have gone in the delta's direction → absorption confirmed
            bool priceHeld = isDemand
                ? bar.Close >= bar.Open   // selling delta but price held up
                : bar.Close <= bar.Open;  // buying delta but price held down

            // Compute ATR
            double atr = GetAtr();
            bool rangeStalled = atr > 0 && (bar.High - bar.Low) < AbsRangeStall * atr;

            // Compute avg |delta| over AvgDeltaPeriod
            double sumAbsDelta = 0;
            int cnt = 0;
            for (int i = 1; i <= Math.Min(AvgDeltaPeriod, Count - 1); i++)
            {
                var b = HistoricalData[i] as HistoryItemBar;
                if (b == null) continue;
                double d = GetBarDelta(b);
                sumAbsDelta += Math.Abs(d);
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
                // IB accepted higher = price closed above IB high = bullish bias → favor demand
                // IB accepted lower  = price closed below IB low  = bearish bias → favor supply
                if (isDemand  && bar.Close > _ibHigh) score += 8;
                if (!isDemand && bar.Close < _ibLow)  score += 8;
                // Inside IB = balanced: no penalty, no bonus
            }
            else if (!_ibFormed)
            {
                // IB still forming — if price is above session open midpoint favor demand, below favor supply
                score += 4; // neutral partial credit while IB forms
            }

            // Session CVD: sum all bars from the start of the current session window
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

        // MTFC (5) + HVN (5) from ZoneMetricsRegistry = max 10
        private int ScoreMetrics(ZoneSnapshot zone)
        {
            if (!ZoneMetricsRegistry.TryGet(_regKey, zone.Top, zone.Bottom, out var met))
                return 0;
            int score = 0;
            if (met.MtfcBonus > 0) score += 5;
            if (met.HvnConfluence)  score += 5;
            return score;
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
                // Expect strings like "1 Min", "5 Min", "15 Min", "1 Hour", etc.
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
            return 4; // safe fallback
        }

        // ── Helper: Bar Delta (real VA or zero if not loaded) ─────────────────────

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

        // ── Helper: ATR (true range, SMA over AtrPeriod) ─────────────────────────

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

        // ── Helper: Eastern Time ──────────────────────────────────────────────────

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

            List<SeSignal> snapshot;
            lock (_sigLock) snapshot = new List<SeSignal>(_signals);
            if (snapshot.Count == 0) return;

            var gr  = args.Graphics;
            var win = CurrentChart.MainWindow;

            using var labelFont = new Font("Consolas", 8f, FontStyle.Bold);
            using var detFont   = new Font("Consolas", 7f, FontStyle.Regular);

            foreach (var sig in snapshot)
            {
                try
                {
                    int x = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(sig.BarTime));
                    int y = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(sig.Price));
                    if (x < 0 || x > args.Rectangle.Width + 200) continue;

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
                            string det = $"Z{sig.ZoneQ} D{sig.DeltaQ} A{sig.AbsQ} S{sig.DayQ} M{sig.MetQ} F{sig.FreshQ}";
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
            // Demand: diamond points UP from zone bottom (arrow up into zone)
            // Supply: diamond points DOWN from zone top (arrow down into zone)
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
                             $"S:{sig.DayQ} M:{sig.MetQ} F:{sig.FreshQ}";
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
                        "ts,symbol,tf,price,zone_id,is_demand,zone_score_raw,zone_type," +
                        "touch_num,total,grade,zone_q,delta_q,abs_q,day_q,met_q,fresh_q," +
                        "ib_formed,ib_high,ib_low,bar_delta,bar_close,bar_open,bar_high,bar_low,bar_volume");
            }
            catch { _csvWriter = null; }
        }

        private void TryLog(SeSignal sig, HistoryItemBar bar)
        {
            try
            {
                EnsureCsv();
                if (_csvWriter == null) return;
                double delta = GetBarDelta(bar);
                _csvWriter.WriteLine(
                    $"{sig.BarTime:yyyy-MM-dd HH:mm:ss}," +
                    $"{Symbol?.Name ?? ""}," +
                    $"{HistoricalData?.Aggregation?.ToString() ?? ""}," +
                    $"{sig.Price:F2}," +
                    $"{sig.ZoneId}," +
                    $"{(sig.IsDemand ? 1 : 0)}," +
                    $"{sig.ZoneQ:F1}," +
                    $"{(sig.IsDemand ? "demand" : "supply")}," +
                    $"{sig.TouchNum}," +
                    $"{sig.Total}," +
                    $"{sig.Grade}," +
                    $"{sig.ZoneQ},{sig.DeltaQ},{sig.AbsQ},{sig.DayQ},{sig.MetQ},{sig.FreshQ}," +
                    $"{(_ibFormed ? 1 : 0)}," +
                    $"{(_ibFormed ? _ibHigh.ToString("F2") : "")}," +
                    $"{(_ibFormed ? _ibLow.ToString("F2") : "")}," +
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

        private string BuildRegKey()
        {
            try
            {
                string sym    = Symbol?.Name ?? "UNK";
                string period = HistoricalData?.Aggregation?.ToString() ?? "UNK";
                return $"{sym}_{period}";
            }
            catch { return ""; }
        }

        private static string ZoneKey(ZoneSnapshot z)
            => $"{z.Type}_{z.Top:F4}_{z.Bottom:F4}";

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
            public int      ZoneQ, DeltaQ, AbsQ, DayQ, MetQ, FreshQ;
            public int      TouchNum;
        }

        private readonly record struct ScoreResult(
            int Total, string Grade,
            int ZoneQ, int DeltaQ, int AbsQ, int DayQ, int MetQ, int FreshQ);
    }
}
