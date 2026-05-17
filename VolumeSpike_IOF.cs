// =============================================================================
// VolumeSpike_IOF.cs — TradePhantoms Volume Spike + IOF Confluence Indicator
// =============================================================================
// Platform : Quantower C# SDK (v1.143.x)
// Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF
//
// Scans the volume profile and draws horizontal lines at price levels where
// volume exceeds a user-defined % above the average of N surrounding levels.
// Classifies each spike context: BUILDING / PEAK / BACKSIDE / ISOLATED.
// Flags confluence when a spike aligns with a live IOF IBI zone (via
// IOFZoneRegistry — requires TradePhantoms_IOF_v2 loaded first).
//
// Profile sources: Session / Daily / Weekly / Monthly (user input).
// All thresholds, colors, line width, extend-bars are adjustable inputs.
//
// IOF zone confluence hook:
//   Calls IOFZoneRegistry.IsNearZone(key, price, tolerance) at render time.
//   Key = "{Symbol.Name}_{chart timeframe period}".
//   Returns false (no confluence) if IOF v2 is not loaded — lines still render.
//
// SDK notes (v1.143.x — confirmed Brandon's Claude 2026-05-17):
//   Volume profile aggregation: HistoryAggregationVolumeProfile
//   AggregationType enum lives in TradingPlatform.BusinessLayer.History.Aggregations
//   See FetchVolumeProfile() for the real implementation pattern.
//
// -----------------------------------------------------------------------------
// PATCH NOTES
// -----------------------------------------------------------------------------
// 2026-05-17: Stubs wired — SDK version and zone registry approach confirmed.
//   - IOFZoneRegistry.IsNearZone() integrated (replaces stub returning false)
//   - FetchVolumeProfile() real API pattern documented; stub returns empty until
//     Christopher confirms v1.143.x sub-path matches his install
//   - LogSwallowed pattern added (matches Brandon's Phase 1.2.4 convention)
//
// 2026-05-16: Initial skeleton build.
//   - Percentage-based spike detection with user-adjustable threshold
//   - BUILDING / PEAK / BACKSIDE / ISOLATED context classification
//   - Multi-profile: Session / Daily / Weekly / Monthly
//   - HUD: lines with context labels, color-coded by regime + confluence
//   - All inputs user-adjustable
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;

namespace TradePhantoms
{
    [Indicator("VolumeSpike_IOF", "Volume Spike IOF", false, Version = "1.1.0")]
    public class VolumeSpike_IOF : Indicator
    {
        // ── User Inputs ────────────────────────────────────────────────────────────

        [InputParameter("Spike Threshold % above avg", sortIndex: 0, minimum: 101, maximum: 1000, increment: 10)]
        public int SpikeThresholdPct = 150;

        [InputParameter("Surrounding Levels (N each side)", sortIndex: 1, minimum: 2, maximum: 30, increment: 1)]
        public int SurroundingN = 5;

        [InputParameter("Profile Timeframe", sortIndex: 2, variants: new object[]
        {
            "Session", 0,
            "Daily",   1,
            "Weekly",  2,
            "Monthly", 3
        })]
        public int ProfileTimeframe = 0;

        [InputParameter("IOF Zone Proximity (ticks)", sortIndex: 3, minimum: 1, maximum: 100, increment: 1)]
        public int IOFZoneProximityTicks = 8;

        [InputParameter("Show Context Labels", sortIndex: 4)]
        public bool ShowContextLabels = true;

        [InputParameter("Extend Line (bars)", sortIndex: 5, minimum: 1, maximum: 1000, increment: 10)]
        public int ExtendBars = 100;

        [InputParameter("Spike Color", sortIndex: 6)]
        public Color SpikeColor = Color.FromArgb(0, 200, 255);

        [InputParameter("IOF Confluence Color", sortIndex: 7)]
        public Color ConfluenceColor = Color.Gold;

        [InputParameter("Backside Color", sortIndex: 8)]
        public Color BacksideColor = Color.FromArgb(255, 100, 60);

        [InputParameter("Line Width", sortIndex: 9, minimum: 1, maximum: 5, increment: 1)]
        public int LineWidth = 2;

        // ── Internal ──────────────────────────────────────────────────────────────

        private readonly List<SpikeLevelInfo> detectedSpikes = new();
        private readonly object spikeLock = new();

        private string _registryKey = "";   // "{Symbol}_{Period}" for IOFZoneRegistry

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        public VolumeSpike_IOF() : base()
        {
            Name           = "VolumeSpike_IOF";
            Description    = "Highlights high-volume price levels; flags IOF IBI zone confluence.";
            SeparateWindow = false;
        }

        protected override void OnInit()
        {
            lock (spikeLock)
                detectedSpikes.Clear();

            // Build registry key once — symbol name + chart period string
            try
            {
                string sym    = Symbol?.Name ?? "UNK";
                string period = HistoricalData?.Aggregation?.ToString() ?? "UNK";
                _registryKey  = $"{sym}_{period}";
            }
            catch { _registryKey = ""; }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason == UpdateReason.NewBar || args.Reason == UpdateReason.HistoricalBar)
                RebuildSpikes();
        }

        // ── Spike Detection ────────────────────────────────────────────────────────

        private void RebuildSpikes()
        {
            var profile = FetchVolumeProfile(ProfileTimeframe);
            if (profile == null || profile.Count < SurroundingN * 2 + 1) return;

            profile.Sort((a, b) => a.Price.CompareTo(b.Price));

            double tickSize = 0;
            try { tickSize = Symbol?.TickSize ?? 0; } catch { }
            double tolerance = IOFZoneProximityTicks * tickSize;

            var newSpikes = new List<SpikeLevelInfo>();

            for (int i = SurroundingN; i < profile.Count - SurroundingN; i++)
            {
                double centerVol = profile[i].Volume;
                double sumVol    = 0;
                for (int j = i - SurroundingN; j <= i + SurroundingN; j++)
                    if (j != i) sumVol += profile[j].Volume;

                double surroundAvg = sumVol / (SurroundingN * 2);
                if (surroundAvg <= 0) continue;

                double pctAbove = (centerVol / surroundAvg) * 100.0;
                if (pctAbove < SpikeThresholdPct) continue;

                bool isConfluence = false;
                if (!string.IsNullOrEmpty(_registryKey) && tolerance > 0)
                {
                    try { isConfluence = IOFZoneRegistry.IsNearZone(_registryKey, profile[i].Price, tolerance); }
                    catch (Exception ex) { LogSwallowed("IsNearZone", ex); }
                }

                newSpikes.Add(new SpikeLevelInfo
                {
                    Price               = profile[i].Price,
                    Volume              = centerVol,
                    PctAboveSurrounding = pctAbove,
                    Context             = ClassifyContext(profile, i),
                    IsIOFConfluence     = isConfluence
                });
            }

            lock (spikeLock)
            {
                detectedSpikes.Clear();
                detectedSpikes.AddRange(newSpikes);
            }
        }

        // ── Context Classification ─────────────────────────────────────────────────

        private SpikeContext ClassifyContext(List<(double Price, double Volume)> profile, int idx)
        {
            bool leftRising   = true;
            bool rightFalling = true;

            for (int j = idx - SurroundingN; j < idx - 1; j++)
                if (profile[j].Volume > profile[j + 1].Volume) { leftRising = false; break; }

            for (int j = idx + 1; j < idx + SurroundingN - 1 && j + 1 < profile.Count; j++)
                if (profile[j].Volume < profile[j + 1].Volume) { rightFalling = false; break; }

            if (leftRising  && rightFalling)  return SpikeContext.Peak;
            if (leftRising  && !rightFalling) return SpikeContext.Building;
            if (!leftRising && rightFalling)  return SpikeContext.Backside;
            return SpikeContext.Isolated;
        }

        // ── Volume Profile Source ──────────────────────────────────────────────────
        //
        // 2026-05-17: SDK version confirmed (v1.143.x). Real implementation
        // pattern below. Uncomment and adapt once Christopher confirms the
        // v1.143.x sub-path matches his Quantower install.
        //
        private List<(double Price, double Volume)> FetchVolumeProfile(int profileType)
        {
            // ── REAL IMPLEMENTATION (v1.143.x pattern — uncomment to activate) ───
            //
            // var aggregationType = profileType switch {
            //     1 => Period.Day,
            //     2 => Period.Week,
            //     3 => Period.Month,
            //     _ => Period.Day   // Session maps to Day in v1.143.x
            // };
            //
            // var aggParams = new HistoryAggregationVolumeProfile(aggregationType);
            // using var hd = Symbol.GetHistory(new HistoryRequestParameters {
            //     Symbol      = Symbol,
            //     Aggregation = aggParams,
            //     FromTime    = HistoricalData[HistoricalData.Count - 1, SeekOriginHistory.Begin]
            //                       .TimeLeft.AddDays(-1),
            //     ToTime      = DateTime.UtcNow
            // });
            //
            // var result = new List<(double Price, double Volume)>();
            // foreach (HistoryItemBar bar in hd)
            //     result.Add((bar.Close, bar.Volume));
            // return result;
            // ─────────────────────────────────────────────────────────────────────

            return new List<(double Price, double Volume)>();
        }

        // ── Rendering ─────────────────────────────────────────────────────────────

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            var gr  = args.Graphics;
            var win = CurrentChart.MainWindow;

            List<SpikeLevelInfo> snapshot;
            lock (spikeLock)
                snapshot = new List<SpikeLevelInfo>(detectedSpikes);

            if (snapshot.Count == 0) return;

            int currentBarX = (int)win.CoordOfTime(Time());
            int leftEdgeX   = (int)win.CoordOfTime(Time(Math.Min(ExtendBars, Count - 1)));

            using var labelFont = new Font("Consolas", 8f, FontStyle.Regular);

            foreach (var spike in snapshot)
            {
                int y = (int)win.CoordOfPrice(spike.Price);

                Color lineColor = spike.IsIOFConfluence ? ConfluenceColor
                                : spike.Context == SpikeContext.Backside ? BacksideColor
                                : SpikeColor;

                DashStyle dash = spike.Context switch
                {
                    SpikeContext.Backside  => DashStyle.Dash,
                    SpikeContext.Building  => DashStyle.DashDot,
                    SpikeContext.Isolated  => DashStyle.Dot,
                    _                      => DashStyle.Solid
                };

                using var pen = new Pen(lineColor, LineWidth) { DashStyle = dash };
                gr.DrawLine(pen, leftEdgeX, y, currentBarX, y);

                if (ShowContextLabels)
                {
                    string ctx   = spike.Context.ToString().ToUpper();
                    string pct   = $"+{(spike.PctAboveSurrounding - 100):F0}%";
                    string iof   = spike.IsIOFConfluence ? " ★IOF" : "";
                    string label = $"{ctx} {pct}{iof}";

                    using var brush = new SolidBrush(Color.FromArgb(220, lineColor));
                    gr.DrawString(label, labelFont, brush, leftEdgeX + 4, y - 14);
                }
            }
        }

        // ── Forensic logging (matches Brandon's Phase 1.2.4 LogSwallowed pattern) ─

        private void LogSwallowed(string site, Exception ex)
        {
            try { Core.Instance.Loggers.Log(ex, $"VolumeSpike_IOF.{site}"); }
            catch { /* never throw from logger */ }
        }
    }

    // ── Supporting Types ───────────────────────────────────────────────────────────

    public enum SpikeContext
    {
        Building,   // 2026-05-16: volume ramping toward this level
        Peak,       // 2026-05-16: symmetric spike — strongest absorption signal
        Backside,   // 2026-05-16: post-peak decline — interest has passed
        Isolated    // 2026-05-16: no clear surrounding trend context
    }

    public class SpikeLevelInfo
    {
        public double       Price               { get; set; }
        public double       Volume              { get; set; }
        public double       PctAboveSurrounding { get; set; }
        public SpikeContext Context             { get; set; }
        public bool         IsIOFConfluence     { get; set; }
    }
}
