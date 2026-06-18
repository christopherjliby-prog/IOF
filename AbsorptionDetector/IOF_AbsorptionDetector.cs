// IOF_AbsorptionDetector.cs — Absorption / Exhaustion → Aggression-Flip detector
//
// Detects TWO distinct reversal signatures (per spec) and labels each:
//
//   ABSORPTION  — elevated volume + delta-close divergence (heavy delta, opposing close).
//                 Someone big is actively soaking the aggression. Volume stays UP.
//                 Example: sellers hit -1072/-2076/-803, then delta -81 BUT bar closes
//                 GREEN at 30,407 → sellers got absorbed.
//
//   EXHAUSTION  — volume DRIES UP into the extreme. Move running out of gas on its own.
//                 Delta weakens AND volume < ExhaustionVolDrop × prior average.
//
// Alert sequence (same for both signature types):
//   WATCH      → effort phase confirmed at the extreme, watching for the tell
//   EXHAUSTION → tell bar just closed (actionable — before the flip)
//   CONFIRMED  → flip bar printed (opposite-sign delta ≥ threshold)
//
// Validated acceptance test (MNQ 1m, June 16 2026 ~8:45-8:51 AM PT, 30,407 low):
//   Effort:     delta -1072, -2076, -803
//   Tell bar:   delta -81, close GREEN (= ABSORPTION, not exhaustion — vol stayed elevated)
//   Flip bar:   delta +2985
//
// Detect-only. No orders. Trader decides. Gate panel executes.
// Zone gate via IOFZoneRegistry cross-DLL reflection (graceful no-op if not loaded).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace IOF_AbsorptionDetector
{
    public enum AbsorptionSide { Demand, Supply }
    public enum AlertLevel    { Watch, Exhaustion, Confirmed }
    public enum SignatureType { Absorption, Exhaustion }   // the TWO distinct patterns

    public class IOF_AbsorptionDetector : Indicator, IVolumeAnalysisIndicator
    {
        // ── Detection parameters ─────────────────────────────────────────────────

        [InputParameter("Effort lookback (bars)", 10, 2, 50, 1, 0)]
        public int EffortLookback = 5;

        [InputParameter("Effort delta threshold (abs)", 20, 1, 20000, 50, 0)]
        public int EffortDeltaThreshold = 800;

        [InputParameter("Effort min heavy bars", 30, 1, 20, 1, 0)]
        public int EffortMinBars = 2;

        [InputParameter("New-extreme lookback (bars)", 35, 2, 50, 1, 0)]
        public int NewExtremeLookback = 5;

        // ── Absorption signature (PRIMARY — delta-close divergence) ──────────────

        [InputParameter("Divergence mode (primary absorption)", 40)]
        public bool DivergenceMode = true;

        [InputParameter("Exhaustion delta max (abs) — collapse form", 45, 1, 20000, 25, 0)]
        public int ExhaustionDeltaMax = 200;

        [InputParameter("Absorption: volume min (elevated)", 50, 1, 1000000, 100, 0)]
        public int ExhaustionVolMin = 1500;

        // ── Exhaustion signature (SECONDARY — volume drying up) ──────────────────

        [InputParameter("Exhaustion: vol drop ratio (x prior avg)", 55, 0.1, 1.0, 0.05, 2)]
        public double ExhaustionVolDrop = 0.6;

        // ── Flip confirmation ────────────────────────────────────────────────────

        [InputParameter("Flip delta threshold (abs)", 60, 1, 20000, 50, 0)]
        public int FlipDeltaThreshold = 1000;

        [InputParameter("Flip confirm window (bars)", 70, 1, 10, 1, 0)]
        public int FlipConfirmWindow = 3;

        // ── Zone gate ────────────────────────────────────────────────────────────

        [InputParameter("Zone proximity (ticks)", 80, 0, 200, 1, 0)]
        public int ZoneProximityTicks = 20;

        [InputParameter("Require zone context (gate all alerts)", 85)]
        public bool RequireZoneContext = false;

        // ── Alerts ────────────────────────────────────────────────────────────────

        [InputParameter("Enable Watch pre-alert", 90)]
        public bool EnableWatchAlert = true;

        [InputParameter("Sound on Exhaustion alert", 100)]
        public bool SoundOnExhaustion = true;

        [InputParameter("Sound on Confirmed alert", 110)]
        public bool SoundOnConfirmed = true;

        // ── Tuning / logging ─────────────────────────────────────────────────────

        [InputParameter("Log all candidates (tuning mode)", 120)]
        public bool LogAllCandidates = false;

        [InputParameter("Log file path", 130)]
        public string LogFilePath = "IOF_AbsorptionDetector_log.csv";

        // ── Display ──────────────────────────────────────────────────────────────

        [InputParameter("Watch color", 140)]
        public Color WatchColor = Color.FromArgb(255, 255, 215, 0);

        [InputParameter("Exhaustion color", 150)]
        public Color ExhaustionColor = Color.FromArgb(255, 255, 140, 0);

        [InputParameter("Confirmed long color", 160)]
        public Color ConfirmedLongColor = Color.FromArgb(255, 0, 200, 90);

        [InputParameter("Confirmed short color", 170)]
        public Color ConfirmedShortColor = Color.FromArgb(255, 220, 40, 40);

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
        }

        private readonly List<PendingConfirm> _pending = new();
        private readonly List<MarkerInfo>     _markers = new();
        private readonly object               _lock    = new();

        private string _zoneKey              = "";
        private bool   _volumeAnalysisLoaded = false;

        // ════════════════════════════════════════════════════════════════════════

        public IOF_AbsorptionDetector() : base()
        {
            Name           = "IOF Absorption Detector";
            ShortName      = "IOF-ABS";
            Description    = "Flags the absorption/exhaustion tell bar before the aggression flip. Detect-only.";
            SeparateWindow = false;
            AddLineSeries("IOF_ABS_hidden", Color.Transparent, 1, LineStyle.Solid);
        }

        public bool IsRequirePriceLevelsCalculation => false;

        public void VolumeAnalysisData_Loaded() => _volumeAnalysisLoaded = true;

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
                            "Time,Side,SignatureType,Stage,EffortBars,EffortDeltas,CurDelta,CurVol,AvgPriorVol,Price,DivergenceHit,NearZone,Fired\n");
                }
                catch { }
            }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (!_volumeAnalysisLoaded) return;
            if (this.HistoricalData == null) return;

            int need = Math.Max(EffortLookback, NewExtremeLookback) + FlipConfirmWindow + 3;
            if (this.HistoricalData.Count < need) return;

            // Fire only on bar close — delta is final only at close.
            // index 0 = forming bar, index 1 = last closed bar.
            if (args.Reason != UpdateReason.NewBar && args.Reason != UpdateReason.HistoricalBar)
                return;

            var closed = this.HistoricalData[1] as HistoryItemBar;
            if (closed == null) return;

            EvaluateBar(closed, AbsorptionSide.Demand);
            EvaluateBar(closed, AbsorptionSide.Supply);
            AdvanceConfirmations(closed);
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
            // ── Step 1: effort phase ────────────────────────────────────────────
            // Scan bars [2 .. EffortLookback+1] — bars BEFORE the current bar.
            int    heavyCount     = 0;
            var    effortDeltas   = new List<double>();
            double avgPriorVol    = 0;
            int    priorVolCount  = 0;

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

            // ── Step 2: at the extreme ──────────────────────────────────────────
            // Prior bars only [2 .. NewExtremeLookback+1]. Current bar must be
            // at (or within 1 tick of) that prior extreme.
            double tickSize  = 0.25;
            try { tickSize = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25; } catch { }

            double priorExtreme = side == AbsorptionSide.Demand ? double.MaxValue : double.MinValue;
            for (int i = 2; i <= NewExtremeLookback + 1; i++)
            {
                var b = this.HistoricalData[i] as HistoryItemBar;
                if (b == null) break;
                if (side == AbsorptionSide.Demand) priorExtreme = Math.Min(priorExtreme, b.Low);
                else                               priorExtreme = Math.Max(priorExtreme, b.High);
            }

            bool atExtreme;
            if (side == AbsorptionSide.Demand)
                atExtreme = cur.Low <= priorExtreme + tickSize;          // at or marginally below prior low
            else
                atExtreme = cur.High >= priorExtreme - tickSize;         // at or marginally above prior high

            // ── Step 3: reward failing ──────────────────────────────────────────
            var prev = this.HistoricalData[2] as HistoryItemBar;
            bool rewardFailing;
            if (side == AbsorptionSide.Demand)
                rewardFailing = prev == null || cur.Low >= prev.Low - tickSize; // not making new lows
            else
                rewardFailing = prev == null || cur.High <= prev.High + tickSize; // not making new highs

            // ── Step 4 + 5: signature type ──────────────────────────────────────
            double curDelta = Delta(cur);
            double curVol   = Vol(cur);

            // ABSORPTION (primary): elevated volume + delta-close divergence.
            //   Demand: heavy negative delta BUT close > open (green close = absorbed)
            //   Supply: heavy positive delta BUT close < open (red close = absorbed)
            // Also catches the simpler collapse form (abs delta ≤ ExhaustionDeltaMax)
            // when DivergenceMode is true, the divergence form takes priority.
            bool divergenceHit;
            if (side == AbsorptionSide.Demand)
                divergenceHit = DivergenceMode
                    && curDelta <= -EffortDeltaThreshold    // heavy seller aggression
                    && cur.Close > cur.Open;                // but bar closed UP — absorbed
            else
                divergenceHit = DivergenceMode
                    && curDelta >= EffortDeltaThreshold     // heavy buyer aggression
                    && cur.Close < cur.Open;                // but bar closed DOWN — absorbed

            bool deltaCollapse = Math.Abs(curDelta) <= ExhaustionDeltaMax
                && (side == AbsorptionSide.Demand ? curDelta <= 0 : curDelta >= 0);

            bool absorptionSignal = (divergenceHit || deltaCollapse) && curVol >= ExhaustionVolMin;

            // EXHAUSTION (secondary): volume drying up into the extreme.
            //   Volume < ExhaustionVolDrop × average of prior lookback bars.
            //   Delta weakening (not necessarily flipping, just below threshold).
            bool volDryingUp     = priorVolCount > 0 && curVol < ExhaustionVolDrop * avgPriorVol;
            bool deltaWeakening  = side == AbsorptionSide.Demand
                ? curDelta > -EffortDeltaThreshold           // below effort threshold = weakening
                : curDelta <  EffortDeltaThreshold;
            bool exhaustionSignal = volDryingUp && deltaWeakening;

            // Near zone?
            bool nearZone = CheckZoneProximity(side == AbsorptionSide.Demand ? cur.Low : cur.High);
            bool zoneOk   = !RequireZoneContext || nearZone;

            // ── Logging ─────────────────────────────────────────────────────────
            if (LogAllCandidates && effortPresent)
            {
                string effortStr = string.Join("|", effortDeltas.ConvertAll(d => d.ToString("F0")));
                TryLog(cur.TimeLeft, side,
                    absorptionSignal ? SignatureType.Absorption : SignatureType.Exhaustion,
                    (absorptionSignal || exhaustionSignal) ? "EXHAUSTION" : "candidate",
                    heavyCount, effortStr, curDelta, curVol, avgPriorVol,
                    side == AbsorptionSide.Demand ? cur.Low : cur.High,
                    divergenceHit, nearZone,
                    (absorptionSignal || exhaustionSignal) && atExtreme && rewardFailing && zoneOk);
            }

            // ── Watch pre-alert ──────────────────────────────────────────────────
            if (EnableWatchAlert && effortPresent && atExtreme && !absorptionSignal && !exhaustionSignal)
            {
                double watchPrice = side == AbsorptionSide.Demand ? cur.Low : cur.High;
                AddMarker(cur.TimeLeft, watchPrice, AlertLevel.Watch, side, SignatureType.Absorption,
                    $"WATCH: {side} effort {heavyCount}/{EffortLookback} bars, delta {curDelta:F0}");
            }

            // ── Fire EXHAUSTION alert for whichever signature fired ──────────────
            if (effortPresent && atExtreme && rewardFailing && zoneOk)
            {
                SignatureType sigType;
                if (absorptionSignal)       sigType = SignatureType.Absorption;
                else if (exhaustionSignal)  sigType = SignatureType.Exhaustion;
                else                        return;

                double alertPrice = side == AbsorptionSide.Demand ? cur.Low : cur.High;
                string effortStr  = FormatRecentDeltas(side);
                string zoneTag    = nearZone ? "" : "  [NO ZONE CONTEXT]";
                string sigLabel   = sigType == SignatureType.Absorption
                    ? (divergenceHit ? "ABSORPTION (divergence)" : "ABSORPTION (collapse)")
                    : "EXHAUSTION (vol drying)";
                string volInfo    = sigType == SignatureType.Absorption
                    ? $"vol {curVol:F0} elevated"
                    : $"vol {curVol:F0} ({curVol / avgPriorVol:P0} of avg)";

                string label = $"{sigLabel}: effort {effortStr}, this bar {curDelta:F0}, {volInfo}{zoneTag}";

                AddMarker(cur.TimeLeft, alertPrice, AlertLevel.Exhaustion, side, sigType, label);

                lock (_lock)
                    _pending.Add(new PendingConfirm { Side = side, Type = sigType, BarsSince = 0 });

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
                        string label = $"{p.Type.ToString().ToUpper()} CONFIRMED: flip delta {curDelta:F0} ({p.BarsSince} bar(s) after tell)";
                        AddMarker(cur.TimeLeft, cur.Close, AlertLevel.Confirmed, p.Side, p.Type, label);
                        if (SoundOnConfirmed) PlaySound(AlertLevel.Confirmed);
                        FirePlatformAlert("ABSORPTION_CONFIRMED", label);
                        _pending.RemoveAt(i);
                    }
                    else if (p.BarsSince >= FlipConfirmWindow)
                    {
                        _pending.RemoveAt(i); // window expired, no flip — drop silently
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
                bool heavy = side == AbsorptionSide.Demand ? d <= -EffortDeltaThreshold : d >= EffortDeltaThreshold;
                if (heavy) parts.Add(d.ToString("F0"));
            }
            return parts.Count > 0 ? string.Join("/", parts) : "n/a";
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
            int heavyCount, string effortDeltas, double curDelta, double curVol, double avgPriorVol,
            double price, bool divHit, bool nearZone, bool fired)
        {
            try
            {
                string line = string.Join(",",
                    time.ToString("yyyy-MM-dd HH:mm:ss"), side, sigType, stage,
                    heavyCount, effortDeltas, curDelta.ToString("F0"), curVol.ToString("F0"),
                    avgPriorVol.ToString("F0"), price.ToString("F4"), divHit, nearZone, fired);
                File.AppendAllText(LogFilePath, line + "\n");
            }
            catch { }
        }

        // ── Rendering ─────────────────────────────────────────────────────────────

        private void AddMarker(DateTime time, double price, AlertLevel level,
            AbsorptionSide side, SignatureType type, string label)
        {
            lock (_lock)
            {
                _markers.Add(new MarkerInfo
                {
                    Time = time, Price = price, Level = level,
                    Side = side, Type = type, Label = label
                });
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

            var gr   = args.Graphics;
            var win  = this.CurrentChart.MainWindow;
            var rect = (Rectangle)win.ClientRectangle;

            using var labelFont  = new Font("Arial", 11f, FontStyle.Bold);
            using var detailFont = new Font("Consolas", 7f, FontStyle.Regular);

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

                // Bubble color: green = demand (buy), red = supply (sell)
                // Watch = semi-transparent; Exhaustion/Confirmed = solid
                Color bubbleColor;
                if (m.Level == AlertLevel.Watch)
                    bubbleColor = isDemand
                        ? Color.FromArgb(120, 0, 200, 80)
                        : Color.FromArgb(120, 220, 40, 40);
                else
                    bubbleColor = isDemand
                        ? Color.FromArgb(220, 0, 210, 80)
                        : Color.FromArgb(220, 230, 30, 30);

                int radius = m.Level == AlertLevel.Exhaustion ? 12
                           : m.Level == AlertLevel.Confirmed  ? 10
                           : 6;

                // Position bubble below bar for demand, above for supply
                int gap     = radius + 6;
                int centerY = isDemand ? y + gap : y - gap;

                using var brush     = new SolidBrush(bubbleColor);
                using var rimPen    = new Pen(Color.White, 1.5f);
                using var textBrush = new SolidBrush(Color.White);

                // Draw filled bubble with white rim
                gr.FillEllipse(brush,    x - radius, centerY - radius, radius * 2, radius * 2);
                gr.DrawEllipse(rimPen,   x - radius, centerY - radius, radius * 2, radius * 2);

                // "BUY NOW" / "SELL NOW" label on Exhaustion and Confirmed bars
                if (m.Level == AlertLevel.Exhaustion || m.Level == AlertLevel.Confirmed)
                {
                    string callout = isDemand ? "BUY NOW" : "SELL NOW";
                    var    sz      = gr.MeasureString(callout, labelFont);
                    int    textX   = x - (int)(sz.Width / 2);
                    int    textY   = isDemand
                        ? centerY + radius + 3
                        : centerY - radius - (int)sz.Height - 3;

                    // Dark shadow for legibility
                    using var shadowBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                    gr.DrawString(callout, labelFont, shadowBrush, textX + 1, textY + 1);
                    gr.DrawString(callout, labelFont, textBrush,   textX,     textY);

                    // Small detail line below the callout (delta context)
                    if (m.Level == AlertLevel.Exhaustion)
                    {
                        var   detailSz = gr.MeasureString(m.Label, detailFont);
                        int   detailX  = x - (int)(detailSz.Width / 2);
                        int   detailY  = isDemand
                            ? textY + (int)sz.Height + 1
                            : textY - (int)detailSz.Height - 1;
                        using var detailBrush = new SolidBrush(Color.FromArgb(200, 220, 220, 220));
                        gr.DrawString(m.Label, detailFont, detailBrush, detailX, detailY);
                    }
                }
            }
        }
    }
}
