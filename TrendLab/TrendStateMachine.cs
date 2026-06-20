// TrendStateMachine.cs — Leg-based trend engine per Mr. Black's methodology.
//
// A "leg" is a continuous run of same-direction candle closes until the
// opposite direction breaks it (bear candle close < prior close ends a bull
// leg; bull candle close > prior close ends a bear leg). Doji continues.
//
// Three alternating legs = one complete structure:
//   Bull: bull-leg → bear-leg → bull-leg  where leg3.High > leg1.High (HH)
//   Bear: bear-leg → bull-leg → bear-leg  where leg3.Low  < leg1.Low  (LL)
//
// Dual control points (both active simultaneously):
//   ControllingHigh = HH of the most recent completed bull structure (only rises)
//   ControllingLow  = LL of the most recent completed bear structure (only falls)
//
// State:
//   BULL  — close > ControllingHigh
//   BEAR  — close < ControllingLow
//   FLAT  — between both (or before any structure forms)
//   No direct Bull→Bear flip. Must pass through FLAT.

using System;
using System.Collections.Generic;

namespace TradePhantomsIOF.Trend
{
    public enum TrendState { Flat = 0, Bull = 1, Bear = 2 }

    // ── Kept for backward compatibility with IOF_TrendLab.cs ─────────────────
    public enum CandleDirection { Bullish = 1, Bearish = -1, Doji = 0 }
    public class ControlPoint  { public double Price; public TrendState Direction; public DateTime Time; public int BarIndex; public double EngulfingHigh; public double EngulfingLow; public double EngulfingOpen; public double EngulfingClose; }
    public class TrendBreakEvent { public DateTime Time; public int BarIndex; public TrendState OldTrend; public TrendState NewTrend; public double BrokenControlPoint; public double BreakBarClose; }
    public class SwingPivot     { public DateTime Time; public int BarIndex; public double Price; public bool IsHigh; public bool IsControllingPivot; }

    public class TrendSnapshot
    {
        public TrendState   State;
        public double       ControllingHigh  = double.NaN;
        public double       ControllingLow   = double.NaN;
        public ControlPoint CurrentControlPoint;
        public List<SwingPivot>    RecentPivots        = new List<SwingPivot>();
        public List<ControlPoint>  RecentControlPoints = new List<ControlPoint>();
        public TrendBreakEvent     LastBreak;
        public DateTime     LastUpdated;

        // Panel uses this for the price display in the state box
        public double ControllingPivotPrice =>
            State == TrendState.Bull ? ControllingLow  :
            State == TrendState.Bear ? ControllingHigh :
            double.NaN;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // INTERNAL: sealed directional leg
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed class Leg
    {
        public int    Direction;  // +1 bull, -1 bear
        public double Extreme;    // highest close (bull) or lowest close (bear) during this leg
        public double StartClose;
        public double EndClose;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TREND STATE MACHINE
    // ─────────────────────────────────────────────────────────────────────────

    public class TrendStateMachine
    {
        // ── Compatibility properties (ignored in leg engine) ──────────────────
        public int  SwingFractalLookback            = 3;
        public int  RequireSegments                 = 3;
        public bool RequireEngulfingForControlPoint = false;
        public int  MaxPivotHistory                 = 50;

        // ── Public state ──────────────────────────────────────────────────────
        public TrendState CurrentState { get; private set; } = TrendState.Flat;

        // ── Events (compatibility) ────────────────────────────────────────────
        public event Action<ControlPoint>             OnControlPointDetected;
        public event Action<TrendBreakEvent>          OnTrendBroken;
        public event Action<TrendState, TrendState>   OnTrendStateChanged;

        // ── Active leg ────────────────────────────────────────────────────────
        private int    _legDir     = 0;
        private double _legExtreme = double.NaN;
        private double _legStart   = double.NaN;
        private double _lastClose  = double.NaN;

        // ── Completed legs ────────────────────────────────────────────────────
        private readonly List<Leg> _legs = new List<Leg>();

        // ── Dual control points ───────────────────────────────────────────────
        private double _ctrlHigh = double.NaN;  // HH from bull structures (only rises)
        private double _ctrlLow  = double.NaN;  // LL from bear structures (only falls)

        // ─────────────────────────────────────────────────────────────────────
        // PER-BAR ENTRY POINT
        // ─────────────────────────────────────────────────────────────────────

        public void OnBarClose(int barIndex, DateTime time,
                               double open, double high, double low, double close,
                               double tickSize)
        {
            // Initialize on first bar
            if (double.IsNaN(_lastClose))
            {
                _lastClose  = close;
                _legDir     = close >= open ? 1 : -1;
                _legExtreme = close;
                _legStart   = close;
                return;
            }

            TrendState oldState = CurrentState;

            // Candle direction — doji continues current leg
            int dir = close > open ? 1 : close < open ? -1 : _legDir;

            // Leg break: opposite candle that closes beyond the prior close
            bool broken = (_legDir ==  1 && dir == -1 && close < _lastClose)
                       || (_legDir == -1 && dir ==  1 && close > _lastClose);

            if (broken)
            {
                // Seal completed leg
                _legs.Add(new Leg
                {
                    Direction  = _legDir,
                    Extreme    = _legExtreme,
                    StartClose = _legStart,
                    EndClose   = _lastClose
                });
                if (_legs.Count > 20) _legs.RemoveAt(0);

                // Check if the last 3 sealed legs form a valid structure
                UpdateControlPoints();

                // Start new leg
                _legDir     = dir;
                _legExtreme = close;
                _legStart   = close;
            }
            else
            {
                // Extend current leg extreme (tracks closes, not wicks)
                _legExtreme = _legDir == 1
                    ? Math.Max(_legExtreme, close)
                    : Math.Min(_legExtreme, close);
            }

            _lastClose = close;

            // Evaluate state against dual control points
            EvaluateState(close);

            // Fire state change event if needed
            if (CurrentState != oldState && OnTrendStateChanged != null)
                OnTrendStateChanged(oldState, CurrentState);
        }

        // ─────────────────────────────────────────────────────────────────────
        // STRUCTURE DETECTION
        // ─────────────────────────────────────────────────────────────────────

        private void UpdateControlPoints()
        {
            int n = _legs.Count;
            if (n < 3) return;

            var a = _legs[n - 3];
            var b = _legs[n - 2];
            var c = _legs[n - 1];

            // Bull structure: bull → bear → bull, C makes HH above A
            if (a.Direction == 1 && b.Direction == -1 && c.Direction == 1
                && c.Extreme > a.Extreme)
            {
                // Controlling high ratchets up with each new HH
                if (double.IsNaN(_ctrlHigh) || c.Extreme > _ctrlHigh)
                    _ctrlHigh = c.Extreme;
            }
            // Bear structure: bear → bull → bear, C makes LL below A
            else if (a.Direction == -1 && b.Direction == 1 && c.Direction == -1
                     && c.Extreme < a.Extreme)
            {
                // Controlling low ratchets down with each new LL
                if (double.IsNaN(_ctrlLow) || c.Extreme < _ctrlLow)
                    _ctrlLow = c.Extreme;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // STATE EVALUATION
        // ─────────────────────────────────────────────────────────────────────

        private void EvaluateState(double close)
        {
            bool hasHigh = !double.IsNaN(_ctrlHigh);
            bool hasLow  = !double.IsNaN(_ctrlLow);

            if (hasHigh && close > _ctrlHigh)
                CurrentState = TrendState.Bull;
            else if (hasLow && close < _ctrlLow)
                CurrentState = TrendState.Bear;
            else
                CurrentState = TrendState.Flat;
        }

        // ─────────────────────────────────────────────────────────────────────
        // PUBLIC INTERFACE
        // ─────────────────────────────────────────────────────────────────────

        public TrendSnapshot GetSnapshot() => new TrendSnapshot
        {
            State           = CurrentState,
            ControllingHigh = _ctrlHigh,
            ControllingLow  = _ctrlLow,
            LastUpdated     = DateTime.UtcNow
        };

        public bool IsAlignedWithTrend(bool isLongZone)
        {
            if (CurrentState == TrendState.Flat) return false;
            return isLongZone ? CurrentState == TrendState.Bull : CurrentState == TrendState.Bear;
        }

        public void Reset()
        {
            _legs.Clear();
            _legDir     = 0;
            _legExtreme = double.NaN;
            _legStart   = double.NaN;
            _lastClose  = double.NaN;
            _ctrlHigh   = double.NaN;
            _ctrlLow    = double.NaN;
            CurrentState = TrendState.Flat;
        }
    }
}
