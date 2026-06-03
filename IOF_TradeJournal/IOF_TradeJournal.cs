// =============================================================================
// IOF_TradeJournal.cs — "WTF Are You Doing?!" Accountability Journal
// =============================================================================
// Platform : Quantower C# SDK (v1.143.x / TradingPlatform.BusinessLayer)
// Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF_TradeJournal\
//
// What this does:
//   Hooks into Quantower's live position feed and auto-journals every trade
//   you take with full IOF zone context, environment state, and an A-F entry
//   quality grade — then writes structured CSV + JSON logs in real time.
//   Grade D/F entries trigger an immediate "WTF Are You Doing?!" alert.
//
// Build phases:
//   Phase 1 (this build): Position feed, CSV + JSON logging, execution data
//   Phase 2: IOFZoneRegistry lookup, zone context, full A-F grading
//   Phase 3: Environment (HTF/ITF trend, ATR, MAE/MFE tracking)
//   Phase 4: Daily HTML dashboard auto-generation at EOD
//
// Dependencies:
//   - IOFZoneRegistry (in TradePhantoms_IOF_v2.dll) accessed via reflection
//   - No Rithmic API. No external services. Zero friction.
// =============================================================================
// PATCH NOTES
// -----------------------------------------------------------------------------
// 2026-06-03: Initial build (Phase 1).
//   - Position feed subscription (PositionAdded / PositionRemoved)
//   - JournalEntry creation at open, completion at close
//   - CSV + JSON real-time append
//   - Session classification (London / NY_Open / NY_Mid / NY_Close)
//   - IOFZoneRegistry lookup via reflection (nearest zone at entry)
//   - Entry grade computation via EntryGrader (Phase 1: inside/outside only)
//   - Grade D/F alert pop-up ("WTF Are You Doing?!")
//   - Chart overlay labels at entry/exit (grade + R at exit)
//   - HTML dashboard generated at 4:00 PM ET or on dispose
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

        // Pending (open) positions: positionId → in-progress JournalEntry
        private readonly Dictionary<string, JournalEntry> _pending = new();

        // Completed entries for overlay rendering
        private readonly List<JournalEntry> _completed = new();

        // Journal writer (one per trading day; re-created at midnight rollover)
        private JournalWriter _writer;
        private DateTime      _writerDate = DateTime.MinValue;

        // For EOD HTML generation at 4 PM ET
        private bool _eodDone;
        private static readonly TimeZoneInfo _et = GetEasternTz();

        // Fonts / brushes for chart overlay
        private Font  _labelFont;
        private Brush _brushA, _brushB, _brushC, _brushD, _brushF, _brushQ, _brushExit, _brushBg;

        // ── Quantower indicator lifecycle ─────────────────────────────────────

        protected override void OnInit()
        {
            Name        = "WTF Are You Doing?! — IOF Journal";
            Description = "Accountability partner — auto-journals every IOF trade.";

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

            try { Core.Instance.PositionAdded   -= OnPositionAdded;   } catch { }
            try { Core.Instance.PositionRemoved  -= OnPositionRemoved; } catch { }
            Core.Instance.PositionAdded   += OnPositionAdded;
            Core.Instance.PositionRemoved  += OnPositionRemoved;

            Core.Instance.Loggers.Log("IOF Trade Journal: initialized.", LoggingLevel.System);
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            // EOD HTML dashboard at 4:00 PM ET
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
            try { Core.Instance.PositionAdded   -= OnPositionAdded;   } catch { }
            try { Core.Instance.PositionRemoved  -= OnPositionRemoved; } catch { }
        }

        public override void Dispose()
        {
            try { Core.Instance.PositionAdded   -= OnPositionAdded;   } catch { }
            try { Core.Instance.PositionRemoved  -= OnPositionRemoved; } catch { }

            if (EnableHtmlDashboard && _writer != null)
                try { GenerateHtml(); } catch { }

            _labelFont?.Dispose();
            (_brushA  as IDisposable)?.Dispose();
            (_brushB  as IDisposable)?.Dispose();
            (_brushC  as IDisposable)?.Dispose();
            (_brushD  as IDisposable)?.Dispose();
            (_brushF  as IDisposable)?.Dispose();
            (_brushQ  as IDisposable)?.Dispose();
            (_brushExit as IDisposable)?.Dispose();
            (_brushBg as IDisposable)?.Dispose();

            base.Dispose();
        }

        // ── Position event handlers ───────────────────────────────────────────

        private void OnPositionAdded(Position position)
        {
            if (!IsOurSymbol(position)) return;
            try
            {
                string posId = position.Id?.ToString() ?? Guid.NewGuid().ToString();
                if (_pending.ContainsKey(posId)) return;  // already tracking

                bool   isLong      = position.Side == Side.Buy;
                double entryPrice  = position.OpenPrice;
                int    contracts   = (int)Math.Abs(position.Quantity);
                DateTime entryUtc  = DateTime.UtcNow;

                var entry = new JournalEntry
                {
                    PositionId   = posId,
                    TradeId      = BuildTradeId(entryUtc, this.Symbol.Name),
                    Date         = entryUtc.Date,
                    EntryTime    = entryUtc,
                    Session      = SessionClassifier.Classify(entryUtc),
                    TimeOfDayBucket = SessionClassifier.GetTimeBucket(entryUtc),
                    Symbol       = this.Symbol?.Name ?? "",
                    Direction    = isLong ? "LONG" : "SHORT",
                    Contracts    = contracts,
                    EntryPrice   = entryPrice,
                    IsComplete   = false
                };

                // Zone lookup (reflection — safe no-op if IOF v2 not loaded)
                PopulateZoneContext(entry, entryPrice);

                // Grade based on Phase 1 data
                entry.EntryGrade = EntryGrader.Grade(entry);

                _pending[posId] = entry;

                // Grade D/F alert
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
                    $"IOF Journal: OPEN {entry.Direction} {contracts}x {this.Symbol?.Name} @ {entryPrice:F2} | Grade={entry.EntryGrade} Zone={entry.NearestZoneType}",
                    LoggingLevel.System);

                // Force chart repaint for overlay
                // (chart repaints automatically on next render pass)
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

                double tickSize  = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25;
                double pointValue = ResolvePointValue();

                // Back-calculate exit price from gross P&L
                double exitPrice;
                if (entry.Contracts > 0 && pointValue > 0)
                {
                    double multiplier = entry.Contracts * pointValue;
                    exitPrice = entry.Direction == "LONG"
                        ? entry.EntryPrice + pnl / multiplier
                        : entry.EntryPrice - pnl / multiplier;
                }
                else
                {
                    exitPrice = this.Symbol?.Last ?? entry.EntryPrice;
                }

                double commission = entry.Contracts * CommissionPerContractRT;
                double netPnl     = pnl - commission;

                entry.ExitTime         = exitUtc;
                entry.HoldTimeSeconds  = (int)(exitUtc - entry.EntryTime).TotalSeconds;
                entry.HoldTimeFormatted = JournalEntry.FormatHoldTime(entry.HoldTimeSeconds);
                entry.ExitPrice        = exitPrice;
                entry.GrossPnL         = pnl;
                entry.Commission       = commission;
                entry.NetPnL           = netPnl;
                entry.ExitReason       = InferExitReason(pnl);
                entry.IsComplete       = true;

                _pending.Remove(posId);
                _completed.Add(entry);

                EnsureWriter();
                _writer.AppendTrade(entry, EnableCsvLog, EnableJsonLog);

                Core.Instance.Loggers.Log(
                    $"IOF Journal: CLOSE {entry.Direction} {entry.Contracts}x {entry.Symbol} @ {exitPrice:F2} | PnL=${netPnl:F2} R={entry.RMultiple:F1} Grade={entry.EntryGrade}",
                    LoggingLevel.System);

                // (chart repaints automatically on next render pass)
            }
            catch (Exception ex)
            {
                try { Core.Instance.Loggers.Log(ex, "IOF_TradeJournal.OnPositionRemoved"); } catch { }
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
                // Draw completed trade labels
                foreach (var e in _completed)
                    DrawTradeLabel(gr, win, e);

                // Draw pending (open) trade labels
                foreach (var e in _pending.Values)
                    DrawTradeLabel(gr, win, e);
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
            string entryLabel = $" {e.EntryGrade} {e.Direction} {e.Contracts}x ";
            gr.FillRectangle(_brushBg, rect.Right - 120, yEntry - 10, 118, 18);
            gr.DrawString(entryLabel, _labelFont, brush, rect.Right - 118, yEntry - 9);

            // Exit label if complete
            if (e.IsComplete && e.ExitPrice > 0)
            {
                int yExit;
                try { yExit = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(e.ExitPrice)); }
                catch { return; }

                if (yExit >= rect.Top - 20 && yExit <= rect.Bottom + 20)
                {
                    string rStr = e.RMultiple > 0 ? $"{e.RMultiple:F1}R" : e.ExitReason;
                    string exitLabel = $" ✕ {rStr} ${e.NetPnL:F0} ";
                    gr.FillRectangle(_brushBg, rect.Right - 120, yExit - 10, 118, 18);
                    gr.DrawString(exitLabel, _labelFont, _brushExit, rect.Right - 118, yExit - 9);
                }
            }
        }

        // ── Zone context (reflection) ─────────────────────────────────────────

        private void PopulateZoneContext(JournalEntry entry, double price)
        {
            try
            {
                // Key must match IOF v2's GetRegistryKey() → Aggregation.ToString().
                // IofTimeframe parameter is kept as a display label only.
                string period = this.HistoricalData?.Aggregation?.ToString() ?? IofTimeframe.ToString();
                string iofKey = $"{this.Symbol?.Name}_{period}";
                double tolerance = ZoneProximityTicks * (this.Symbol?.TickSize ?? 0.25);

                Type regType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TradePhantoms_IOF_v2") continue;
                    regType = asm.GetType("TradePhantoms.IOFZoneRegistry");
                    if (regType != null) break;
                }
                if (regType == null) return;

                var getZones = regType.GetMethod("GetZones", BindingFlags.Public | BindingFlags.Static);
                if (getZones == null) return;

                var zones = getZones.Invoke(null, new object[] { iofKey }) as System.Collections.IEnumerable;
                if (zones == null) return;

                // Find nearest zone to entry price
                object bestZone    = null;
                double bestDist    = double.MaxValue;

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

                // Inside zone check
                entry.EntryInsideZone = price >= zBottom - tolerance && price <= zTop + tolerance;

                // Distance from nearest edge (ticks)
                double tickSz = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25;
                double nearEdge = price < zBottom ? zBottom - price
                                : price > zTop    ? price    - zTop
                                : 0;
                entry.EntryDistanceFromZoneEdgeTicks = (int)Math.Round(nearEdge / tickSz);

                // Zone purity
                entry.ZonePurityAtEntry = zTouch == 0 ? "FRESH"
                                        : zTouch <= 1  ? "TESTED"
                                        : "DEGRADED";

                // Optional ZoneMetrics (DepartureMultiplier etc.) — populated by IOF v2
                // via a separate static accessor if available.
                TryPopulateZoneMetrics(entry, bestZone, regType);
            }
            catch { }
        }

        private void TryPopulateZoneMetrics(JournalEntry entry, object zone, Type regType)
        {
            try
            {
                Type metricsType = regType.Assembly.GetType("TradePhantoms.ZoneMetricsRegistry");
                if (metricsType == null) return;

                // Use the double-overload: TryGet(regKey, top, bottom, out ZoneMetricsExport)
                var tryGet = metricsType.GetMethod("TryGet",
                    new[] { typeof(string), typeof(double), typeof(double), metricsType.Assembly.GetType("TradePhantoms.ZoneMetricsExport").MakeByRefType() });

                if (tryGet == null) return;

                string period = this.HistoricalData?.Aggregation?.ToString() ?? IofTimeframe.ToString();
                string regKey = $"{this.Symbol?.Name}_{period}";

                object[] parms = new object[] { regKey, entry.NearestZoneTop, entry.NearestZoneBottom, null };
                bool found = (bool)tryGet.Invoke(null, parms);
                if (!found) return;

                object m = parms[3];
                if (m == null) return;

                entry.DepartureMultiplier  = GetProp<double>(m, "DepartureMultiplier");
                entry.AbsorptionMultiplier = GetProp<double>(m, "AbsorptionMultiplier");
                entry.MtfcBonus            = GetProp<double>(m, "MtfcBonus");
                entry.HvnConfluence        = GetProp<bool>(m, "HvnConfluence");
            }
            catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private bool IsOurSymbol(Position position)
        {
            if (position == null || this.Symbol == null) return false;
            if (position.Symbol == null) return false;
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

        private static string BuildTradeId(DateTime utc, string symbol)
            => $"{utc:yyyyMMdd-HHmmss}-{symbol}";

        private static string InferExitReason(double pnl)
        {
            if (pnl > 0)  return "TP";
            if (pnl < 0)  return "SL";
            return "BE";
        }

        private Brush GradeBrush(string grade) => grade switch
        {
            "A" => _brushA,
            "B" => _brushB,
            "C" => _brushC,
            "D" => _brushD,
            "F" => _brushF,
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
            // Reflection-based Alerts panel (mirrors AlertsHelper.cs pattern)
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

            // Try reflection for TickCost / TickValue ($/tick) → convert to $/point
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

            // Hardcoded fallback table for common futures
            string root = this.Symbol.Name.TrimEnd("0123456789HMUZ".ToCharArray()).ToUpperInvariant();
            return root switch
            {
                "MNQ" => 2.0,    // $2/pt
                "NQ"  => 20.0,   // $20/pt
                "MES" => 5.0,    // $5/pt
                "ES"  => 50.0,   // $50/pt
                "M2K" => 5.0,    // $5/pt
                "RTY" => 50.0,   // $50/pt
                "MYM" => 0.5,    // $0.50/pt
                "YM"  => 5.0,    // $5/pt
                "CL"  => 1000.0, // $1000/pt
                "MCL" => 100.0,  // $100/pt
                "GC"  => 100.0,  // $100/pt
                "MGC" => 10.0,   // $10/pt
                _     => 2.0     // default MNQ
            };
        }

        private static TimeZoneInfo GetEasternTz()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { }
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch { }
            return TimeZoneInfo.Utc;
        }

        private static DateTime ToEasternTime(DateTime utc)
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(utc, _et); }
            catch { return utc; }
        }
    }
}
