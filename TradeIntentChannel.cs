// =============================================================================
// TradeIntentChannel.cs — Inter-component bus between IOF indicator + AutoSLTP
// =============================================================================
// The IOF v2 indicator computes per-zone entry/SL/TP/contract math when a zone
// ARMS. The AutoSLTP strategy normally uses fixed tick distances. This channel
// lets the indicator publish its zone-derived plan, and lets the strategy
// read it when a position opens — so brackets land at the correct zone-based
// SL/TP rather than blanket fixed-tick defaults.
//
// Lookup is by symbol name + side + entry-price-within-tolerance, so the
// indicator can publish a plan that the strategy can reliably match when the
// user clicks in at the indicator's plotted entry.
//
// Threading: the channel uses a lock to serialize publishes and reads. Pub
// from the indicator's chart thread (OnUpdate / handlers); reads from the
// strategy's PositionAdded thread.
// =============================================================================

using System;
using System.Collections.Generic;

namespace TradePhantomsIOF.IntentBus
{
    /// <summary>
    /// One published intent — the zone-derived trade plan the indicator wants
    /// the strategy to honor when a matching position opens.
    /// </summary>
    public class TradeIntent
    {
        public DateTime PublishedAt;     // when the indicator emitted this
        public string   SymbolName;      // matched against position.Symbol.Name
        public string   AccountId;       // matched against position.Account?.Id (optional — empty = any)
        public bool     IsLong;          // direction
        public double   Entry;           // exact zone-derived entry price
        public double   SL;              // far wick + buffer
        public double   TP1, TP2, TP3;   // 1x/2x/3x zone-height multiples
        public int      Contracts;       // dollar-risk / slDist / pointValue, capped at MaxContracts
        public double   DollarRisk;      // for verification
        public double   ZoneHeight;      // for diagnostics
        public string   ZoneId;          // master indicator's zone id
        public int      Score;           // zone score (so strategy can decide whether to enforce)
        public string   FormationCode;   // "RBR" / "DBR" / "RBD" / "DBD"
        public string   Source;          // "ARMED" / "ACTIVE_MARKET" / etc.

        public bool MatchesFill(string symbolName, string accountId, bool fillIsLong, double fillPrice, double tickSize)
        {
            if (string.IsNullOrEmpty(symbolName)) return false;
            if (!string.Equals(this.SymbolName, symbolName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(this.AccountId) && !string.IsNullOrEmpty(accountId) &&
                !string.Equals(this.AccountId, accountId, StringComparison.Ordinal))
                return false;
            if (this.IsLong != fillIsLong) return false;

            // Tolerance: 3 ticks (slippage on market orders, limit-fill drift).
            double tol = Math.Max(tickSize, 0.0001) * 3.0;
            return Math.Abs(this.Entry - fillPrice) <= tol;
        }
    }

    /// <summary>
    /// Publish/subscribe channel. Static so the strategy can read what the
    /// indicator wrote without holding a reference to the indicator instance.
    /// </summary>
    public static class TradeIntentChannel
    {
        private static readonly object gate = new object();
        private static readonly List<TradeIntent> queue = new List<TradeIntent>();
        private const int MaxQueueDepth = 32;          // FIFO cap
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Indicator calls this when a zone ARMs (or refreshes its plan).
        /// Replaces any earlier intent for the same ZoneId.
        /// </summary>
        public static void Publish(TradeIntent intent)
        {
            if (intent == null) return;
            intent.PublishedAt = DateTime.UtcNow;
            lock (gate)
            {
                // Replace if same ZoneId already queued (only the latest plan
                // for that zone matters).
                if (!string.IsNullOrEmpty(intent.ZoneId))
                {
                    for (int i = queue.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(queue[i].ZoneId, intent.ZoneId, StringComparison.Ordinal))
                        {
                            queue.RemoveAt(i);
                        }
                    }
                }
                queue.Add(intent);
                // FIFO cap.
                while (queue.Count > MaxQueueDepth) queue.RemoveAt(0);
            }
        }

        /// <summary>
        /// Strategy calls this on PositionAdded. Returns the most recent intent
        /// matching the fill (or null if no match). Matching intent is REMOVED
        /// from the queue so a single intent honors a single fill.
        /// </summary>
        public static TradeIntent ConsumeMatching(
            string symbolName, string accountId, bool fillIsLong, double fillPrice, double tickSize)
        {
            lock (gate)
            {
                PruneStaleLocked();
                // Newest first — the latest plan wins if multiple zones map to the same price.
                for (int i = queue.Count - 1; i >= 0; i--)
                {
                    var x = queue[i];
                    if (x.MatchesFill(symbolName, accountId, fillIsLong, fillPrice, tickSize))
                    {
                        queue.RemoveAt(i);
                        return x;
                    }
                }
                return null;
            }
        }

        /// <summary>
        /// Strategy can also peek without consuming (diagnostics).
        /// </summary>
        public static List<TradeIntent> Snapshot()
        {
            lock (gate)
            {
                PruneStaleLocked();
                return new List<TradeIntent>(queue);
            }
        }

        /// <summary>
        /// Indicator/strategy call on shutdown to avoid leaking state across
        /// indicator reloads (Quantower keeps the AppDomain alive).
        /// </summary>
        public static void Clear()
        {
            lock (gate)
            {
                queue.Clear();
            }
        }

        private static void PruneStaleLocked()
        {
            var cutoff = DateTime.UtcNow - StaleAfter;
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                if (queue[i].PublishedAt < cutoff) queue.RemoveAt(i);
            }
        }
    }
}
