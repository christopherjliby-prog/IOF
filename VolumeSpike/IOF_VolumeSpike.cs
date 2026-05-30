// IOF_VolumeSpike.cs — High Volume Node detector for Quantower
//
// Finds price levels where volume is abnormally high vs surrounding levels.
// Classifies each node: PEAK / BUILDING / BACKSIDE / ISOLATED.
// Flags ★IOF when a node overlaps a live TradePhantoms_IOF_v2 zone (cross-DLL
// via reflection — graceful no-op if IOF_v2 isn't loaded on the same chart).
//
// DATA SOURCE — tries in order, stops at first that returns > 10 levels:
//   1. Quantower native HistoryAggregationVolumeProfile (most accurate)
//   2. Bar-close volume bucketing over LookbackBars (always works)
//
// Install: drop IOF_VolumeSpike/ subfolder into Quantower Scripts\Indicators.
//          Does NOT need to share a folder with TradePhantoms_IOF_v2.
//
// Confirmed SDK: TradingPlatform.BusinessLayer v1.145.17

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    public class IOF_VolumeSpike : Indicator
    {
        // ── Detection ──────────────────────────────────────────────────────────
        [InputParameter("Spike threshold % above avg", 0, 101, 2000, 10, 0)]
        public int SpikeThresholdPct = 150;

        [InputParameter("Surrounding levels (N each side)", 1, 2, 30, 1, 0)]
        public int SurroundingN = 5;

        // ── Native VP settings ──────────────────────────────────────────────────
        [InputParameter("Profile days (native VP)", 2, 1, 10, 1, 0)]
        public int ProfileDays = 1;

        // ── Bar-based fallback settings ─────────────────────────────────────────
        [InputParameter("Fallback: lookback bars", 3, 50, 5000, 50, 0)]
        public int LookbackBars = 500;

        [InputParameter("Fallback: bucket size (ticks)", 4, 1, 20, 1, 0)]
        public int BucketTicks = 4;

        // ── IOF confluence ──────────────────────────────────────────────────────
        [InputParameter("IOF zone proximity (ticks)", 5, 1, 100, 1, 0)]
        public int IOFZoneProximityTicks = 8;

        // ── Display ─────────────────────────────────────────────────────────────
        [InputParameter("Show labels", 6)]
        public bool ShowLabels = true;

        [InputParameter("Line width", 7, 1, 5, 1, 0)]
        public int LineWidth = 2;

        // ── Colors ──────────────────────────────────────────────────────────────
        [InputParameter("HVN color", 8)]
        public Color HvnColor = Color.FromArgb(220, 0, 200, 255);

        [InputParameter("IOF confluence color", 9)]
        public Color ConfluenceColor = Color.FromArgb(255, 255, 215, 0);

        [InputParameter("Backside color", 10)]
        public Color BacksideColor = Color.FromArgb(220, 255, 100, 60);

        // ── Internal ────────────────────────────────────────────────────────────
        private List<HvnLevel>  _levels    = new List<HvnLevel>();
        private readonly object _lock      = new object();
        private string          _iofKey    = "";
        private bool            _nativeFailed = false;   // stop retrying after first failure

        // ── Types ───────────────────────────────────────────────────────────────
        private enum NodeContext { Peak, Building, Backside, Isolated }

        private class HvnLevel
        {
            public double      Price;
            public double      Volume;
            public double      PctAboveAvg;
            public NodeContext Context;
            public bool        IsIOFConfluence;
        }

        public IOF_VolumeSpike()
        {
            Name           = "IOF Volume Spike";
            ShortName      = "IOF-VS";
            Description    = "Marks price levels with abnormally high volume. ★IOF = overlaps live IOF zone.";
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            lock (_lock) _levels.Clear();
            _nativeFailed = false;

            try
            {
                string sym    = this.Symbol?.Name ?? "UNK";
                string period = this.HistoricalData?.Aggregation?.ToString() ?? "UNK";
                _iofKey = $"{sym}_{period}";
            }
            catch { _iofKey = ""; }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason == UpdateReason.NewBar || args.Reason == UpdateReason.HistoricalBar)
                RebuildLevels();
        }

        // ── Core rebuild ────────────────────────────────────────────────────────

        private void RebuildLevels()
        {
            if (this.Symbol == null) return;

            // Try native VP first; fall back to bar-based
            List<(double Price, double Volume)> profile = null;

            if (!_nativeFailed)
            {
                profile = FetchNativeProfile();
                if (profile == null || profile.Count < SurroundingN * 2 + 1)
                {
                    _nativeFailed = true;
                    profile = null;
                }
            }

            if (profile == null)
                profile = FetchBarBasedProfile();

            if (profile == null || profile.Count < SurroundingN * 2 + 1) return;

            profile.Sort((a, b) => a.Price.CompareTo(b.Price));

            double tick      = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double tolerance = IOFZoneProximityTicks * tick;

            var newLevels = new List<HvnLevel>();

            for (int i = SurroundingN; i < profile.Count - SurroundingN; i++)
            {
                double centerVol = profile[i].Volume;
                double sumVol    = 0;
                for (int j = i - SurroundingN; j <= i + SurroundingN; j++)
                    if (j != i) sumVol += profile[j].Volume;

                double avgVol = sumVol / (SurroundingN * 2);
                if (avgVol <= 0) continue;

                double pctAbove = centerVol / avgVol * 100.0;
                if (pctAbove < SpikeThresholdPct) continue;

                bool isIOF = CheckIOFConfluence(profile[i].Price, tolerance);

                newLevels.Add(new HvnLevel
                {
                    Price        = profile[i].Price,
                    Volume       = centerVol,
                    PctAboveAvg  = pctAbove,
                    Context      = ClassifyContext(profile, i),
                    IsIOFConfluence = isIOF
                });
            }

            lock (_lock)
            {
                _levels.Clear();
                _levels.AddRange(newLevels);
            }
        }

        // ── Volume profile sources ───────────────────────────────────────────────

        private List<(double Price, double Volume)> FetchNativeProfile()
        {
            try
            {
                var agg      = new HistoryAggregationVolumeProfile(Period.DAY1);
                var fromTime = DateTime.UtcNow.Date.AddDays(-ProfileDays);
                var hd       = this.Symbol.GetHistory(agg, fromTime, DateTime.UtcNow);
                if (hd == null || hd.Count == 0) return null;

                var result = new List<(double Price, double Volume)>();

                // Accumulate across requested days
                for (int i = 0; i < hd.Count; i++)
                {
                    var item = hd[i, SeekOriginHistory.Begin] as HistoryItemVolumeProfile;
                    if (item?.PriceLevels == null) continue;

                    foreach (var kvp in item.PriceLevels)
                    {
                        if (kvp.Value == null || kvp.Value.Volume <= 0) continue;
                        result.Add((kvp.Key, kvp.Value.Volume));
                    }
                }

                return result.Count > 10 ? result : null;
            }
            catch { return null; }
        }

        private List<(double Price, double Volume)> FetchBarBasedProfile()
        {
            try
            {
                if (this.HistoricalData == null) return null;

                double tick       = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
                double bucketSize = BucketTicks * tick;
                if (bucketSize <= 0) return null;

                var buckets = new Dictionary<int, double>();
                int total   = this.HistoricalData.Count;
                int start   = Math.Max(0, total - LookbackBars);

                for (int i = start; i < total; i++)
                {
                    var bar = this.HistoricalData[i, SeekOriginHistory.Begin] as HistoryItemBar;
                    if (bar == null || bar.Volume <= 0) continue;

                    // Distribute volume equally across O/H/L/C price buckets
                    double volPer = bar.Volume / 4.0;
                    foreach (double px in new[] { bar.Open, bar.High, bar.Low, bar.Close })
                    {
                        int bucket = (int)Math.Round(px / bucketSize);
                        if (!buckets.ContainsKey(bucket)) buckets[bucket] = 0;
                        buckets[bucket] += volPer;
                    }
                }

                return buckets.Count > 10
                    ? buckets.Select(kv => (kv.Key * bucketSize, kv.Value)).ToList()
                    : null;
            }
            catch { return null; }
        }

        // ── Context classification ───────────────────────────────────────────────

        private static NodeContext ClassifyContext(List<(double Price, double Volume)> profile, int idx)
        {
            int n = Math.Min(3, idx);

            bool leftRising   = true;
            bool rightFalling = true;

            for (int j = idx - n; j < idx - 1; j++)
                if (profile[j].Volume > profile[j + 1].Volume) { leftRising = false; break; }

            for (int j = idx + 1; j < idx + n && j + 1 < profile.Count; j++)
                if (profile[j].Volume < profile[j + 1].Volume) { rightFalling = false; break; }

            if  (leftRising  && rightFalling)  return NodeContext.Peak;
            if  (leftRising  && !rightFalling) return NodeContext.Building;
            if  (!leftRising && rightFalling)  return NodeContext.Backside;
            return NodeContext.Isolated;
        }

        // ── IOF confluence via cross-DLL reflection ──────────────────────────────

        private bool CheckIOFConfluence(double price, double tolerance)
        {
            if (string.IsNullOrEmpty(_iofKey) || tolerance <= 0) return false;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TradePhantoms_IOF_v2") continue;
                    var type   = asm.GetType("TradePhantoms.IOFZoneRegistry");
                    if (type == null) continue;
                    var method = type.GetMethod("IsNearZone",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method == null) continue;
                    return (bool)method.Invoke(null, new object[] { _iofKey, price, tolerance });
                }
            }
            catch { }
            return false;
        }

        // ── Rendering ────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.Symbol == null) return;

            var gr  = args.Graphics;
            var win = this.CurrentChart.MainWindow;

            List<HvnLevel> snapshot;
            lock (_lock) snapshot = new List<HvnLevel>(_levels);
            if (snapshot.Count == 0) return;

            var rect = (System.Drawing.Rectangle)win.ClientRectangle;

            using var labelFont = new Font("Consolas", 8f, FontStyle.Regular);

            foreach (var lvl in snapshot)
            {
                int y;
                try { y = (int)Math.Round((double)win.CoordinatesConverter.GetChartY(lvl.Price)); }
                catch { continue; }

                if (y < rect.Top || y > rect.Bottom) continue;

                Color lineColor = lvl.IsIOFConfluence ? ConfluenceColor
                                : lvl.Context == NodeContext.Backside ? BacksideColor
                                : HvnColor;

                DashStyle dash = lvl.Context switch
                {
                    NodeContext.Backside  => DashStyle.Dash,
                    NodeContext.Building  => DashStyle.DashDot,
                    NodeContext.Isolated  => DashStyle.Dot,
                    _                    => DashStyle.Solid
                };

                using var pen = new Pen(lineColor, LineWidth) { DashStyle = dash };
                gr.DrawLine(pen, rect.Left, y, rect.Right, y);

                if (ShowLabels)
                {
                    string ctx   = lvl.Context.ToString().ToUpper();
                    string pct   = $"+{(lvl.PctAboveAvg - 100):F0}%";
                    string iof   = lvl.IsIOFConfluence ? " ★IOF" : "";
                    string label = $"HVN {ctx} {pct}{iof}";

                    using var brush = new SolidBrush(Color.FromArgb(220, lineColor));
                    gr.DrawString(label, labelFont, brush, rect.Left + 4, y - 14);
                }
            }
        }

        protected override void OnClear()
        {
            lock (_lock) _levels.Clear();
        }
    }
}
