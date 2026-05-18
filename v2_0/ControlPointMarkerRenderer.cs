// =============================================================================
// ControlPointMarkerRenderer.cs — TradePhantoms IOF QuickEntry v2.0
// -----------------------------------------------------------------------------
// Self-contained Quantower-side renderer for two related trend artifacts that
// the TrendStateMachine produces:
//
//   1. Control points  — engulfing-candle pivots that anchor a HL (bull) or
//                         LH (bear) controlling level. Drawn as small filled
//                         triangles at the engulfing candle's low/high. The
//                         currently-controlling pivot is annotated with a
//                         small "CTRL" label so traders can immediately tell
//                         which level the trend state machine is watching.
//
//   2. Trend breaks    — the bar where price closed through the controlling
//                         pivot, flipping the trend back to FLAT. Drawn as a
//                         short horizontal dashed line at the broken price
//                         that extends ~50 px to the right of the break bar,
//                         labelled "BR" in the appropriate flip-color.
//
// Drawing contract:
//   - GDI+ via OnPaintChart.
//   - Caller passes priceToY / barIndexToX lambdas so this file has zero
//     dependency on Quantower's coordinate converter (decoupled, testable).
//   - Caller passes the (currently unused) HistoricalData reference so the
//     signature aligns with the rest of the v2 renderers and so future
//     enhancements (e.g. drawing a line back to the engulfing bar) won't
//     require API churn.
//
// Public surface:
//   TradePhantomsIOF.UI.ControlPointMarker
//   TradePhantomsIOF.UI.TrendBreakMarker
//   TradePhantomsIOF.UI.ControlPointMarkerRenderer.DrawControlPoints(...)
//   TradePhantomsIOF.UI.ControlPointMarkerRenderer.DrawTrendBreaks(...)
//
// All drawing is pure with respect to the input lists — no static state.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TradePhantomsIOF.UI
{
    // -------------------------------------------------------------------------
    // Public data contracts. Defined locally so this file does NOT reference
    // TrendStateMachine.cs directly. The master indicator constructs and feeds
    // these instances per paint. Field shapes mirror what TrendStateMachine.cs
    // produces in v2 (engulfing candle pivot + flip event).
    // -------------------------------------------------------------------------

    /// <summary>
    /// One engulfing-candle control point. The pivot price is the engulfing
    /// candle's low (bull, anchoring an HL) or high (bear, anchoring an LH).
    /// IsControllingPivot tags the single CP that is currently in force.
    /// </summary>
    public class ControlPointMarker
    {
        public DateTime Time;
        public int      BarIndex;
        public double   Price;            // engulfing low (bull) / high (bear)
        public bool     IsBull;
        public double   EngulfingHigh;
        public double   EngulfingLow;
        public double   EngulfingOpen;
        public double   EngulfingClose;
        public bool     IsControllingPivot;
    }

    /// <summary>
    /// One trend-break event. WasBull == true → BULL trend was active and was
    /// broken by a close below the controlling HL. BreakBarClose is the close
    /// price of the bar that triggered the flip; BrokenPrice is the pivot.
    /// </summary>
    public class TrendBreakMarker
    {
        public DateTime Time;
        public int      BarIndex;
        public bool     WasBull;
        public double   BrokenPrice;
        public double   BreakBarClose;
    }

    // -------------------------------------------------------------------------
    // Renderer
    // -------------------------------------------------------------------------
    public static class ControlPointMarkerRenderer
    {
        // ---- Layout / appearance constants ----------------------------------
        private const int   CTRL_LABEL_GAP_PX  = 3;     // gap between marker and CTRL label
        private const int   BREAK_LINE_RIGHT   = 50;    // px the dashed line extends
        private const int   BREAK_LABEL_GAP_PX = 4;     // gap between break X and "BR" text
        private const float BREAK_PEN_WIDTH    = 2f;    // thicker pen for break lines
        private const float MARKER_PEN_WIDTH   = 1.25f; // outline pen for triangle border

        // Cached label strings.
        private const string CTRL_LABEL  = "CTRL";
        private const string BREAK_LABEL = "BR";

        // =====================================================================
        // Public entry — Control points
        // =====================================================================

        /// <summary>
        /// Draw all <paramref name="controlPoints"/> as small triangles at their
        /// engulfing-candle pivot price. Bull pivots point up (in
        /// <paramref name="bullColor"/>), bear pivots point down (in
        /// <paramref name="bearColor"/>). The pivot flagged
        /// IsControllingPivot=true gets a small "CTRL" label adjacent to the
        /// triangle (above for bear, below for bull — i.e. away from the chart
        /// candles so the label doesn't overlap price action).
        /// </summary>
        // 2026-05-12 bugfix: signature now takes a TIME-based X resolver
        // instead of a BarIndex-based one. When TrendFromITF=true the
        // marker's BarIndex points into the ITF feed (1H), but the caller
        // was resolving it against the chart-TF HistoricalData (e.g. 5m),
        // producing wildly wrong screen X coordinates ("markers in no
        // man's land"). marker.Time is feed-agnostic — use it directly.
        public static void DrawControlPoints(
            Graphics g,
            Rectangle chartArea,
            object historicalData,                    // accepted for signature parity; not used directly
            Func<double, int> priceToY,
            Func<DateTime, int> timeToX,
            IList<ControlPointMarker> controlPoints,
            Color bullColor,
            Color bearColor,
            int markerSizePx = 8,
            int textSize = 10)
        {
            // ---- Null / empty guards ----
            if (g == null || priceToY == null || timeToX == null) return;
            if (controlPoints == null || controlPoints.Count == 0) return;
            if (chartArea.Width <= 0 || chartArea.Height <= 0) return;

            if (markerSizePx < 4)  markerSizePx = 4;
            if (markerSizePx > 32) markerSizePx = 32;
            if (textSize     < 6)  textSize     = 6;
            if (textSize     > 24) textSize     = 24;

            // Save graphics state — restored in finally so callers see the
            // same SmoothingMode / TextRenderingHint they passed in.
            var prevSmoothing  = g.SmoothingMode;
            var prevTextRender = g.TextRenderingHint;

            try
            {
                g.SmoothingMode    = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // ---- One-time GDI resource block per paint ----
                using (var bullFill   = new SolidBrush(bullColor))
                using (var bearFill   = new SolidBrush(bearColor))
                using (var bullOutline = new Pen(DarkenColor(bullColor, 0.55f), MARKER_PEN_WIDTH))
                using (var bearOutline = new Pen(DarkenColor(bearColor, 0.55f), MARKER_PEN_WIDTH))
                using (var ctrlOutline = new Pen(Color.FromArgb(220, 240, 240, 240), 1.25f))
                using (var ctrlBgBrush = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                using (var ctrlFgBrush = new SolidBrush(Color.White))
                using (var ctrlFont   = BuildLabelFont(textSize))
                {
                    // ---- Loop control points ----
                    for (int i = 0; i < controlPoints.Count; i++)
                    {
                        var cp = controlPoints[i];
                        if (cp == null) continue;

                        // ---- Resolve screen coordinates via caller lambdas.
                        // 2026-05-12: use marker.Time (feed-agnostic) to
                        // avoid the ITF-vs-chart-TF index mismatch bug.
                        int x, y;
                        try
                        {
                            x = timeToX(cp.Time);
                            y = priceToY(cp.Price);
                        }
                        catch
                        {
                            // Defensive: a misbehaving lambda must not crash paint.
                            continue;
                        }

                        // ---- Off-screen culling ---------------------------------
                        // Allow markerSizePx of slop so a marker hugging the edge
                        // still draws its visible portion correctly.
                        if (x + markerSizePx < chartArea.Left)  continue;
                        if (x - markerSizePx > chartArea.Right) continue;
                        if (y + markerSizePx < chartArea.Top)   continue;
                        if (y - markerSizePx > chartArea.Bottom) continue;

                        // ---- Build triangle ------------------------------------
                        // Bull → triangle pointing UP, anchored at the bottom
                        //        center on the engulfing low.
                        // Bear → triangle pointing DOWN, anchored at the top
                        //        center on the engulfing high.
                        Point[] tri = BuildTriangle(x, y, markerSizePx, cp.IsBull);

                        // ---- Fill + outline ------------------------------------
                        SolidBrush fill   = cp.IsBull ? bullFill   : bearFill;
                        Pen        border = cp.IsBull ? bullOutline : bearOutline;
                        g.FillPolygon(fill, tri);
                        g.DrawPolygon(border, tri);

                        // ---- CTRL annotation for the active controlling pivot --
                        if (cp.IsControllingPivot)
                        {
                            DrawCtrlLabel(g, ctrlFont, ctrlBgBrush, ctrlFgBrush, ctrlOutline,
                                          x, y, markerSizePx, cp.IsBull, chartArea);
                        }
                    }
                }
            }
            finally
            {
                g.SmoothingMode    = prevSmoothing;
                g.TextRenderingHint = prevTextRender;
            }
        }

        // =====================================================================
        // Public entry — Trend breaks
        // =====================================================================

        /// <summary>
        /// Draw all <paramref name="breaks"/> as a short dashed horizontal line
        /// at the broken-pivot price, extending ~50 px to the right of the
        /// break bar. Each break is captioned "BR" in
        /// <paramref name="bullToFlatColor"/> when a bull trend broke, or
        /// <paramref name="bearToFlatColor"/> when a bear trend broke.
        /// </summary>
        // 2026-05-12 bugfix: same TIME-based resolver pattern as
        // DrawControlPoints — see comment there.
        public static void DrawTrendBreaks(
            Graphics g,
            Rectangle chartArea,
            object historicalData,                    // accepted for signature parity; not used directly
            Func<double, int> priceToY,
            Func<DateTime, int> timeToX,
            IList<TrendBreakMarker> breaks,
            Color bullToFlatColor,
            Color bearToFlatColor,
            int textSize = 10)
        {
            // ---- Null / empty guards ----
            if (g == null || priceToY == null || timeToX == null) return;
            if (breaks == null || breaks.Count == 0) return;
            if (chartArea.Width <= 0 || chartArea.Height <= 0) return;

            if (textSize < 6)  textSize = 6;
            if (textSize > 24) textSize = 24;

            var prevSmoothing  = g.SmoothingMode;
            var prevTextRender = g.TextRenderingHint;

            try
            {
                g.SmoothingMode    = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                using (var bullPen = new Pen(bullToFlatColor, BREAK_PEN_WIDTH) { DashStyle = DashStyle.Dash })
                using (var bearPen = new Pen(bearToFlatColor, BREAK_PEN_WIDTH) { DashStyle = DashStyle.Dash })
                using (var bullBrush = new SolidBrush(bullToFlatColor))
                using (var bearBrush = new SolidBrush(bearToFlatColor))
                using (var bgBrush   = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                using (var labelFont = BuildLabelFont(textSize))
                {
                    for (int i = 0; i < breaks.Count; i++)
                    {
                        var br = breaks[i];
                        if (br == null) continue;

                        // 2026-05-12: use marker.Time (feed-agnostic) to avoid
                        // the ITF-vs-chart-TF index mismatch bug that put
                        // break markers in "no man's land" on the chart.
                        int x, y;
                        try
                        {
                            x = timeToX(br.Time);
                            y = priceToY(br.BrokenPrice);
                        }
                        catch
                        {
                            // A misbehaving lambda should never crash paint.
                            continue;
                        }

                        // ---- Off-screen culling ---------------------------------
                        // The dashed line extends BREAK_LINE_RIGHT pixels to the
                        // right; treat the whole segment when deciding whether
                        // it's visible. We additionally allow a small slop on
                        // the y axis so a near-edge break still paints.
                        int xRight = x + BREAK_LINE_RIGHT;
                        if (xRight < chartArea.Left)   continue;
                        if (x      > chartArea.Right)  continue;
                        if (y < chartArea.Top - 2 || y > chartArea.Bottom + 2) continue;

                        // ---- Pen / brush selection by direction -----------------
                        Pen        pen   = br.WasBull ? bullPen   : bearPen;
                        SolidBrush brush = br.WasBull ? bullBrush : bearBrush;

                        // ---- Dashed horizontal line at the broken price --------
                        // Clamp to chart bounds so the dashes don't bleed outside.
                        int x0 = Math.Max(x,      chartArea.Left);
                        int x1 = Math.Min(xRight, chartArea.Right);
                        if (x1 > x0)
                        {
                            g.DrawLine(pen, x0, y, x1, y);
                        }

                        // ---- "BR" label, drawn at the break bar's X -------------
                        // Background pill behind the text so it stays legible
                        // over candles and gridlines.
                        DrawBreakLabel(g, labelFont, bgBrush, brush, x, y,
                                       br.WasBull, chartArea);
                    }
                }
            }
            finally
            {
                g.SmoothingMode    = prevSmoothing;
                g.TextRenderingHint = prevTextRender;
            }
        }

        // =====================================================================
        // Internal helpers
        // =====================================================================

        // Build a 3-point triangle. Bull: apex up, base anchored at (x,y).
        // Bear: apex down, base anchored at (x,y).
        //
        // markerSize is the triangle's base width AND height (so the triangle
        // is approximately equilateral-ish for visual balance). The base sits
        // on (or just above/below) the pivot price y.
        private static Point[] BuildTriangle(int x, int y, int markerSize, bool isBull)
        {
            int half = markerSize / 2;
            if (isBull)
            {
                // Apex points UP (above the engulfing low). Base sits just
                // BELOW the pivot price so the apex visually pokes the
                // engulfing candle from underneath.
                return new[]
                {
                    new Point(x - half, y + markerSize),       // bottom-left
                    new Point(x + half, y + markerSize),       // bottom-right
                    new Point(x,        y),                    // apex up at pivot price
                };
            }
            else
            {
                // Apex points DOWN. Base sits just ABOVE the pivot price.
                return new[]
                {
                    new Point(x - half, y - markerSize),       // top-left
                    new Point(x + half, y - markerSize),       // top-right
                    new Point(x,        y),                    // apex down at pivot price
                };
            }
        }

        // Draw the "CTRL" badge for the active controlling pivot. Positioned
        // OPPOSITE the marker apex so it never overlaps the marker:
        //   Bull marker (apex up)   → CTRL drawn ABOVE the marker.
        //   Bear marker (apex down) → CTRL drawn BELOW the marker.
        private static void DrawCtrlLabel(Graphics g, Font font, SolidBrush bg, SolidBrush fg,
                                          Pen border, int x, int y, int markerSize, bool isBull,
                                          Rectangle chartArea)
        {
            SizeF sz = g.MeasureString(CTRL_LABEL, font);
            int padX = 3, padY = 1;
            int w = (int)Math.Ceiling(sz.Width)  + padX * 2;
            int h = (int)Math.Ceiling(sz.Height) + padY * 2;

            // Compute label rect relative to triangle. Triangle apex is at y;
            // base sits +/- markerSize from y depending on isBull.
            int labelX = x - w / 2;
            int labelY;
            if (isBull)
            {
                // CTRL above the marker. Apex is at y; place label above apex.
                labelY = y - h - CTRL_LABEL_GAP_PX;
            }
            else
            {
                // CTRL below the marker. Apex is at y; base extends to y - markerSize.
                // Place label below apex (y), gap below.
                labelY = y + CTRL_LABEL_GAP_PX;
            }

            // Clamp inside chart area so the label is never cropped.
            if (labelX < chartArea.Left)  labelX = chartArea.Left;
            if (labelX + w > chartArea.Right)  labelX = chartArea.Right  - w;
            if (labelY < chartArea.Top)   labelY = chartArea.Top;
            if (labelY + h > chartArea.Bottom) labelY = chartArea.Bottom - h;

            var rect = new Rectangle(labelX, labelY, w, h);
            g.FillRectangle(bg, rect);
            g.DrawRectangle(border, rect);
            g.DrawString(CTRL_LABEL, font, fg, labelX + padX, labelY + padY);
        }

        // Draw the "BR" caption next to a break event. Placed slightly to the
        // right of the break bar, on the same side as the dashed line, with a
        // semi-transparent black backing pill for legibility.
        private static void DrawBreakLabel(Graphics g, Font font, SolidBrush bg, SolidBrush fg,
                                           int x, int y, bool wasBull, Rectangle chartArea)
        {
            SizeF sz = g.MeasureString(BREAK_LABEL, font);
            int padX = 3, padY = 1;
            int w = (int)Math.Ceiling(sz.Width)  + padX * 2;
            int h = (int)Math.Ceiling(sz.Height) + padY * 2;

            // Anchor: just to the right of the break bar X, vertically offset
            // so the label sits ABOVE a bull-broken line (pointing toward
            // where price went — down) and BELOW a bear-broken line (pointing
            // up). This gives a natural "trend has flipped this way" hint.
            int labelX = x + BREAK_LABEL_GAP_PX;
            int labelY = wasBull ? y - h - 2 : y + 2;

            // Clamp inside chart area.
            if (labelX + w > chartArea.Right) labelX = chartArea.Right - w;
            if (labelX < chartArea.Left)      labelX = chartArea.Left;
            if (labelY < chartArea.Top)       labelY = chartArea.Top;
            if (labelY + h > chartArea.Bottom) labelY = chartArea.Bottom - h;

            var rect = new Rectangle(labelX, labelY, w, h);
            g.FillRectangle(bg, rect);
            g.DrawString(BREAK_LABEL, font, fg, labelX + padX, labelY + padY);
        }

        // Build a small bold sans-serif font for badge labels. Falls back to
        // the GDI+ default sans-serif if Segoe UI is unavailable.
        private static Font BuildLabelFont(int textSize)
        {
            try
            {
                return new Font("Segoe UI", textSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, textSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }
        }

        // Darken a color by the given factor (0.0 = black, 1.0 = unchanged).
        // Used for triangle outlines so a bright fill still has a crisp edge.
        private static Color DarkenColor(Color c, float factor)
        {
            if (factor < 0f) factor = 0f;
            if (factor > 1f) factor = 1f;
            int r = (int)Math.Round(c.R * factor);
            int g = (int)Math.Round(c.G * factor);
            int b = (int)Math.Round(c.B * factor);
            return Color.FromArgb(c.A, r, g, b);
        }
    }
}
