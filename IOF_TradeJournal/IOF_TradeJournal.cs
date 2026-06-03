// =============================================================================
// IOF_TradeJournal.cs — "WTF Are You Doing?!" Accountability Journal
// =============================================================================
// Platform : Quantower C# SDK (v1.143.x / TradingPlatform.BusinessLayer)
// Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF_TradeJournal\
//
// Phases implemented:
//   Phase 1: Position feed, CSV + JSON logging, session detection, chart overlay
//   Phase 2: IOFZoneRegistry lookup, ZoneMetricsRegistry, full A-F grading
//   Phase 3: EMA trend (ITF/HTF), ATR-20, MAE/MFE tick tracking, TP inference
//   Phase 4: EOD HTML dashboard with D:×/ABS:× tables, MTFC/HVN, freshness, time-of-day
//
// Dependencies:
//   - IOFZoneRegistry + ZoneMetricsRegistry (TradePhantoms_IOF_v2.dll) via reflection
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms.Journal
{
    public class IOF_TradeJournal : Indicator
    {
        // ── Parameters ────────────────────────────────────────────────────────

        [InputParameter("Log Directory", 0)]
        public string LogDirectory = @"C:\Quantower\Logs\IOF_Journal";

        [InputParameter("IOF Timeframe (period)", 1, 1, 1440, 1, 0)]
        public int IofTimeframe = 5;

        [InputParameter("Zone Proximity Ticks", 2, 1, 50, 1, 0)]
        public int ZoneProximityTicks = 8;

        [InputParameter("Commission Per Contract (RT)", 3, 0.0, 50.0, 0.05, 2)]
        public double CommissionPerContractRT = 0.90;

        [InputParameter("Enable CSV Log", 4)]
        public bool EnableCsvLog = true;

        [InputParameter("Enable JSON Log", 5)]
        public bool EnableJsonLog = true;

        [InputParameter("Enable HTML Dashboard", 6)]
        public bool EnableHtmlDashboard = true;

        [InputParameter("Show Overlay Labels", 7)]
        public bool ShowOverlayLabels = true;

        [InputParameter("Alert on Grade D/F", 8)]
        public bool AlertOnGradeDF = true;

        // ── Internal state ────────────────────────────────────────────────────

        // Open positions: positionId → in-progress JournalEntry
        private readonly Dictionary<string, JournalEntry> _pending = new();

        // Completed entries for overlay rendering
        private readonly List<JournalEntry> _completed = new();

        // Journal writer (re-created at midnight rollover)
        private JournalWriter _writer;
        private DateTime      _writerDate = DateTime.MinValue;

        // EOD HTML at 4 PM ET
        private bool _eodDone;
        private static readonly TimeZoneInfo _et = GetEasternTz();

        // Phase 3: MAE/MFE tick tracking (lock-protected — NewLast fires on IO thread)
        private readonly Dictionary<string, (double entryPrice, bool isLong, double mae, double mfe)> _maeTracking = new();
        private readonly object _maeLock = new object();

        // Phase 3: cached per-bar environment values (updated in OnUpdate)
        private double _cachedAtr20;
        private string _cachedItfTrend = "";
        private string _cachedHtfTrend = "";

        // Fonts / brushes for chart overlay
        private Font  _labelFont;
        private Brush _brushA, _brushB, _brushC, _brushD, _brushF, _brushQ, _brushExit, _brushBg;

        // ── Indicator lifecycle ───────────────────────────────────────────────

        protected override void OnInit()
        {
            Name        = "WTF Are You Doing?! — IOF Journal";
            Description = "Accountability partner — auto-journals every IOF trade.";
            SeparateWindow = false;
            AddLineSeries("Journal_anchor", Color.Transparent, 1, LineStyle.Solid);

            _labelFont = new Font("Consolas", 8f, FontStyle.Bold);
            _brushA    = new SolidBrush(Color.FromArgb(0, 200, 83));
            _brushB    = new SolidBrush(Color.FromArgb(0, 212, 255));
            _brushC    = new SolidBrush(Color.FromArgb(255, 214, 0));
            _brushD    = new SolidBrush(Color.FromArgb(255, 109, 0));
            _brushF    = new SolidBrush(Color.FromArgb(255, 23, 68));
            _brushQ    = new SolidBrush(Color.FromArgb(150, 150, 150));
            _brushExit = new SolidBrush(Color.FromArgb(200, 200, 200));
            _brushBg   = new SolidBrush(Color.FromArgb(160, 10, 10, 10));

            EnsureWriter();

            try { Core.Instance.PositionAdded  -= OnPositionAdded;  } catch { }
            try { Core.Instance.PositionRemoved -= OnPositionRemoved; } catch { }
            Core.Instance.PositionAdded  += OnPositionAdded;
            Core.Instance.PositionRemoved += OnPositionRemoved;

            // Phase 3: tick-level MAE/MFE tracking
            if (this.Symbol != null)
            {
                try { this.Symbol.NewLast -= OnNewLast_Journal; } catch { }
                this.Symbol.NewLast += OnNewLast_Journal;
            }

            Core.Instance.Loggers.Log("IOF Trade Journal: initialized.", LoggingLevel.System);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            // Phase 3: refresh cached environment state on each new bar
            _cachedAtr20    = ComputeAtr20();
            _cachedItfTrend = ComputeEmaTrend(50);
            _cachedHtfTrend = ComputeEmaTrend(200);

            // Phase 4: EOD HTML at 4:00 PM ET
            if (!_eodDone && EnableHtmlDashboard)
            {
                DateTime et = ToEasternTime(DateTime.UtcNow);
                if (et.TimeOfDay >= new TimeSpan(16, 0, 0))
                {
                    GenerateHtml();
                    _eodDone = true;
                }
            }

            // Midnight rollover — new writer for new day
            if (DateTime.UtcNow.Date != _writerDate)
            {
                _eodDone = false;
                EnsureWriter();
            }
        }

        protected override void OnClear()
        {
            try { Core.Instance.PositionAdded  -= OnPositionAdded;  } catch { }
            try { Core.Instance.PositionRemoved -= OnPositionRemoved; } catch { }
            if (this.Symbol != null)
                try { this.Symbol.NewLast -= OnNewLast_Journal; } catch { }
        }

        public override void Dispose()
        {
            try { Core.Instance.PositionAdded  -= OnPositionAdded;  } catch { }
            try { Core.Instance.PositionRemoved -= OnPositionRemoved; } catch { }
            if (this.Symbol != null)
                try { this.Symbol.NewLast -= OnNewLast_Journal; } catch { }

            if (EnableHtmlDashboard && _writer != null)
                try { GenerateHtml(); } catch { }

            _labelFont?.Dispose();
            (_brushA   as IDisposable)?.Dispose();
            (_brushB   as IDisposable)?.Dispose();
            (_brushC   as IDisposable)?.Dispose();
            (_brushD   as IDisposable)?.Dispose();
            (_brushF   as IDisposable)?.Dispose();
            (_brushQ   as IDisposable)?.Dispose();
            (_brushExit as IDisposable)?.Dispose();
            (_brushBg  as IDisposable)?.Dispose();

            base.Dispose();
        }

        // ── Position event handlers ───────────────────────────────────────────

        private void OnPositionAdded(Position position)
        {
            if (!IsOurSymbol(position)) return;
            try
            {
                string posId = position.Id?.ToString() ?? Guid.NewGuid().ToString();
                if (_pending.ContainsKey(posId)) return;

                bool   isLong     = position.Side == Side.Buy;
                double entryPrice = position.OpenPrice;
                int    contracts  = (int)Math.Abs(position.Quantity);
                DateTime entryUtc = DateTime.UtcNow;

                var entry = new JournalEntry
                {
                    PositionId      = posId,
                    TradeId         = BuildTradeId(entryUtc, this.Symbol.Name),
                    Date            = entryUtc.Date,
                    EntryTime       = entryUtc,
                    Session         = SessionClassifier.Classify(entryUtc),
                    TimeOfDayBucket = SessionClassifier.GetTimeBucket(entryUtc),
                    Symbol          = this.Symbol?.Name ?? "",
                    Direction       = isLong ? "LONG" : "SHORT",
                    Contracts       = contracts,
                    EntryPrice      = entryPrice,
                    IsComplete      = false,

                    // Phase 3: environment snapshot at entry
                    BarAtr20     = _cachedAtr20,
                    ItfTrend     = _cachedItfTrend,
                    HtfTrend     = _cachedHtfTrend,
                    TradeWithTrend = IsTradeWithTrend(isLong, _cachedItfTrend),
                };

                // Phase 2: zone context + grade
                PopulateZoneContext(entry, entryPrice);
                entry.EntryGrade = EntryGrader.Grade(entry);

                // Phase 3: HTF zone proximity
                entry.NearestHtfZoneDistance = ComputeNearestHtfZoneDistance(entryPrice);

                // Phase 3: start MAE/MFE tracking
                lock (_maeLock)
                {
                    _maeTracking[posId] = (entryPrice, isLong, 0.0, 0.0);
                }

                _pending[posId] = entry;

                if (AlertOnGradeDF &&
                    (entry.EntryGrade == "D" || entry.EntryGrade == "F" || entry.EntryGrade == "?"))
                {
                    string msg = entry.EntryGrade == "F"
                        ? "WTF Are You Doing?! Grade F — No IOF zone nearby. Are you chasing?!"
                        : $"WTF Are You Doing?! Grade {entry.EntryGrade} entry — {entry.NearestZoneType} zone, no confluence.";
                    Core.Instance.Loggers.Log(msg, LoggingLevel.Error);
                    FireAlert(msg);
                }

                Core.Instance.Loggers.Log(
                    $"IOF Journal: OPEN {entry.Direction} {contracts}x {this.Symbol?.Name} @ {entryPrice:F2} | Grade={entry.EntryGrade} Zone={entry.NearestZoneType} ITF={entry.ItfTrend} HTF={entry.HtfTrend}",
                    LoggingLevel.System);
            }
            catch (Exception ex)
            {
                try { Core.Instance.Loggers.Log(ex, "IOF_TradeJournal.OnPositionAdded"); } catch { }
            }
        }

        private void OnPositionRemoved(Position position)
        {
            if (!IsOurSymbol(position)) return;
            try
            {
                string posId = position.Id?.ToString() ?? "";
                if (!_pending.TryGetValue(posId, out var entry)) return;

                DateTime exitUtc = DateTime.UtcNow;
                double   pnl     = 0;
                try { pnl = position.GrossPnL?.Value ?? 0; } catch { }

                double tickSize   = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25;
                double pointValue = ResolvePointValue();

                // Back-calculate exit price from gross P&L
                double exitPrice;
                if (entry.Contracts > 0 && pointValue > 0)
                {
                    double mult = entry.Contracts * pointValue;
                    exitPrice = entry.Direction == "LONG"
                        ? entry.EntryPrice + pnl / mult
                        : entry.EntryPrice - pnl / mult;
                }
                else
                {
                    exitPrice = this.Symbol?.Last ?? entry.EntryPrice;
                }

                double commission = entry.Contracts * CommissionPerContractRT;
                double netPnl     = pnl - commission;

                // Phase 3: harvest MAE/MFE
                lock (_maeLock)
                {
                    if (_maeTracking.TryGetValue(posId, out var tracking))
                    {
                        entry.MaxAdverseExcursion   = tracking.mae;
                        entry.MaxFavorableExcursion = tracking.mfe;
                        _maeTracking.Remove(posId);
                    }
                }

                entry.ExitTime          = exitUtc;
                entry.HoldTimeSeconds   = (int)(exitUtc - entry.EntryTime).TotalSeconds;
                entry.HoldTimeFormatted = JournalEntry.FormatHoldTime(entry.HoldTimeSeconds);
                entry.ExitPrice         = exitPrice;
                entry.GrossPnL          = pnl;
                entry.Commission        = commission;
                entry.NetPnL            = netPnl;
                entry.ExitReason        = InferExitReason(pnl);
                entry.IsComplete        = true;

                // R-multiple: 1R = zone height × contracts × pointValue
                double zoneHeight = Math.Abs(entry.NearestZoneTop - entry.NearestZoneBottom);
                if (zoneHeight > 0 && entry.Contracts > 0 && pointValue > 0)
                    entry.RMultiple = pnl / (entry.Contracts * zoneHeight * pointValue);

                // Phase 3: TP hit inference from MFE vs zone height
                InferTpHits(entry);

                _pending.Remove(posId);
                _completed.Add(entry);

                EnsureWriter();
                _writer.AppendTrade(entry, EnableCsvLog, EnableJsonLog);

                Core.Instance.Loggers.Log(
                    $"IOF Journal: CLOSE {entry.Direction} {entry.Contracts}x {entry.Symbol} @ {exitPrice:F2} | Net=${netPnl:F2} R={entry.RMultiple:F1} Grade={entry.EntryGrade} MAE={entry.MaxAdverseExcursion:F1}pt MFE={entry.MaxFavorableExcursion:F1}pt",
                    LoggingLevel.System);
            }
            catch (Exception ex)
            {
                try { Core.Instance.Loggers.Log(ex, "IOF_TradeJournal.OnPositionRemoved"); } catch { }
            }
        }

        // ── Phase 3: tick MAE/MFE handler ────────────────────────────────────

        private void OnNewLast_Journal(Symbol symbol, Last last)
        {
            double price = last.Price;
            if (price <= 0) return;

            lock (_maeLock)
            {
                var keys = new List<string>(_maeTracking.Keys);
                foreach (var key in keys)
                {
                    var t     = _maeTracking[key];
                    double delta    = price - t.entryPrice;
                    double adverse  = t.isLong ? Math.Max(-delta, 0) : Math.Max(delta, 0);
                    double favorable = t.isLong ? Math.Max(delta, 0) : Math.Max(-delta, 0);

                    double newMae = Math.Max(t.mae, adverse);
                    double newMfe = Math.Max(t.mfe, favorable);

                    if (newMae != t.mae || newMfe != t.mfe)
                        _maeTracking[key] = (t.entryPrice, t.isLong, newMae, newMfe);
                }
            }
        }

        // ── Chart overlay ─────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (!ShowOverlayLabels) return;
            if (this.Symbol == null || this.CurrentChart == null) return;

            var gr  = args.Graphics;
            var win = this.CurrentChart.MainWindow;
            if (win == null) return;

            try
            {
                foreach (var e in _completed)   DrawTradeLabel(gr, win, e);
                foreach (var e in _pending.Values) DrawTradeLabel(gr, win, e);
            }
            catch { }
        }

        private void DrawTradeLabel(Graphics gr, dynamic win, JournalEntry e)
        {
            if (e.EntryPrice <= 0) return;

            int yEntry;
            try { yEntry = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(e.EntryPrice)); }
            catch { return; }

            var rect = (Rectangle)win.ClientRectangle;
            if (yEntry < rect.Top - 20 || yEntry > rect.Bottom + 20) return;

            Brush brush = GradeBrush(e.EntryGrade);
            string trendStr = string.IsNullOrEmpty(e.ItfTrend) ? "" : $" {e.ItfTrend[0]}";
            string entryLabel = $" {e.EntryGrade} {e.Direction} {e.Contracts}x{trendStr} ";
            gr.FillRectangle(_brushBg, rect.Right - 130, yEntry - 10, 128, 18);
            gr.DrawString(entryLabel, _labelFont, brush, rect.Right - 128, yEntry - 9);

            if (e.IsComplete && e.ExitPrice > 0)
            {
                int yExit;
                try { yExit = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(e.ExitPrice)); }
                catch { return; }

                if (yExit >= rect.Top - 20 && yExit <= rect.Bottom + 20)
                {
                    string rStr = e.RMultiple != 0 ? $"{e.RMultiple:F1}R" : e.ExitReason;
                    string exitLabel = $" ✕ {rStr} ${e.NetPnL:F0} ";
                    gr.FillRectangle(_brushBg, rect.Right - 130, yExit - 10, 128, 18);
                    gr.DrawString(exitLabel, _labelFont, _brushExit, rect.Right - 128, yExit - 9);
                }
            }
        }

        // ── Zone context (reflection) ─────────────────────────────────────────

        private void PopulateZoneContext(JournalEntry entry, double price)
        {
            try
            {
                string period = this.HistoricalData?.Aggregation?.ToString() ?? IofTimeframe.ToString();
                string iofKey = $"{this.Symbol?.Name}_{period}";
                double tolerance = ZoneProximityTicks * (this.Symbol?.TickSize ?? 0.25);

                Type regType = FindRegistryType("TradePhantoms.IOFZoneRegistry");
                if (regType == null) return;

                var getZones = regType.GetMethod("GetZones", BindingFlags.Public | BindingFlags.Static);
                if (getZones == null) return;

                var zones = getZones.Invoke(null, new object[] { iofKey }) as System.Collections.IEnumerable;
                if (zones == null) return;

                object bestZone = null;
                double bestDist = double.MaxValue;

                foreach (var z in zones)
                {
                    double top    = GetProp<double>(z, "Top");
                    double bottom = GetProp<double>(z, "Bottom");
                    double mid    = (top + bottom) / 2.0;
                    double dist   = Math.Abs(price - mid);
                    if (dist < bestDist) { bestDist = dist; bestZone = z; }
                }

                if (bestZone == null) return;

                double zTop    = GetProp<double>(bestZone, "Top");
                double zBottom = GetProp<double>(bestZone, "Bottom");
                object zType   = GetProp<object>(bestZone, "Type");
                double zScore  = GetProp<double>(bestZone, "Score");
                int    zTouch  = GetProp<int>(bestZone, "TouchCount");

                entry.NearestZoneType    = zType?.ToString() ?? "";
                entry.NearestZoneTop     = zTop;
                entry.NearestZoneBottom  = zBottom;
                entry.ZoneScore          = zScore;
                entry.ZoneTouchCountAtEntry = zTouch;
                entry.ZoneTimeframe      = $"{IofTimeframe}m";
                entry.EntryInsideZone    = price >= zBottom - tolerance && price <= zTop + tolerance;

                double tickSz  = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25;
                double nearEdge = price < zBottom ? zBottom - price
                                : price > zTop    ? price    - zTop
                                : 0;
                entry.EntryDistanceFromZoneEdgeTicks = (int)Math.Round(nearEdge / tickSz);

                entry.ZonePurityAtEntry = zTouch == 0 ? "FRESH"
                                        : zTouch <= 1  ? "TESTED"
                                        : "DEGRADED";

                TryPopulateZoneMetrics(entry, regType);
            }
            catch { }
        }

        private void TryPopulateZoneMetrics(JournalEntry entry, Type regType)
        {
            try
            {
                Type metricsType = regType.Assembly.GetType("TradePhantoms.ZoneMetricsRegistry");
                if (metricsType == null) return;

                Type exportType = regType.Assembly.GetType("TradePhantoms.ZoneMetricsExport");
                if (exportType == null) return;

                var tryGet = metricsType.GetMethod("TryGet",
                    new[] { typeof(string), typeof(double), typeof(double), exportType.MakeByRefType() });
                if (tryGet == null) return;

                string period = this.HistoricalData?.Aggregation?.ToString() ?? IofTimeframe.ToString();
                string regKey = $"{this.Symbol?.Name}_{period}";

                object[] parms = new object[] { regKey, entry.NearestZoneTop, entry.NearestZoneBottom, null };
                bool found = (bool)tryGet.Invoke(null, parms);
                if (!found || parms[3] == null) return;

                object m = parms[3];
                entry.DepartureMultiplier  = GetProp<double>(m, "DepartureMultiplier");
                entry.AbsorptionMultiplier = GetProp<double>(m, "AbsorptionMultiplier");
                entry.MtfcBonus            = GetProp<double>(m, "MtfcBonus");
                entry.HvnConfluence        = GetProp<bool>(m, "HvnConfluence");
            }
            catch { }
        }

        // ── Phase 3: environment helpers ──────────────────────────────────────

        private double ComputeAtr20()
        {
            try
            {
                if (this.Count < 21) return 0;
                double total = 0;
                for (int i = 0; i < 20; i++)
                {
                    double h = this.GetPrice(PriceType.High,  i);
                    double l = this.GetPrice(PriceType.Low,   i);
                    double p = this.GetPrice(PriceType.Close, i + 1);
                    double tr = Math.Max(h - l, Math.Max(Math.Abs(h - p), Math.Abs(l - p)));
                    total += tr;
                }
                return total / 20.0;
            }
            catch { return 0; }
        }

        private string ComputeEmaTrend(int period)
        {
            try
            {
                if (this.Count < period) return "";
                double k   = 2.0 / (period + 1);
                double ema = this.GetPrice(PriceType.Close, period - 1);
                for (int i = period - 2; i >= 0; i--)
                    ema = ema * (1 - k) + this.GetPrice(PriceType.Close, i) * k;

                double current  = this.GetPrice(PriceType.Close, 0);
                double flatBand = _cachedAtr20 > 0 ? _cachedAtr20 * 0.1
                                : (this.Symbol?.TickSize ?? 0.25) * 4;

                if (current > ema + flatBand) return "BULL";
                if (current < ema - flatBand) return "BEAR";
                return "FLAT";
            }
            catch { return ""; }
        }

        private int ComputeNearestHtfZoneDistance(double price)
        {
            try
            {
                string sym    = this.Symbol?.Name ?? "";
                double tickSz = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25;

                Type regType = FindRegistryType("TradePhantoms.IOFZoneRegistry");
                if (regType == null) return 0;

                var getZones = regType.GetMethod("GetZones", BindingFlags.Public | BindingFlags.Static);
                if (getZones == null) return 0;

                // Check common HTF aggregation strings
                var htfCandidates = new[]
                {
                    "15 Min", "15Min", "15", "30 Min", "30Min", "30",
                    "60 Min", "60Min", "60", "1 Hour", "1Hour", "120 Min"
                };

                double bestDist = double.MaxValue;
                foreach (var period in htfCandidates)
                {
                    string htfKey = $"{sym}_{period}";
                    var zones = getZones.Invoke(null, new object[] { htfKey }) as System.Collections.IEnumerable;
                    if (zones == null) continue;

                    foreach (var z in zones)
                    {
                        double top    = GetProp<double>(z, "Top");
                        double bottom = GetProp<double>(z, "Bottom");
                        double d      = price < bottom ? bottom - price
                                      : price > top    ? price    - top
                                      : 0;
                        if (d < bestDist) bestDist = d;
                    }
                }

                return bestDist == double.MaxValue ? 0 : (int)Math.Round(bestDist / tickSz);
            }
            catch { return 0; }
        }

        private static bool IsTradeWithTrend(bool isLong, string trend)
        {
            if (string.IsNullOrEmpty(trend) || trend == "FLAT") return false;
            return isLong ? trend == "BULL" : trend == "BEAR";
        }

        private static void InferTpHits(JournalEntry e)
        {
            double zoneHeight = Math.Abs(e.NearestZoneTop - e.NearestZoneBottom);
            if (zoneHeight <= 0 || e.MaxFavorableExcursion <= 0) return;

            e.HitTp1 = e.MaxFavorableExcursion >= zoneHeight;
            e.HitTp2 = e.MaxFavorableExcursion >= zoneHeight * 2.0;
            e.HitTp3 = e.MaxFavorableExcursion >= zoneHeight * 3.0;
            // EarlyExit: left money on the table — profitable but never reached TP1 extension
            e.EarlyExit = e.NetPnL > 0 && !e.HitTp1;
        }

        // ── General helpers ───────────────────────────────────────────────────

        private bool IsOurSymbol(Position position)
        {
            if (position == null || this.Symbol == null || position.Symbol == null) return false;
            return string.Equals(position.Symbol.Name, this.Symbol.Name,
                                 StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureWriter()
        {
            DateTime today = DateTime.UtcNow.Date;
            if (_writer != null && today == _writerDate) return;
            _writerDate = today;
            try { Directory.CreateDirectory(LogDirectory); } catch { }
            _writer = new JournalWriter(LogDirectory, today);
        }

        private void GenerateHtml()
        {
            try
            {
                _writer?.GenerateHtmlDashboard();
                Core.Instance.Loggers.Log(
                    $"IOF Journal: HTML dashboard written → {_writer?.HtmlPath()}",
                    LoggingLevel.System);
            }
            catch { }
        }

        private Type FindRegistryType(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "TradePhantoms_IOF_v2") continue;
                var t = asm.GetType(typeName);
                if (t != null) return t;
            }
            return null;
        }

        private static string BuildTradeId(DateTime utc, string symbol)
            => $"{utc:yyyyMMdd-HHmmss}-{symbol}";

        private static string InferExitReason(double pnl)
        {
            if (pnl > 0) return "TP";
            if (pnl < 0) return "SL";
            return "BE";
        }

        private Brush GradeBrush(string grade) => grade switch
        {
            "A" => _brushA, "B" => _brushB, "C" => _brushC,
            "D" => _brushD, "F" => _brushF,
            _   => _brushQ
        };

        private static T GetProp<T>(object obj, string name)
        {
            try
            {
                var prop = obj.GetType().GetProperty(name);
                if (prop != null)
                {
                    object v = prop.GetValue(obj);
                    if (v is T t) return t;
                    return (T)Convert.ChangeType(v, typeof(T));
                }
                var field = obj.GetType().GetField(name);
                if (field != null)
                {
                    object v = field.GetValue(obj);
                    if (v is T t) return t;
                    return (T)Convert.ChangeType(v, typeof(T));
                }
            }
            catch { }
            return default;
        }

        private static void FireAlert(string message)
        {
            try
            {
                object core = typeof(Core).GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (core == null) return;
                var alertsProp = core.GetType().GetProperty("Alerts");
                if (alertsProp == null) return;
                object alerts = alertsProp.GetValue(core);
                if (alerts == null) return;
                var addAlert = alerts.GetType().GetMethod("AddAlert",
                    new[] { typeof(string), typeof(string) });
                addAlert?.Invoke(alerts, new object[] { "IOF Journal", message });
            }
            catch { }
        }

        private double ResolvePointValue()
        {
            if (this.Symbol == null) return 2.0;
            double tickSize = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;

            string[] candidates = { "TickCost", "TickValue", "PointValue", "ContractMultiplier" };
            foreach (var propName in candidates)
            {
                try
                {
                    var prop = this.Symbol.GetType().GetProperty(propName);
                    if (prop == null) continue;
                    double num = Convert.ToDouble(prop.GetValue(this.Symbol));
                    if (num <= 0) continue;
                    return (propName == "TickCost" || propName == "TickValue")
                        ? num / tickSize
                        : num;
                }
                catch { }
            }

            string root = this.Symbol.Name.TrimEnd("0123456789HMUZ".ToCharArray()).ToUpperInvariant();
            return root switch
            {
                "MNQ" => 2.0,    "NQ"  => 20.0,  "MES" => 5.0,   "ES"  => 50.0,
                "M2K" => 5.0,    "RTY" => 50.0,  "MYM" => 0.5,   "YM"  => 5.0,
                "CL"  => 1000.0, "MCL" => 100.0, "GC"  => 100.0, "MGC" => 10.0,
                _     => 2.0
            };
        }

        private static TimeZoneInfo GetEasternTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); } catch { }
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }      catch { }
            return TimeZoneInfo.Utc;
        }

        private static DateTime ToEasternTime(DateTime utc)
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(utc, _et); }
            catch { return utc; }
        }
    }
}
