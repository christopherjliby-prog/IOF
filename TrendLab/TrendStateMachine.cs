// TrendStateMachine.cs — Leg-based trend engine per Mr. Black's methodology.
//
// Control point = the FIRST reversal candle when a leg changes direction.
// That candle's full OHLC is stored as a rectangle (CPBox), extended right.
// CPBox is invalidated when a BODY CLOSE breaks through its range.
//
// Three alternating legs AFTER a control point confirm the trend:
//   Bull: bull-leg(HH) → bear-leg(HL) → bull-leg(HH)  after a bullish CP
//   Bear: bear-leg(LL) → bull-leg(LH) → bear-leg(LL)  after a bearish CP
//
// FLAT: price between an active bull CP and an active bear CP, neither broken.
// No direct Bull→Bear flip — must pass through FLAT.

using System;
using System.Collections.Generic;

namespace TradePhantomsIOF.Trend
{
    public enum TrendState { Flat = 0, Bull = 1, Bear = 2 }

    // ── Backward-compat stubs ─────────────────────────────────────────────────
    public enum CandleDirection { Bullish = 1, Bearish = -1, Doji = 0 }
    public class ControlPoint  { public double Price; public TrendState Direction; public DateTime Time; public int BarIndex; public double EngulfingHigh; public double EngulfingLow; public double EngulfingOpen; public double EngulfingClose; }
    public class TrendBreakEvent { public DateTime Time; public int BarIndex; public TrendState OldTrend; public TrendState NewTrend; public double BrokenControlPoint; public double BreakBarClose; }
    public class SwingPivot { public DateTime Time; public int BarIndex; public double Price; public bool IsHigh; public bool IsControllingPivot; }

    // ── Institutional Order Flow box (first reversal candle) ──────────────────
    public class CPBox
    {
        public double   Open;
        public double   High;
        public double   Low;
        public double   Close;
        public DateTime Time;
        public int      BarIndex;
        public bool     IsBull;          // true = bullish CP (bottom reversal)
        public bool     IsConfirmed;     // true = 3 segments completed after this CP
        public bool     IsBroken;
        public DateTime BrokenTime;
        public int      BrokenBarIndex;
    }

    // ── Leg pivot label exposed for chart drawing ─────────────────────────────
    public class LegPivot
    {
        public DateTime Time;
        public double   Price;
        public string   Label;   // "HH", "HL", "LH", "LL"
        public bool     IsBull;
    }

    public class TrendSnapshot
    {
        public TrendState          State;
        public double              ControllingHigh = double.NaN;  // bearish CP box high (draw zone here)
        public double              ControllingLow  = double.NaN;  // bullish CP box low  (draw zone here)
        public List<LegPivot>      LegPivots       = new List<LegPivot>();
        public ControlPoint        CurrentControlPoint;
        public List<SwingPivot>    RecentPivots        = new List<SwingPivot>();
        public List<ControlPoint>  RecentControlPoints = new List<ControlPoint>();
        public TrendBreakEvent     LastBreak;
        public DateTime            LastUpdated;

        // Active CP boxes for rectangle drawing
        public CPBox               ActiveBullCP;
        public CPBox               ActiveBearCP;
        // All historical boxes (including broken) for drawing history
        public List<CPBox>         AllCPBoxes = new List<CPBox>();

        public double ControllingPivotPrice =>
            State == TrendState.Bull ? ControllingLow  :
            State == TrendState.Bear ? ControllingHigh :
            double.NaN;
    }

    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class Leg
    {
        public int      Direction;
        public double   Extreme;
        public DateTime ExtremeTime;
        public double   StartClose;
        public double   EndClose;
        public DateTime StartTime;
        public int      StartBarIndex;
    }

    // ─────────────────────────────────────────────────────────────────────────
    public class TrendStateMachine
    {
        // ── Compat props ──────────────────────────────────────────────────────
        public int  SwingFractalLookback            = 3;
        public int  RequireSegments                 = 3;
        public bool RequireEngulfingForControlPoint = false;
        public int  MaxPivotHistory                 = 50;

        // A leg only seals if its extreme moved at least this many ticks from
        // its start close. Filters single-candle noise on higher timeframes.
        public int MinLegTicks = 4;

        public TrendState CurrentState { get; private set; } = TrendState.Flat;

        public event Action<ControlPoint>           OnControlPointDetected;
        public event Action<TrendBreakEvent>        OnTrendBroken;
        public event Action<TrendState, TrendState> OnTrendStateChanged;

        // ── Active leg ────────────────────────────────────────────────────────
        private int      _legDir     = 0;
        private double   _legExtreme = double.NaN;
        private DateTime _legExtremeTime;
        private double   _legStart   = double.NaN;
        private DateTime _legStartTime;
        private int      _legStartBarIndex;
        private double   _lastClose  = double.NaN;

        // ── Completed legs ────────────────────────────────────────────────────
        private readonly List<Leg>      _legs   = new List<Leg>();
        private readonly List<LegPivot> _pivots = new List<LegPivot>();

        // ── Active control point boxes ────────────────────────────────────────
        private CPBox _activeBullCP = null;
        private CPBox _activeBearCP = null;

        // Historical CP boxes (for drawing)
        private readonly List<CPBox> _allBoxes = new List<CPBox>();

        // ─────────────────────────────────────────────────────────────────────
        public void OnBarClose(int barIndex, DateTime time,
                               double open, double high, double low, double close,
                               double tickSize)
        {
            if (double.IsNaN(_lastClose))
            {
                _lastClose         = close;
                _legDir            = close >= open ? 1 : -1;
                _legExtreme        = close;
                _legExtremeTime    = time;
                _legStart          = close;
                _legStartTime      = time;
                _legStartBarIndex  = barIndex;
                return;
            }

            TrendState oldState = CurrentState;
            int dir = close > open ? 1 : close < open ? -1 : _legDir;

            double minMove = MinLegTicks * tickSize;
            bool legMoved = (_legDir ==  1 && _legExtreme >= _legStart + minMove)
                         || (_legDir == -1 && _legExtreme <= _legStart - minMove);

            bool broken = legMoved
                       && ((_legDir ==  1 && dir == -1 && close < _lastClose)
                        || (_legDir == -1 && dir ==  1 && close > _lastClose));

            if (broken)
            {
                // Seal the completed leg
                var leg = new Leg
                {
                    Direction     = _legDir,
                    Extreme       = _legExtreme,
                    ExtremeTime   = _legExtremeTime,
                    StartClose    = _legStart,
                    EndClose      = _lastClose,
                    StartTime     = _legStartTime,
                    StartBarIndex = _legStartBarIndex
                };
                _legs.Add(leg);
                if (_legs.Count > 40) _legs.RemoveAt(0);

                AddLegPivot(leg);

                // Current candle is the FIRST candle of the new leg = the control point
                var newCP = new CPBox
                {
                    Open     = open,
                    High     = high,
                    Low      = low,
                    Close    = close,
                    Time     = time,
                    BarIndex = barIndex,
                    IsBull   = dir == 1
                };

                RegisterControlPoint(newCP);

                // Start new leg
                _legDir           = dir;
                _legExtreme       = close;
                _legExtremeTime   = time;
                _legStart         = close;
                _legStartTime     = time;
                _legStartBarIndex = barIndex;
            }
            else
            {
                bool newExtreme = (_legDir == 1 && close > _legExtreme)
                               || (_legDir == -1 && close < _legExtreme);
                if (newExtreme)
                {
                    _legExtreme     = close;
                    _legExtremeTime = time;
                }
            }

            _lastClose = close;

            // Check if current close breaks any active CP box
            CheckCPBreaks(close, barIndex, time);

            // Count legs completed after each CP toward confirmation
            UpdateSegmentCounts();

            EvaluateState(close);

            if (CurrentState != oldState && OnTrendStateChanged != null)
                OnTrendStateChanged(oldState, CurrentState);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Register a new CP box. New CP of same direction replaces the old one.
        private void RegisterControlPoint(CPBox cp)
        {
            _allBoxes.Add(cp);
            if (_allBoxes.Count > 80) _allBoxes.RemoveAt(0);

            if (cp.IsBull)
            {
                // New bullish reversal candle replaces old bull CP
                _activeBullCP = cp;
            }
            else
            {
                // New bearish reversal candle replaces old bear CP
                _activeBearCP = cp;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Body close above bearish CP High → bear CP broken (institution absorbed).
        // Body close below bullish CP Low  → bull CP broken (institution absorbed).
        private void CheckCPBreaks(double close, int barIndex, DateTime time)
        {
            if (_activeBearCP != null && close > _activeBearCP.High)
            {
                _activeBearCP.IsBroken       = true;
                _activeBearCP.BrokenTime     = time;
                _activeBearCP.BrokenBarIndex = barIndex;
                _activeBearCP = null;
            }

            if (_activeBullCP != null && close < _activeBullCP.Low)
            {
                _activeBullCP.IsBroken       = true;
                _activeBullCP.BrokenTime     = time;
                _activeBullCP.BrokenBarIndex = barIndex;
                _activeBullCP = null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Count how many legs have completed since each active CP was set.
        // Confirmation requires 3 completed legs after the CP.
        private void UpdateSegmentCounts()
        {
            if (_activeBullCP != null)
            {
                int segs = 0;
                for (int i = _legs.Count - 1; i >= 0; i--)
                {
                    if (_legs[i].StartBarIndex >= _activeBullCP.BarIndex)
                        segs++;
                    else
                        break;
                }
                _activeBullCP.IsConfirmed = segs >= 3;
            }

            if (_activeBearCP != null)
            {
                int segs = 0;
                for (int i = _legs.Count - 1; i >= 0; i--)
                {
                    if (_legs[i].StartBarIndex >= _activeBearCP.BarIndex)
                        segs++;
                    else
                        break;
                }
                _activeBearCP.IsConfirmed = segs >= 3;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private void EvaluateState(double close)
        {
            bool bullConfirmed = _activeBullCP != null && _activeBullCP.IsConfirmed;
            bool bearConfirmed = _activeBearCP != null && _activeBearCP.IsConfirmed;

            if (bullConfirmed && !bearConfirmed)
                CurrentState = TrendState.Bull;
            else if (bearConfirmed && !bullConfirmed)
                CurrentState = TrendState.Bear;
            else if (bullConfirmed && bearConfirmed)
                CurrentState = TrendState.Flat; // price between both active CPs
            else
                CurrentState = TrendState.Flat;
        }

        // ─────────────────────────────────────────────────────────────────────
        private void AddLegPivot(Leg leg)
        {
            Leg prevSame = null;
            for (int i = _legs.Count - 2; i >= 0; i--)
            {
                if (_legs[i].Direction == leg.Direction) { prevSame = _legs[i]; break; }
            }

            string label;
            if (leg.Direction == 1)
            {
                label = (prevSame == null || leg.Extreme > prevSame.Extreme) ? "HH" : "LH";
            }
            else
            {
                label = (prevSame == null || leg.Extreme < prevSame.Extreme) ? "LL" : "HL";
            }

            _pivots.Add(new LegPivot
            {
                Time   = leg.ExtremeTime,
                Price  = leg.Extreme,
                Label  = label,
                IsBull = leg.Direction == 1
            });
            if (_pivots.Count > 60) _pivots.RemoveAt(0);
        }

        // ─────────────────────────────────────────────────────────────────────
        public TrendSnapshot GetSnapshot()
        {
            double ctrlHigh = _activeBearCP != null ? _activeBearCP.High : double.NaN;
            double ctrlLow  = _activeBullCP != null ? _activeBullCP.Low  : double.NaN;

            return new TrendSnapshot
            {
                State           = CurrentState,
                ControllingHigh = ctrlHigh,
                ControllingLow  = ctrlLow,
                LegPivots       = new List<LegPivot>(_pivots),
                ActiveBullCP    = _activeBullCP,
                ActiveBearCP    = _activeBearCP,
                AllCPBoxes      = new List<CPBox>(_allBoxes),
                LastUpdated     = DateTime.UtcNow
            };
        }

        public bool IsAlignedWithTrend(bool isLongZone)
        {
            if (CurrentState == TrendState.Flat) return false;
            return isLongZone ? CurrentState == TrendState.Bull : CurrentState == TrendState.Bear;
        }

        public void Reset()
        {
            _legs.Clear();
            _pivots.Clear();
            _allBoxes.Clear();
            _legDir           = 0;
            _legExtreme       = double.NaN;
            _legStart         = double.NaN;
            _lastClose        = double.NaN;
            _activeBullCP     = null;
            _activeBearCP     = null;
            CurrentState      = TrendState.Flat;
        }
    }
}
