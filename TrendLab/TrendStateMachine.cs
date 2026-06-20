// TrendStateMachine.cs — Leg-based trend engine per Mr. Black's methodology.
//
// A "leg" = continuous run of same-direction closes until the opposite direction
// breaks it. Three alternating legs = one structure.
// Dual control points: ControllingHigh (HH from bull structures, only rises)
//                      ControllingLow  (LL from bear structures, only falls)
// FLAT when price is between both. No direct Bull→Bear flip.

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

    // ── Leg pivot exposed for chart drawing ───────────────────────────────────
    public class LegPivot
    {
        public DateTime Time;    // time when extreme close was set
        public double   Price;   // extreme close price
        public string   Label;   // "HH", "HL", "LH", "LL"
        public bool     IsBull;  // true = bull leg (HH or LH), false = bear leg (LL or HL)
    }

    public class TrendSnapshot
    {
        public TrendState          State;
        public double              ControllingHigh = double.NaN;
        public double              ControllingLow  = double.NaN;
        public List<LegPivot>      LegPivots       = new List<LegPivot>();
        public ControlPoint        CurrentControlPoint;
        public List<SwingPivot>    RecentPivots        = new List<SwingPivot>();
        public List<ControlPoint>  RecentControlPoints = new List<ControlPoint>();
        public TrendBreakEvent     LastBreak;
        public DateTime            LastUpdated;

        public double ControllingPivotPrice =>
            State == TrendState.Bull ? ControllingLow  :
            State == TrendState.Bear ? ControllingHigh :
            double.NaN;
    }

    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class Leg
    {
        public int      Direction;    // +1 bull, -1 bear
        public double   Extreme;      // highest close (bull) or lowest close (bear)
        public DateTime ExtremeTime;  // bar time when extreme was set
        public double   StartClose;
        public double   EndClose;
        public DateTime StartTime;
    }

    // ─────────────────────────────────────────────────────────────────────────
    public class TrendStateMachine
    {
        // ── Compat props ──────────────────────────────────────────────────────
        public int  SwingFractalLookback            = 3;
        public int  RequireSegments                 = 3;
        public bool RequireEngulfingForControlPoint = false;
        public int  MaxPivotHistory                 = 50;

        // ── Leg size filter ───────────────────────────────────────────────────
        // A leg only seals if its extreme moved at least this many ticks from
        // its start close. Filters out single-candle noise on choppy TFs.
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
        private double   _lastClose  = double.NaN;

        // ── Completed legs ────────────────────────────────────────────────────
        private readonly List<Leg>      _legs     = new List<Leg>();
        private readonly List<LegPivot> _pivots   = new List<LegPivot>();

        // ── Dual control points ───────────────────────────────────────────────
        private double _ctrlHigh = double.NaN;
        private double _ctrlLow  = double.NaN;

        // ─────────────────────────────────────────────────────────────────────
        public void OnBarClose(int barIndex, DateTime time,
                               double open, double high, double low, double close,
                               double tickSize)
        {
            if (double.IsNaN(_lastClose))
            {
                _lastClose      = close;
                _legDir         = close >= open ? 1 : -1;
                _legExtreme     = close;
                _legExtremeTime = time;
                _legStart       = close;
                _legStartTime   = time;
                return;
            }

            TrendState oldState = CurrentState;
            int dir = close > open ? 1 : close < open ? -1 : _legDir;

            // Leg break requires: opposite candle closes beyond prior close AND
            // the leg that's ending moved at least MinLegTicks from its start.
            double minMove = MinLegTicks * tickSize;
            bool legMoved = (_legDir == 1  && _legExtreme >= _legStart + minMove)
                         || (_legDir == -1 && _legExtreme <= _legStart - minMove);

            bool broken = legMoved
                       && ((_legDir ==  1 && dir == -1 && close < _lastClose)
                        || (_legDir == -1 && dir ==  1 && close > _lastClose));

            if (broken)
            {
                var leg = new Leg
                {
                    Direction   = _legDir,
                    Extreme     = _legExtreme,
                    ExtremeTime = _legExtremeTime,
                    StartClose  = _legStart,
                    EndClose    = _lastClose,
                    StartTime   = _legStartTime
                };
                _legs.Add(leg);
                if (_legs.Count > 20) _legs.RemoveAt(0);

                // Label this leg relative to the previous same-direction leg
                AddLegPivot(leg);

                UpdateControlPoints();

                _legDir         = dir;
                _legExtreme     = close;
                _legExtremeTime = time;
                _legStart       = close;
                _legStartTime   = time;
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
            EvaluateState(close);

            if (CurrentState != oldState && OnTrendStateChanged != null)
                OnTrendStateChanged(oldState, CurrentState);
        }

        // ─────────────────────────────────────────────────────────────────────
        private void AddLegPivot(Leg leg)
        {
            // Find previous leg of same direction to determine HH/HL/LH/LL
            Leg prevSame = null;
            for (int i = _legs.Count - 2; i >= 0; i--)
            {
                if (_legs[i].Direction == leg.Direction) { prevSame = _legs[i]; break; }
            }

            string label;
            if (leg.Direction == 1) // bull leg
            {
                if (prevSame == null)            label = "HH";
                else if (leg.Extreme > prevSame.Extreme) label = "HH";
                else                             label = "LH";
            }
            else // bear leg
            {
                if (prevSame == null)            label = "LL";
                else if (leg.Extreme < prevSame.Extreme) label = "LL";
                else                             label = "HL";
            }

            _pivots.Add(new LegPivot
            {
                Time  = leg.ExtremeTime,
                Price = leg.Extreme,
                Label = label,
                IsBull = leg.Direction == 1
            });
            if (_pivots.Count > 60) _pivots.RemoveAt(0);
        }

        // ─────────────────────────────────────────────────────────────────────
        private void UpdateControlPoints()
        {
            int n = _legs.Count;
            if (n < 3) return;

            var a = _legs[n - 3];
            var b = _legs[n - 2];
            var c = _legs[n - 1];

            if (a.Direction == 1 && b.Direction == -1 && c.Direction == 1
                && c.Extreme > a.Extreme)
            {
                if (double.IsNaN(_ctrlHigh) || c.Extreme > _ctrlHigh)
                    _ctrlHigh = c.Extreme;
            }
            else if (a.Direction == -1 && b.Direction == 1 && c.Direction == -1
                     && c.Extreme < a.Extreme)
            {
                if (double.IsNaN(_ctrlLow) || c.Extreme < _ctrlLow)
                    _ctrlLow = c.Extreme;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private void EvaluateState(double close)
        {
            bool hasHigh = !double.IsNaN(_ctrlHigh);
            bool hasLow  = !double.IsNaN(_ctrlLow);

            if      (hasHigh && close > _ctrlHigh) CurrentState = TrendState.Bull;
            else if (hasLow  && close < _ctrlLow)  CurrentState = TrendState.Bear;
            else                                    CurrentState = TrendState.Flat;
        }

        // ─────────────────────────────────────────────────────────────────────
        public TrendSnapshot GetSnapshot()
        {
            var pivotsCopy = new List<LegPivot>(_pivots);
            return new TrendSnapshot
            {
                State           = CurrentState,
                ControllingHigh = _ctrlHigh,
                ControllingLow  = _ctrlLow,
                LegPivots       = pivotsCopy,
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
            _legDir         = 0;
            _legExtreme     = double.NaN;
            _legStart       = double.NaN;
            _lastClose      = double.NaN;
            _ctrlHigh       = double.NaN;
            _ctrlLow        = double.NaN;
            CurrentState    = TrendState.Flat;
        }
    }
}
