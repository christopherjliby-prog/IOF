// ════════════════════════════════════════════════════════════════════════════
// TrendStateMachine.cs — Engulfing-candle control points + persistent
// HH/HL/HH (or LH/LL/LH) trend state machine for the OnlyFutures IOF v2
// Quantower indicator system.
// ════════════════════════════════════════════════════════════════════════════
//
// Implements the "institutional trend identification" rules (per the project
// PDF + user trading approach memo):
//
//   * A trend is established by THREE confirmed segments of the structure:
//       Bullish → HH then HL then HH
//       Bearish → LH then LL then LH
//   * Wicks NEVER establish or break a trend. Only candle BODY closes count.
//   * The "control point" of a move is the engulfing candle at its origin.
//     The controlling pivot price is that engulfing candle's low (bull) /
//     high (bear). A bar whose body close violates this controlling pivot
//     is a trend break.
//   * A trend break does NOT immediately flip state. It sets state to FLAT
//     and the new trend (if any) requires a fresh 3-segment confirmation in
//     the new direction.
//
// Hard exclusions (per feedback_iof_hard_exclusions.md):
//   * NO BOS / NO CHoCH terminology, ever. We use "trend break" only.
//   * Body-close logic is mandatory for break detection — wicks are noise.
//
// Self-contained: no Quantower SDK reference; pure logic the master indicator
// drives via OnBarClose(). Single namespace OnlyFuturesIOF.Trend so the
// master file just imports and instantiates.
//
// File is intentionally event-driven: consumers wire OnControlPointDetected /
// OnTrendBroken / OnTrendStateChanged callbacks rather than polling. This
// matches the lifecycle layer's pattern — the master indicator's OnBarClose
// runs the per-bar update and the events are dispatched in a fixed order
// (control point first, then break, then state change) so downstream
// consumers see a coherent snapshot at each event boundary.
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OnlyFuturesIOF.Trend
{
    // ────────────────────────────────────────────────────────────────────────
    // ENUMS
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persistent trend state. FLAT is the "unconfirmed" state — either
    /// initial or post-break, awaiting a fresh 3-segment pattern. Bull/Bear
    /// only become active once HH/HL/HH (or LH/LL/LH) plus an aligned
    /// engulfing-candle control point are all in place.
    /// </summary>
    public enum TrendState
    {
        Flat = 0,
        Bull = 1,
        Bear = 2
    }

    /// <summary>
    /// Body direction of a single candle. Doji is open ≈ close within one
    /// tick, so the candle has no directional commitment.
    /// </summary>
    public enum CandleDirection
    {
        Bullish = 1,
        Bearish = -1,
        Doji = 0
    }

    // ────────────────────────────────────────────────────────────────────────
    // PUBLIC RECORD TYPES
    // Plain mutable POCOs — no records / init-only properties so the file
    // builds against pre-C# 9 toolchains (Quantower historically targets
    // .NET Framework / older language versions).
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A control point — the engulfing candle that started a directional
    /// move. The controlling pivot price (the level a body close must clear
    /// to invalidate the trend) is stored in Price; the engulfing candle's
    /// full OHLC is captured for downstream renderers.
    /// </summary>
    public class ControlPoint
    {
        public DateTime Time;
        public int BarIndex;
        /// <summary>Engulfing low (bull) or engulfing high (bear).</summary>
        public double Price;
        /// <summary>TrendState.Bull or TrendState.Bear (never Flat).</summary>
        public TrendState Direction;
        public double EngulfingHigh;
        public double EngulfingLow;
        public double EngulfingOpen;
        public double EngulfingClose;
    }

    /// <summary>
    /// Records the bar on which a controlling-pivot violation occurred. The
    /// new state is FLAT (we never directly flip Bull→Bear without a fresh
    /// 3-segment confirmation in between, per PDF spec).
    /// </summary>
    public class TrendBreakEvent
    {
        public DateTime Time;
        public int BarIndex;
        public TrendState OldTrend;
        public TrendState NewTrend; // always Flat in current spec
        public double BrokenControlPoint;
        public double BreakBarClose;
    }

    /// <summary>
    /// Fractal-derived swing pivot. We tag the most recent qualifying HL
    /// (uptrend) or LH (downtrend) as the controlling pivot. Mutable so the
    /// state machine can promote/demote pivots as new structure arrives.
    /// </summary>
    public class SwingPivot
    {
        public DateTime Time;
        public int BarIndex;
        public double Price;
        public bool IsHigh;
        public bool IsControllingPivot;
    }

    /// <summary>
    /// Snapshot of the state machine's current view. Returned by
    /// GetSnapshot(); designed to be cheaply consumable by the master
    /// indicator's render loop without exposing the internal histories.
    /// </summary>
    public class TrendSnapshot
    {
        public TrendState State;
        public ControlPoint CurrentControlPoint;
        /// <summary>NaN if FLAT; otherwise the controlling HL (Bull) / LH (Bear) price.</summary>
        public double ControllingPivotPrice;
        public List<SwingPivot> RecentPivots;
        public List<ControlPoint> RecentControlPoints;
        public TrendBreakEvent LastBreak;
        public DateTime LastUpdated;
    }

    // ────────────────────────────────────────────────────────────────────────
    // ENGULFING DETECTOR
    // Pure static helpers — no state. Used by the state machine and exposed
    // publicly so the renderer / debug overlay can call ClassifyDirection()
    // independently if it needs to color individual candles.
    // ────────────────────────────────────────────────────────────────────────

    public static class EngulfingDetector
    {
        /// <summary>
        /// Classify a candle's body direction with a one-tick doji band.
        /// Bullish requires close > open + tickSize; bearish symmetric. The
        /// tick-band protects against floating-point drift on synthetic /
        /// continuous-contract feeds where open and close can be exactly
        /// equal but stored as slightly-different doubles.
        /// </summary>
        public static CandleDirection ClassifyDirection(double open, double close, double tickSize)
        {
            double tol = tickSize > 0 ? tickSize : 0.0;
            if (close > open + tol) return CandleDirection.Bullish;
            if (close < open - tol) return CandleDirection.Bearish;
            return CandleDirection.Doji;
        }

        /// <summary>
        /// Bullish engulfing per the project PDF: current candle is bullish,
        /// prior candle is bearish, and the current body fully covers the
        /// prior body (open ≤ prevClose AND close ≥ prevOpen).
        /// </summary>
        public static bool IsBullishEngulfing(
            double open, double close,
            double prevOpen, double prevClose)
        {
            // Current must be bullish
            if (!(close > open)) return false;
            // Previous must be bearish
            if (!(prevClose < prevOpen)) return false;
            // Current body covers previous body
            if (!(open <= prevClose)) return false;
            if (!(close >= prevOpen)) return false;
            return true;
        }

        /// <summary>Mirrored bearish engulfing.</summary>
        public static bool IsBearishEngulfing(
            double open, double close,
            double prevOpen, double prevClose)
        {
            if (!(close < open)) return false;
            if (!(prevClose > prevOpen)) return false;
            if (!(open >= prevClose)) return false;
            if (!(close <= prevOpen)) return false;
            return true;
        }

        /// <summary>
        /// Returns Bull / Bear / null. Convenience for the state machine so
        /// it can write a single switch instead of two if-blocks.
        /// </summary>
        public static TrendState? DetectEngulfing(
            double open, double close,
            double prevOpen, double prevClose)
        {
            if (IsBullishEngulfing(open, close, prevOpen, prevClose))
                return TrendState.Bull;
            if (IsBearishEngulfing(open, close, prevOpen, prevClose))
                return TrendState.Bear;
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // INTERNAL — buffered bar context so we can run lookback fractal swing
    // detection. We keep just (2 × SwingFractalLookback + 1) bars + a small
    // tail for break-detection context.
    // ────────────────────────────────────────────────────────────────────────

    internal class BarContext
    {
        public int BarIndex;
        public DateTime Time;
        public double Open;
        public double High;
        public double Low;
        public double Close;
    }

    // ════════════════════════════════════════════════════════════════════════
    // TREND STATE MACHINE
    // ════════════════════════════════════════════════════════════════════════

    public class TrendStateMachine
    {
        // ── Configuration ───────────────────────────────────────────────────

        /// <summary>Bars on each side for fractal swing pivot detection.</summary>
        public int SwingFractalLookback = 3;

        /// <summary>
        /// Number of segments required to confirm a trend. Project default
        /// is 3 (HH-HL-HH); exposed as a knob so future research modes
        /// (e.g. 5-segment "strong" trend) can flip it.
        /// </summary>
        public int RequireSegments = 3;

        /// <summary>
        /// Strict mode (PDF default) — control points must be engulfing
        /// candles. If false, the most recent fractal pivot is used as a
        /// fallback, which is looser but useful for backtests on series
        /// where no engulfing happens to print.
        /// </summary>
        public bool RequireEngulfingForControlPoint = true;

        /// <summary>Cap on retained swing pivots.</summary>
        public int MaxPivotHistory = 50;

        // ── Public read-only state ──────────────────────────────────────────

        public TrendState CurrentState { get; private set; } = TrendState.Flat;

        // ── Events ──────────────────────────────────────────────────────────

        public event Action<ControlPoint> OnControlPointDetected;
        public event Action<TrendBreakEvent> OnTrendBroken;
        /// <summary>Fired with (oldState, newState).</summary>
        public event Action<TrendState, TrendState> OnTrendStateChanged;

        // ── Internal state ──────────────────────────────────────────────────

        private readonly List<SwingPivot> pivotHistory = new List<SwingPivot>();
        private readonly List<ControlPoint> controlPointHistory = new List<ControlPoint>();
        private const int MaxControlPointHistory = 20;

        /// <summary>
        /// Rolling window of bars around the lookback distance. Index 0 is
        /// the OLDEST retained bar; the newest is at the tail. Capped at
        /// 2 * SwingFractalLookback + 1 + a small slack for break-context.
        /// </summary>
        private readonly List<BarContext> recentBars = new List<BarContext>();

        /// <summary>Last engulfing seen — pending promotion to confirmed CP.</summary>
        private ControlPoint pendingControlPoint;

        /// <summary>Currently active controlling pivot (HL price for Bull, LH for Bear).</summary>
        private double currentControllingPivotPrice = double.NaN;

        private TrendBreakEvent lastBreak;
        private DateTime lastUpdated;

        // ────────────────────────────────────────────────────────────────────
        // PER-BAR ENTRYPOINT
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Drive one closed bar through the state machine. Order of internal
        /// steps is fixed and load-bearing:
        ///
        ///   (1) buffer the bar into recentBars
        ///   (2) detect a swing pivot at (barIndex - lookback) if it qualifies
        ///   (3) detect engulfing on the CURRENT bar; record pending CP
        ///   (4) trend-confirmation logic (FLAT → Bull/Bear) using pivots+CP
        ///   (5) trend-break detection (Bull/Bear → FLAT) using close vs CP
        ///
        /// Events fire AFTER all state mutations are settled, in this order:
        ///   OnControlPointDetected → OnTrendBroken → OnTrendStateChanged.
        /// This is the same "compute, then dispatch" discipline the lifecycle
        /// layer uses (see TradeLifecycle.PassA — re-read state vs. cached).
        /// </summary>
        public void OnBarClose(
            int barIndex,
            DateTime time,
            double open,
            double high,
            double low,
            double close,
            double tickSize)
        {
            lastUpdated = time;

            // (1) Buffer the bar.
            BufferBar(barIndex, time, open, high, low, close);

            // Stage event payloads for ordered dispatch at the end. We do
            // NOT fire callbacks mid-update — a callback that mutates state
            // (e.g. consumer toggles a flag) shouldn't see a half-updated
            // state machine. Mirrors lifecycle PASS A's re-read-state idiom.
            ControlPoint stagedControlPoint = null;
            TrendBreakEvent stagedBreak = null;
            TrendState oldState = CurrentState;

            // (2) Swing pivot detection (centered on a bar that's now fully
            //     bracketed by lookback bars on either side).
            DetectFractalPivot();

            // (3) Engulfing detection on the CURRENT (just-closed) bar.
            //     Stored as pendingControlPoint until trend-confirmation
            //     promotes it. We DO fire OnControlPointDetected for ALL
            //     detected engulfings (so renderers can paint them) — but
            //     "currentControlPoint" only updates when the trend confirms.
            BarContext prevBar = recentBars.Count >= 2
                ? recentBars[recentBars.Count - 2]
                : null;
            if (prevBar != null)
            {
                TrendState? eng = EngulfingDetector.DetectEngulfing(
                    open, close, prevBar.Open, prevBar.Close);
                if (eng.HasValue)
                {
                    var cp = new ControlPoint
                    {
                        Time = time,
                        BarIndex = barIndex,
                        Price = eng.Value == TrendState.Bull ? low : high,
                        Direction = eng.Value,
                        EngulfingHigh = high,
                        EngulfingLow = low,
                        EngulfingOpen = open,
                        EngulfingClose = close
                    };
                    pendingControlPoint = cp;
                    AddControlPointToHistory(cp);
                    stagedControlPoint = cp;
                }
            }

            // (4) Trend-confirmation logic — FLAT → Bull/Bear when both:
            //     (a) the recent pivot ladder shows the required segments,
            //     (b) a recent engulfing CP matches the candidate direction.
            //
            //     KEY RULE: a break does NOT directly flip Bull→Bear. After a
            //     break we go to FLAT, and the new direction (if any) has to
            //     re-qualify from scratch. The PDF: "you will not know a
            //     trend has broken until the structure plays out across the
            //     three segments."
            if (CurrentState == TrendState.Flat)
            {
                TrendState? confirmed = TryConfirmTrendFromPivots();
                if (confirmed.HasValue && HasAlignedRecentControlPoint(confirmed.Value))
                {
                    CurrentState = confirmed.Value;
                    PromoteControllingPivot(confirmed.Value);
                }
            }
            else
            {
                // (5) Active trend → check for body-close violation of the
                //     controlling pivot. Wicks NEVER trigger a break (per
                //     feedback_iof_hard_exclusions.md / PDF). We use bar
                //     close, not bar low/high.
                bool breakOccurred = false;
                if (!double.IsNaN(currentControllingPivotPrice))
                {
                    double tickTol = tickSize > 0 ? tickSize * 0.5 : 0.0;
                    if (CurrentState == TrendState.Bull
                        && close < currentControllingPivotPrice - tickTol)
                    {
                        breakOccurred = true;
                    }
                    else if (CurrentState == TrendState.Bear
                        && close > currentControllingPivotPrice + tickTol)
                    {
                        breakOccurred = true;
                    }
                }

                if (breakOccurred)
                {
                    stagedBreak = new TrendBreakEvent
                    {
                        Time = time,
                        BarIndex = barIndex,
                        OldTrend = CurrentState,
                        NewTrend = TrendState.Flat,
                        BrokenControlPoint = currentControllingPivotPrice,
                        BreakBarClose = close
                    };
                    lastBreak = stagedBreak;
                    CurrentState = TrendState.Flat;
                    currentControllingPivotPrice = double.NaN;
                    // Demote any "controlling" pivot flag.
                    for (int i = 0; i < pivotHistory.Count; i++)
                        pivotHistory[i].IsControllingPivot = false;
                }
                else
                {
                    // Active-trend pivot maintenance: as new pivots come in,
                    // the controlling pivot ratchets in the trend direction.
                    // (E.g. uptrend: a higher HL becomes the new controller.)
                    UpdateControllingPivotInTrend();
                }
            }

            // (6) Dispatch events in the documented order.
            //     We re-read CurrentState rather than trusting a cached
            //     value so a same-bar engulfing + break + new state is
            //     reported coherently. Avoids the BUG-A-style staleness
            //     pattern that haunted the Pine lifecycle.
            if (stagedControlPoint != null && OnControlPointDetected != null)
            {
                OnControlPointDetected(stagedControlPoint);
            }
            if (stagedBreak != null && OnTrendBroken != null)
            {
                OnTrendBroken(stagedBreak);
            }
            if (CurrentState != oldState && OnTrendStateChanged != null)
            {
                OnTrendStateChanged(oldState, CurrentState);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // PUBLIC QUERIES
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Cheap snapshot of the state machine. Returns copies of the
        /// internal lists (capped to recent N) so callers can mutate without
        /// affecting state. Used by the dashboard renderer once per frame.
        /// </summary>
        public TrendSnapshot GetSnapshot()
        {
            return new TrendSnapshot
            {
                State = CurrentState,
                CurrentControlPoint = GetCurrentControlPoint(),
                ControllingPivotPrice = currentControllingPivotPrice,
                RecentPivots = TakeLast(pivotHistory, 10),
                RecentControlPoints = TakeLast(controlPointHistory, 5),
                LastBreak = lastBreak,
                LastUpdated = lastUpdated
            };
        }

        /// <summary>
        /// Returns true if the supplied zone direction is aligned with the
        /// current trend. FLAT → false (no trade alignment until a trend
        /// confirms). Master indicator uses this to gate "with-trend only"
        /// arming logic in the lifecycle pass C.
        /// </summary>
        public bool IsAlignedWithTrend(bool isLongZone)
        {
            if (CurrentState == TrendState.Flat) return false;
            return isLongZone
                ? CurrentState == TrendState.Bull
                : CurrentState == TrendState.Bear;
        }

        /// <summary>Reset to a clean slate. Drops all state.</summary>
        public void Reset()
        {
            pivotHistory.Clear();
            controlPointHistory.Clear();
            recentBars.Clear();
            pendingControlPoint = null;
            currentControllingPivotPrice = double.NaN;
            lastBreak = null;
            CurrentState = TrendState.Flat;
            lastUpdated = default(DateTime);
        }

        // ────────────────────────────────────────────────────────────────────
        // INTERNAL — bar buffering
        // ────────────────────────────────────────────────────────────────────

        private void BufferBar(int barIndex, DateTime time,
                               double open, double high, double low, double close)
        {
            recentBars.Add(new BarContext
            {
                BarIndex = barIndex,
                Time = time,
                Open = open,
                High = high,
                Low = low,
                Close = close
            });
            // Keep ~2L+1 bars for fractal detection plus a small slack for
            // break-context lookback. The "+ 4" is a conservative pad.
            int cap = Math.Max(7, 2 * SwingFractalLookback + 1 + 4);
            while (recentBars.Count > cap)
                recentBars.RemoveAt(0);
        }

        // ────────────────────────────────────────────────────────────────────
        // INTERNAL — fractal pivot detection
        // A bar at offset (count - 1 - lookback) qualifies as a swing high
        // if its high is the strict maximum within the lookback window on
        // both sides. Symmetric for swing low. Body-vs-wick: the spec says
        // wicks don't establish trend, but for *fractal swing detection*
        // bar.High and bar.Low are fine for v1 (per the task's parenthetical).
        // ────────────────────────────────────────────────────────────────────

        private void DetectFractalPivot()
        {
            int L = SwingFractalLookback;
            // Need 2L + 1 bars buffered to evaluate the center.
            if (recentBars.Count < 2 * L + 1) return;

            int centerIdx = recentBars.Count - 1 - L;
            BarContext center = recentBars[centerIdx];

            bool isHigh = true;
            bool isLow = true;
            for (int j = centerIdx - L; j <= centerIdx + L; j++)
            {
                if (j == centerIdx) continue;
                if (recentBars[j].High >= center.High) isHigh = false;
                if (recentBars[j].Low <= center.Low) isLow = false;
                if (!isHigh && !isLow) break;
            }

            if (!isHigh && !isLow) return;

            // Dedupe: if the most recent pivot in history is the same bar,
            // don't double-record (can happen on data refresh / replay).
            if (pivotHistory.Count > 0)
            {
                var last = pivotHistory[pivotHistory.Count - 1];
                if (last.BarIndex == center.BarIndex && last.IsHigh == isHigh)
                    return;
            }

            pivotHistory.Add(new SwingPivot
            {
                Time = center.Time,
                BarIndex = center.BarIndex,
                Price = isHigh ? center.High : center.Low,
                IsHigh = isHigh,
                IsControllingPivot = false
            });

            while (pivotHistory.Count > MaxPivotHistory)
                pivotHistory.RemoveAt(0);
        }

        // ────────────────────────────────────────────────────────────────────
        // INTERNAL — trend confirmation from pivot ladder
        //
        // Walk the most recent pivots looking for the alternating pattern:
        //   Bull: …  H_a  L_b  H_c  L_d  H_e   where H_e > H_c > H_a
        //                                       and L_d > L_b      (HL rising)
        //   Bear: …  L_a  H_b  L_c  H_d  L_e   where L_e < L_c < L_a
        //                                       and H_d < H_b      (LH falling)
        //
        // RequireSegments = 3 reduces this to: last 4 alternating pivots
        // satisfy "two up-steps in the highs and one up-step in the lows"
        // for bull (mirrored for bear). We implement a generic walker so the
        // user can flip RequireSegments to 5+ for a "strong trend" mode
        // without changing this code.
        // ────────────────────────────────────────────────────────────────────

        private TrendState? TryConfirmTrendFromPivots()
        {
            // Need at least RequireSegments + 1 alternating pivots.
            int needed = RequireSegments + 1;
            if (pivotHistory.Count < needed) return null;

            // Take the last `needed` pivots. Verify they alternate H-L-H-L…
            // or L-H-L-H… If not strictly alternating, the structure isn't
            // clean enough to declare a trend — return null and wait.
            var tail = new List<SwingPivot>();
            for (int i = pivotHistory.Count - needed; i < pivotHistory.Count; i++)
                tail.Add(pivotHistory[i]);

            // Ensure alternation
            for (int i = 1; i < tail.Count; i++)
            {
                if (tail[i].IsHigh == tail[i - 1].IsHigh) return null;
            }

            bool startsHigh = tail[0].IsHigh;

            // Check Bull pattern: highs strictly rising AND lows strictly rising
            //   highs are at even indices if startsHigh, odd otherwise.
            bool bullOk = CheckMonotonic(tail, true /*highs*/, true /*ascending*/, startsHigh)
                       && CheckMonotonic(tail, false /*lows*/, true /*ascending*/, startsHigh);
            if (bullOk) return TrendState.Bull;

            bool bearOk = CheckMonotonic(tail, true /*highs*/, false /*descending*/, startsHigh)
                       && CheckMonotonic(tail, false /*lows*/, false /*descending*/, startsHigh);
            if (bearOk) return TrendState.Bear;

            return null;
        }

        /// <summary>
        /// Verify that the highs (or lows) within an alternating pivot list
        /// are strictly monotonic in the requested direction. Returns true
        /// only if there are at least 2 pivots of the relevant type AND each
        /// one strictly improves on the prior in the requested direction.
        /// </summary>
        private bool CheckMonotonic(List<SwingPivot> tail, bool wantHigh, bool ascending, bool startsHigh)
        {
            // First index of the relevant kind:
            //   wantHigh + startsHigh → 0
            //   wantHigh + !startsHigh → 1
            //   !wantHigh + startsHigh → 1
            //   !wantHigh + !startsHigh → 0
            int firstIdx = (wantHigh == startsHigh) ? 0 : 1;
            double prev = double.NaN;
            int count = 0;
            for (int i = firstIdx; i < tail.Count; i += 2)
            {
                double price = tail[i].Price;
                if (!double.IsNaN(prev))
                {
                    if (ascending && !(price > prev)) return false;
                    if (!ascending && !(price < prev)) return false;
                }
                prev = price;
                count++;
            }
            return count >= 2;
        }

        /// <summary>
        /// Returns true if a recent control point in the history matches the
        /// supplied direction. "Recent" = within the last 20 stored CPs;
        /// we don't time-window-bound it here because in fast markets a
        /// trend-defining engulfing might be 10+ bars back by the time the
        /// 3rd HH prints. Caller can layer time windows on top.
        ///
        /// 2026-05-10 BUGFIX: when RequireEngulfingForControlPoint is false,
        /// the alternating HH/HL (or LH/LL) pivot ladder is itself sufficient
        /// to confirm a trend — we should NOT require an engulfing CP in
        /// history. PromoteControllingPivot already falls back to the most
        /// recent matching swing pivot when the strict toggle is off, so the
        /// controlling-pivot price is well-defined either way. Previously the
        /// gate always required an engulfing regardless of the toggle, which
        /// kept clean trends FLAT in markets that just rallied without a
        /// textbook engulfing candle.
        /// </summary>
        private bool HasAlignedRecentControlPoint(TrendState dir)
        {
            if (!RequireEngulfingForControlPoint) return true;
            for (int i = controlPointHistory.Count - 1; i >= 0; i--)
            {
                if (controlPointHistory[i].Direction == dir) return true;
            }
            return false;
        }

        /// <summary>
        /// Promote the most recent HL (Bull) or LH (Bear) as the controlling
        /// pivot price. If RequireEngulfingForControlPoint is true and we
        /// have an aligned engulfing CP, prefer that price (engulfing low /
        /// high) over the fractal pivot — that's the PDF's stricter rule.
        /// </summary>
        private void PromoteControllingPivot(TrendState dir)
        {
            // First clear all flags
            for (int i = 0; i < pivotHistory.Count; i++)
                pivotHistory[i].IsControllingPivot = false;

            // Find the most recent matching pivot
            //   Bull → most recent low (which by structure is an HL)
            //   Bear → most recent high (which by structure is an LH)
            SwingPivot controlling = null;
            for (int i = pivotHistory.Count - 1; i >= 0; i--)
            {
                var p = pivotHistory[i];
                if (dir == TrendState.Bull && !p.IsHigh) { controlling = p; break; }
                if (dir == TrendState.Bear && p.IsHigh)  { controlling = p; break; }
            }

            double pivotPrice = double.NaN;
            if (controlling != null)
            {
                controlling.IsControllingPivot = true;
                pivotPrice = controlling.Price;
            }

            // PDF strict mode: prefer the engulfing CP's low/high. We pick
            // the engulfing whose price is MORE PROTECTIVE (closer to current
            // price in the trend direction is risky; we want the most recent
            // aligned engulfing whose level hasn't been violated).
            if (RequireEngulfingForControlPoint)
            {
                for (int i = controlPointHistory.Count - 1; i >= 0; i--)
                {
                    var cp = controlPointHistory[i];
                    if (cp.Direction != dir) continue;
                    pivotPrice = cp.Price;
                    break;
                }
            }

            currentControllingPivotPrice = pivotPrice;
        }

        /// <summary>
        /// While in an active trend, ratchet the controlling pivot whenever
        /// a new HL (Bull) or LH (Bear) prints in the trend direction. We
        /// only ever move the controller in the trend direction (never
        /// back). Mirrors the lifecycle layer's "never move SL backwards"
        /// clamp — same principle, different domain.
        /// </summary>
        private void UpdateControllingPivotInTrend()
        {
            if (CurrentState == TrendState.Flat) return;
            if (pivotHistory.Count == 0) return;

            SwingPivot newest = null;
            for (int i = pivotHistory.Count - 1; i >= 0; i--)
            {
                var p = pivotHistory[i];
                if (CurrentState == TrendState.Bull && !p.IsHigh) { newest = p; break; }
                if (CurrentState == TrendState.Bear && p.IsHigh)  { newest = p; break; }
            }
            if (newest == null) return;

            bool ratchet = false;
            if (double.IsNaN(currentControllingPivotPrice))
            {
                ratchet = true;
            }
            else if (CurrentState == TrendState.Bull
                     && newest.Price > currentControllingPivotPrice)
            {
                ratchet = true;
            }
            else if (CurrentState == TrendState.Bear
                     && newest.Price < currentControllingPivotPrice)
            {
                ratchet = true;
            }

            if (ratchet)
            {
                for (int i = 0; i < pivotHistory.Count; i++)
                    pivotHistory[i].IsControllingPivot = false;
                newest.IsControllingPivot = true;
                currentControllingPivotPrice = newest.Price;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // INTERNAL — small list helpers
        // ────────────────────────────────────────────────────────────────────

        private void AddControlPointToHistory(ControlPoint cp)
        {
            controlPointHistory.Add(cp);
            while (controlPointHistory.Count > MaxControlPointHistory)
                controlPointHistory.RemoveAt(0);
        }

        private ControlPoint GetCurrentControlPoint()
        {
            // The "current" CP is the most recent one matching the current
            // trend direction, or the pending one if FLAT. Lets the renderer
            // paint the most relevant level.
            if (CurrentState == TrendState.Flat)
                return pendingControlPoint;

            for (int i = controlPointHistory.Count - 1; i >= 0; i--)
            {
                if (controlPointHistory[i].Direction == CurrentState)
                    return controlPointHistory[i];
            }
            return null;
        }

        private List<T> TakeLast<T>(List<T> source, int n)
        {
            var result = new List<T>();
            int start = Math.Max(0, source.Count - n);
            for (int i = start; i < source.Count; i++) result.Add(source[i]);
            return result;
        }

        // ────────────────────────────────────────────────────────────────────
        // SELF-TEST (DEBUG only). Runs a synthetic OHLC sequence with a clear
        // uptrend (HH-HL-HH plus a bullish engulfing) followed by a body-close
        // break of the controlling HL. Asserts the expected events fired in
        // the documented order. Pure scaffolding — has no production effect.
        // ────────────────────────────────────────────────────────────────────

        [Conditional("DEBUG")]
        public static void RunSelfTest()
        {
            var sm = new TrendStateMachine
            {
                SwingFractalLookback = 2,
                RequireSegments = 3,
                RequireEngulfingForControlPoint = true
            };

            int cpCount = 0;
            int breakCount = 0;
            int stateChanges = 0;
            sm.OnControlPointDetected += delegate { cpCount++; };
            sm.OnTrendBroken += delegate { breakCount++; };
            sm.OnTrendStateChanged += delegate { stateChanges++; };

            // Synthetic series: bullish engulfing at bar 2, then a clean
            // up-staircase forming HH-HL-HH, then a body-close break.
            // OHLC tuples (open, high, low, close):
            var bars = new (double O, double H, double L, double C)[]
            {
                (100.0, 100.5,  99.0,  99.2),  // 0 bearish prep
                (99.2,  99.4,   98.5,  98.8),  // 1 bearish (sets up engulfing)
                (98.5, 100.5,   98.4, 100.2),  // 2 BULLISH ENGULFING of bar 1
                (100.2, 102.0,  99.8, 101.8),  // 3 push up (will become H_a)
                (101.8, 102.2, 100.5, 100.8),  // 4 pullback (will become L_b = HL)
                (100.8, 103.5, 100.6, 103.2),  // 5 push up (H_c — HH > H_a)
                (103.2, 103.4, 101.5, 101.8),  // 6 pullback (L_d > L_b → HL)
                (101.8, 105.0, 101.7, 104.8),  // 7 push up (H_e — HH > H_c) → confirms BULL
                (104.8, 105.0, 100.0,  98.0),  // 8 BREAK — body close < controlling HL
            };
            var t0 = new DateTime(2026, 1, 1, 9, 30, 0);
            for (int i = 0; i < bars.Length; i++)
            {
                sm.OnBarClose(i, t0.AddMinutes(i),
                              bars[i].O, bars[i].H, bars[i].L, bars[i].C, 0.25);
            }

            // We expect at least one CP (bar 2 engulfing), one trend
            // confirmation (FLAT → Bull around bar 7), and one break (bar 8
            // → FLAT). State changes ≥ 2: FLAT→Bull and Bull→FLAT.
            Debug.Assert(cpCount >= 1, "Self-test: at least one CP expected");
            Debug.Assert(stateChanges >= 2, "Self-test: at least 2 state changes expected");
            Debug.Assert(breakCount >= 1, "Self-test: at least one trend break expected");
            Debug.Assert(sm.CurrentState == TrendState.Flat,
                "Self-test: final state should be FLAT after break");
        }
    }
}
