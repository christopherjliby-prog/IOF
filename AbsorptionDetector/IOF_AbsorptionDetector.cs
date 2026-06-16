// IOF_AbsorptionDetector.cs — Absorption → Exhaustion → Aggression-Flip detector
//
// Detects the IOF absorption signature in real time from bar-level delta and
// fires an alert at the EXHAUSTION bar — the actionable moment, before the
// flip prints. Confirms on the following flip bar.
//
// This indicator does NOT place orders. It flags. The trader decides.
// Gate panel (separate indicator) owns execution.
//
// Validated against MNQ, June 16 2026, ~8:45-8:51 AM PT, 30,407 low:
//   effort   : delta -1072, -2076, -803 (heavy one-sided selling, no new lows)
//   exhaustion: delta -81  (effort collapses to near-zero on elevated volume)
//   flip     : delta +2985 (opposite side takes the close)
//
// Standalone project — does not modify TradePhantoms_IOF_v2.cs. Reads the IOF
// zone registry (if present on the chart) via cross-DLL reflection, same
// pattern as IOF_VolumeSpike — graceful no-op if the zone indicator isn't
// loaded.
//
// Confirmed SDK: TradingPlatform.BusinessLayer (see ../refs).
// Delta/volume data path confirmed against TradePhantoms_IOF_v2.cs's proven
// usage: bar.VolumeAnalysisData.Total.Delta, bar.Volume.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace IOF_AbsorptionDetector
{
    public enum AbsorptionSide { Demand, Supply }

    public enum AlertLevel { Watch, Exhaustion, Confirmed }

    public class IOF_AbsorptionDetector : Indicator, IVolumeAnalysisIndicator
    {
        // ── Detection parameters (starting values — tune via LogAllCandidates) ──

        [InputParameter("Effort lookback (bars)", 10, 2, 50, 1, 0)]
        public int EffortLookback = 5;

        [InputParameter("Effort delta threshold (abs)", 20, 1, 20000, 50, 0)]
        public int EffortDeltaThreshold = 800;

        [InputParameter("Effort min heavy bars", 30, 1, 20, 1, 0)]
        public int EffortMinBars = 2;

        [InputParameter("Exhaustion delta max (abs)", 40, 1, 20000, 25, 0)]
        public int ExhaustionDeltaMax = 200;

        [InputParameter("Exhaustion volume min", 50, 1, 1000000, 100, 0)]
        public int ExhaustionVolMin = 1500;

        [InputParameter("Flip delta threshold (abs)", 60, 1, 20000, 50, 0)]
        public int FlipDeltaThreshold = 1000;

        [InputParameter("Flip confirm window (bars)", 70, 1, 10, 1, 0)]
        public int FlipConfirmWindow = 3;

        [InputParameter("New-extreme lookback (bars)", 80, 2, 50, 1, 0)]
        public int NewExtremeLookback = 5;

        [InputParameter("Zone proximity (ticks)", 90, 0, 200, 1, 0)]
        public int ZoneProximityTicks = 20;

        [InputParameter("Require zone context", 95)]
        public bool RequireZoneContext = false;

        // ── Alerts / tuning ──────────────────────────────────────────────────────

        [InputParameter("Enable Watch pre-alert", 100)]
        public bool EnableWatchAlert = true;

        [InputParameter("Sound on Exhaustion", 110)]
        public bool SoundOnExhaustion = true;

        [InputParameter("Sound on Confirmed", 120)]
        public bool SoundOnConfirmed = true;

        [InputParameter("Log all candidates (tuning mode)", 130)]
        public bool LogAllCandidates = false;

        [InputParameter("Log file path", 140)]
        public string LogFilePath = "IOF_AbsorptionDetector_log.csv";

        // ── Display ──────────────────────────────────────────────────────────────

        [InputParameter("Watch color", 150)]
        public Color WatchColor = Color.FromArgb(255, 255, 215, 0);

        [InputParameter("Exhaustion color", 160)]
        public Color ExhaustionColor = Color.FromArgb(255, 255, 140, 0);

        [InputParameter("Confirmed long color", 170)]
        public Color ConfirmedLongColor = Color.FromArgb(255, 0, 200, 90);

        [InputParameter("Confirmed short color", 180)]
        public Color ConfirmedShortColor = Color.FromArgb(255, 220, 40, 40);

        // ── Internal state ───────────────────────────────────────────────────────

        private class PendingSignal
        {
            public AbsorptionSide Side;
            public int            BarsSinceExhaustion;
            public bool           Confirmed;
        }

        private readonly List<PendingSignal> _pending = new List<PendingSignal>();
        private readonly List<MarkerInfo>    _markers = new List<MarkerInfo>();
        private readonly object              _lock = new object();

        private struct MarkerInfo
        {
            public DateTime    Time;
            public double      Price;
            public AlertLevel  Level;
            public AbsorptionSide Side;
            public string      Label;
        }

        private string _zoneKey = "";
        private bool   _volumeAnalysisLoaded = false;

        // ════════════════════════════════════════════════════════════════════════

        public IOF_AbsorptionDetector() : base()
        {
            Name        = "IOF Absorption Detector";
            ShortName   = "IOF-ABS";
            Description = "Flags the absorption exhaustion bar before the aggression flip. Detect-only — no orders.";
            SeparateWindow = false;
            AddLineSeries("IOF_ABS_marker", Color.Transparent, 1, LineStyle.Solid);
        }

        public bool IsRequirePriceLevelsCalculation => false;

        public void VolumeAnalysisData_Loaded()
        {
            _volumeAnalysisLoaded = true;
        }

        protected override void OnInit()
        {
            lock (_lock) { _pending.Clear(); _markers.Clear(); }
            _volumeAnalysisLoaded = false;

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
                            "Time,Side,Stage,EffortBars,EffortDeltaSum,CurrentDelta,CurrentVolume,Price,NearZone,Fired\n");
                }
                catch { }
            }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (this.HistoricalData == null || this.HistoricalData.Count < EffortLookback + FlipConfirmWindow + 2)
                return;

            // Only evaluate on a newly CLOSED bar — index 1 is the last fully
            // closed bar relative to the forming bar at index 0. Firing here
            // means the exhaustion bar has already closed (one-bar lag, by design).
            if (args.Reason != UpdateReason.NewBar && args.Reason != UpdateReason.HistoricalBar)
                return;

            // Delta is only meaningful once cluster/volume-analysis data has
            // loaded — before that, Total.Delta reads as 0 and would look like
            // a fake exhaustion bar. Wait for the real data.
            if (!_volumeAnalysisLoaded)
                return;

            var closedBar = this.HistoricalData[1] as HistoryItemBar;
            if (closedBar == null) return;

            EvaluateBar(closedBar, AbsorptionSide.Demand);
            EvaluateBar(closedBar, AbsorptionSide.Supply);
            AdvanceConfirmations(closedBar);
        }

        // ── Core detection ───────────────────────────────────────────────────────

        private double GetDelta(HistoryItemBar bar)
        {
            try { return bar?.VolumeAnalysisData?.Total?.Delta ?? 0.0; }
            catch { return 0.0; }
        }

        private double GetVolume(HistoryItemBar bar)
        {
            try { return bar?.Volume ?? 0.0; }
            catch { return 0.0; }
        }

        private void EvaluateBar(HistoryItemBar currentBar, AbsorptionSide side)
        {
            // index 1 = currentBar (just closed). Scan bars [2 .. EffortLookback+1]
            // for the effort phase that precedes it.
            int heavyCount = 0;
            double effortDeltaSum = 0;
            for (int i = 2; i <= EffortLookback + 1; i++)
            {
                var b = this.HistoricalData[i] as HistoryItemBar;
                if (b == null) break;
                double d = GetDelta(b);
                effortDeltaSum += d;
                bool heavy = side == AbsorptionSide.Demand
                    ? d <= -EffortDeltaThreshold
                    : d >= EffortDeltaThreshold;
                if (heavy) heavyCount++;
            }
            bool effortPresent = heavyCount >= EffortMinBars;

            // New-extreme check over NewExtremeLookback bars (excluding current).
            double extreme = side == AbsorptionSide.Demand ? double.MaxValue : double.MinValue;
            for (int i = 1; i <= NewExtremeLookback; i++)
            {
                var b = this.HistoricalData[i] as HistoryItemBar;
                if (b == null) break;
                if (side == AbsorptionSide.Demand) extreme = Math.Min(extreme, b.Low);
                else extreme = Math.Max(extreme, b.High);
            }

            var prevBar = this.HistoricalData[2] as HistoryItemBar;
            bool atExtreme = side == AbsorptionSide.Demand
                ? currentBar.Low <= extreme + 1e-9
                : currentBar.High >= extreme - 1e-9;

            bool rewardFailing = prevBar != null && (side == AbsorptionSide.Demand
                ? !(currentBar.Low < prevBar.Low) || currentBar.Close > currentBar.Low
                : !(currentBar.High > prevBar.High) || currentBar.Close < currentBar.High);

            double curDelta = GetDelta(currentBar);
            double curVolume = GetVolume(currentBar);

            bool exhaustionDelta = Math.Abs(curDelta) <= ExhaustionDeltaMax
                && (side == AbsorptionSide.Demand ? curDelta <= 0 : curDelta >= 0);
            bool volumeElevated = curVolume >= ExhaustionVolMin;

            bool nearZone = CheckZoneProximity(side == AbsorptionSide.Demand ? currentBar.Low : currentBar.High);
            bool zoneOk = !RequireZoneContext || nearZone;

            bool fired = effortPresent && atExtreme && rewardFailing && exhaustionDelta && volumeElevated && zoneOk;

            if (LogAllCandidates && (effortPresent || fired))
            {
                TryLog(currentBar.TimeLeft, side, fired ? "EXHAUSTION" : "candidate",
                    heavyCount, effortDeltaSum, curDelta, curVolume,
                    side == AbsorptionSide.Demand ? currentBar.Low : currentBar.High,
                    nearZone, fired);
            }

            if (effortPresent && atExtreme && !fired && EnableWatchAlert)
            {
                // Watch: effort + at the extreme, but exhaustion hasn't printed yet.
                AddMarker(currentBar.TimeLeft,
                    side == AbsorptionSide.Demand ? currentBar.Low : currentBar.High,
                    AlertLevel.Watch, side,
                    $"WATCH: {side} effort {heavyCount}/{EffortLookback}, delta {curDelta:F0}");
            }

            if (fired)
            {
                lock (_lock)
                {
                    _pending.Add(new PendingSignal
                    {
                        Side = side,
                        BarsSinceExhaustion = 0,
                        Confirmed = false
                    });
                }

                string label = $"EXHAUSTION: effort {FormatRecentDeltas(side)}, this bar {curDelta:F0}, vol {curVolume:F0}" +
                                (RequireZoneContext ? "" : (nearZone ? "" : "  [NO ZONE CONTEXT]"));
                AddMarker(currentBar.TimeLeft,
                    side == AbsorptionSide.Demand ? currentBar.Low : currentBar.High,
                    AlertLevel.Exhaustion, side, label);

                if (SoundOnExhaustion) PlaySound(AlertLevel.Exhaustion);
                FirePlatformAlert("ABSORPTION_EXHAUSTION", label);
            }
        }

        private string FormatRecentDeltas(AbsorptionSide side)
        {
            var parts = new List<string>();
            for (int i = EffortLookback + 1; i >= 2; i--)
            {
                var b = this.HistoricalData[i] as HistoryItemBar;
                if (b == null) continue;
                double d = GetDelta(b);
                bool heavy = side == AbsorptionSide.Demand ? d <= -EffortDeltaThreshold : d >= EffortDeltaThreshold;
                if (heavy) parts.Add(d.ToString("F0"));
            }
            return parts.Count > 0 ? string.Join("/", parts) : "n/a";
        }

        private void AdvanceConfirmations(HistoryItemBar currentBar)
        {
            double curDelta = GetDelta(currentBar);
            lock (_lock)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    var p = _pending[i];
                    if (p.Confirmed) { _pending.RemoveAt(i); continue; }

                    p.BarsSinceExhaustion++;

                    bool flip = p.Side == AbsorptionSide.Demand
                        ? curDelta >= FlipDeltaThreshold
                        : curDelta <= -FlipDeltaThreshold;

                    if (flip)
                    {
                        p.Confirmed = true;
                        string label = $"CONFIRMED: flip delta {curDelta:F0} ({p.BarsSinceExhaustion} bar(s) after exhaustion)";
                        AddMarker(currentBar.TimeLeft, currentBar.Close, AlertLevel.Confirmed, p.Side, label);
                        if (SoundOnConfirmed) PlaySound(AlertLevel.Confirmed);
                        FirePlatformAlert("ABSORPTION_CONFIRMED", label);
                        _pending.RemoveAt(i);
                    }
                    else if (p.BarsSinceExhaustion >= FlipConfirmWindow)
                    {
                        // Window expired without a flip — drop silently, no confirmation.
                        _pending.RemoveAt(i);
                    }
                }
            }
        }

        // ── Zone proximity (cross-DLL reflection into IOFZoneRegistry) ───────────

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
                    var type = asm.GetType("TradePhantoms.IOFZoneRegistry");
                    if (type == null) continue;
                    var method = type.GetMethod("IsNearZone", BindingFlags.Public | BindingFlags.Static);
                    if (method == null) continue;
                    return (bool)method.Invoke(null, new object[] { _zoneKey, price, tolerance });
                }
            }
            catch { }
            return false; // zone indicator not loaded on this chart — caller decides what to do
        }

        // ── Alerts ────────────────────────────────────────────────────────────────

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
                var alerts = alertsProp?.GetValue(core);
                if (alerts == null) return;
                var addAlert = alerts.GetType().GetMethod("AddAlert", new[] { typeof(string), typeof(string) });
                addAlert?.Invoke(alerts, new object[] { eventType, message });
            }
            catch { }
        }

        private void TryLog(DateTime time, AbsorptionSide side, string stage, int heavyCount,
            double effortDeltaSum, double curDelta, double curVolume, double price, bool nearZone, bool fired)
        {
            try
            {
                string line = string.Join(",",
                    time.ToString("yyyy-MM-dd HH:mm:ss"), side, stage, heavyCount,
                    effortDeltaSum.ToString("F0"), curDelta.ToString("F0"), curVolume.ToString("F0"),
                    price.ToString("F2"), nearZone, fired);
                File.AppendAllText(LogFilePath, line + "\n");
            }
            catch { }
        }

        // ── Rendering ─────────────────────────────────────────────────────────────

        private void AddMarker(DateTime time, double price, AlertLevel level, AbsorptionSide side, string label)
        {
            lock (_lock)
            {
                _markers.Add(new MarkerInfo { Time = time, Price = price, Level = level, Side = side, Label = label });
                if (_markers.Count > 500) _markers.RemoveAt(0);
            }
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);
            if (this.CurrentChart == null) return;

            List<MarkerInfo> snap;
            lock (_lock) { snap = new List<MarkerInfo>(_markers); }
            if (snap.Count == 0) return;

            var gr = args.Graphics;
            var win = this.CurrentChart.MainWindow;
            var rect = (Rectangle)win.ClientRectangle;

            using var font = new Font("Consolas", 8f, FontStyle.Bold);

            foreach (var m in snap)
            {
                int x, y;
                try
                {
                    x = (int)Math.Round((double)win.CoordinatesConverter.GetChartX(m.Time));
                    y = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(m.Price));
                }
                catch { continue; }

                if (x < rect.Left - 50 || x > rect.Right + 50) continue;

                Color c = m.Level switch
                {
                    AlertLevel.Watch => WatchColor,
                    AlertLevel.Exhaustion => ExhaustionColor,
                    AlertLevel.Confirmed => m.Side == AbsorptionSide.Demand ? ConfirmedLongColor : ConfirmedShortColor,
                    _ => Color.Gray
                };

                int markerSize = m.Level == AlertLevel.Exhaustion ? 7 : 5;
                using var brush = new SolidBrush(c);
                int dirY = m.Side == AbsorptionSide.Demand ? 12 : -12;
                gr.FillEllipse(brush, x - markerSize / 2, y + dirY - markerSize / 2, markerSize, markerSize);

                if (m.Level != AlertLevel.Watch)
                {
                    var textY = y + dirY + (m.Side == AbsorptionSide.Demand ? 6 : -18);
                    gr.DrawString(m.Label, font, brush, x + 6, textY);
                }
            }
        }
    }
}
