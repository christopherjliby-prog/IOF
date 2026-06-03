// =============================================================================
// IOFZoneRegistry.cs — Shared static zone list for cross-indicator access
// =============================================================================
// Platform : Quantower C# SDK (v1.143.x)
// Drop path: C:\Quantower\Settings\Scripts\Indicators\IOF
//
// Allows VolumeSpike_IOF (and any future consumer indicator) to query the live
// IOF zone list populated by TradePhantoms_IOF_v2 without re-scanning bars.
//
// Architecture (Option A — confirmed by Brandon's Claude 2026-05-17):
//   PRODUCER : TradePhantoms_IOF_v2 calls Update() at the end of each zone
//              scan pass (after ScanZones / ApplyMTFCBonus complete).
//   CONSUMER : VolumeSpike_IOF calls IsNearZone() at render time.
//   THREAD   : Both run on the Quantower indicator thread — no locking needed.
//              If that assumption changes, wrap _zones in a ReaderWriterLockSlim.
//
// Key: "{SymbolName}_{Aggregation.ToString()}" e.g. "MNQ_5" or "MES_15"
//
// -----------------------------------------------------------------------------
// PATCH NOTES
// -----------------------------------------------------------------------------
// 2026-05-17: Initial build.
//   - Static registry keyed by symbol+timeframe string
//   - Update() replaces the full zone list for a given key each scan pass
//   - IsNearZone() checks price proximity within tolerance (ticks * tickSize)
//   - GetZones() for consumers that want the full snapshot (e.g. future ML feed)
//   - ZoneSnapshot record: Top, Bottom, Type, Score, IsTradeable, TouchCount
//
// 2026-06-03: Phase 2 — ZoneMetricsRegistry added.
//   - ZoneMetricsExport: DepartureMultiplier, AbsorptionMultiplier, MtfcBonus,
//     HvnConfluence — the four confluence factors used for A-F entry grading.
//   - ZoneMetricsRegistry: keyed by "{regKey}|{top:F4}|{bottom:F4}". Populated
//     lazily from DrawZones() when departure/absorption filters are applied.
//   - IOFZoneRegistry.Update() now called from ScanZones() in IOF v2 after
//     every scan pass (previously it was never called — bug fixed).
//   - IOFZoneRegistry.Clear() now called from OnClear() in IOF v2 so stale
//     zones don't persist after a symbol/timeframe switch.
// =============================================================================

using System;
using System.Collections.Generic;

namespace TradePhantoms
{
    public static class IOFZoneRegistry
    {
        private static readonly Dictionary<string, List<ZoneSnapshot>> _zones
            = new(StringComparer.OrdinalIgnoreCase);

        // ── Producer API — called by TradePhantoms_IOF_v2 ────────────────────────

        /// <summary>
        /// Replace the zone list for this symbol+timeframe key.
        /// Call at the end of each ScanZones pass.
        /// Key convention: $"{symbol.Name}_{aggregation}" e.g. "MNQ_5"
        /// </summary>
        public static void Update(string key, List<ZoneSnapshot> zones)
        {
            if (string.IsNullOrEmpty(key)) return;
            _zones[key] = zones ?? new List<ZoneSnapshot>();
        }

        // ── Consumer API — called by VolumeSpike_IOF and future indicators ───────

        /// <summary>
        /// Returns true if any zone for this key has its top/bottom range within
        /// <paramref name="tolerance"/> of <paramref name="price"/>.
        /// tolerance = IOFZoneProximityTicks * Symbol.TickSize
        /// </summary>
        public static bool IsNearZone(string key, double price, double tolerance)
        {
            if (!_zones.TryGetValue(key, out var list) || list == null) return false;

            foreach (var z in list)
            {
                // Expand zone bounds by tolerance on each side
                if (price >= z.Bottom - tolerance && price <= z.Top + tolerance)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns all zones for a key. Empty list if key not registered yet.
        /// </summary>
        public static IReadOnlyList<ZoneSnapshot> GetZones(string key)
        {
            if (_zones.TryGetValue(key, out var list) && list != null)
                return list.AsReadOnly();
            return Array.Empty<ZoneSnapshot>();
        }

        /// <summary>
        /// Clears the registry for a specific key (call from OnClear/Dispose).
        /// </summary>
        public static void Clear(string key)
        {
            if (!string.IsNullOrEmpty(key))
                _zones.Remove(key);
        }
    }

    /// <summary>
    /// Lightweight read-only snapshot of an IOF zone passed to the registry.
    /// Populated from IofZone fields inside TradePhantoms_IOF_v2's scan pass.
    /// </summary>
    public record ZoneSnapshot(
        double   Top,
        double   Bottom,
        ZoneType Type,
        double   Score,
        bool     IsTradeable,
        int      TouchCount
    );

    // ZoneType mirrors the enum in TradePhantoms_IOF_v2 — keep in sync.
    // RBR = Rally-Base-Rally (demand), DBD = Drop-Base-Drop (supply),
    // DBR = Drop-Base-Rally (demand), RBD = Rally-Base-Drop (supply)
    public enum ZoneType { RBR, DBR, DBD, RBD }

    // =========================================================================
    // ZoneMetricsRegistry — Phase 2 confluence metrics for A-F entry grading
    // =========================================================================
    // Keyed by "{regKey}|{top:F4}|{bottom:F4}" where regKey matches the
    // IOFZoneRegistry key for the same chart. Populated lazily from
    // DrawZones() in IOF v2 when departure/absorption filters run.
    //
    // The IOF_TradeJournal indicator reads this via reflection at position-open
    // time to populate DepartureMultiplier, AbsorptionMultiplier, MtfcBonus,
    // and HvnConfluence on each JournalEntry for full A-F grading.
    // =========================================================================

    public static class ZoneMetricsRegistry
    {
        private static readonly Dictionary<string, ZoneMetricsExport> _metrics
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Store or update metrics for a zone identified by its top/bottom price levels.
        /// Called from IOF v2 DrawZones() after ComputeZoneMetrics() runs.
        /// </summary>
        public static void Update(string regKey, double top, double bottom, ZoneMetricsExport metrics)
        {
            if (string.IsNullOrEmpty(regKey)) return;
            string key = MakeKey(regKey, top, bottom);
            _metrics[key] = metrics;
        }

        /// <summary>
        /// Try to retrieve metrics for a zone. Returns false if not yet computed
        /// (ComputeZoneMetrics is lazy — runs on first DrawZones pass for each zone).
        /// </summary>
        public static bool TryGet(string regKey, double top, double bottom, out ZoneMetricsExport metrics)
        {
            string key = MakeKey(regKey, top, bottom);
            return _metrics.TryGetValue(key, out metrics);
        }

        /// <summary>Overload used by journal reflection path (passes zoneKey as pre-formatted string).</summary>
        public static bool TryGet(string regKey, string zoneKey, out ZoneMetricsExport metrics)
        {
            string key = string.IsNullOrEmpty(regKey)
                ? zoneKey
                : $"{regKey}|{zoneKey}";
            return _metrics.TryGetValue(key, out metrics);
        }

        /// <summary>
        /// Clears all metrics for a given registry key (call from IOF v2 OnClear).
        /// </summary>
        public static void Clear(string regKey)
        {
            if (string.IsNullOrEmpty(regKey)) return;
            var toRemove = new List<string>();
            foreach (var kv in _metrics)
            {
                if (kv.Key.StartsWith(regKey + "|", StringComparison.OrdinalIgnoreCase))
                    toRemove.Add(kv.Key);
            }
            foreach (var k in toRemove)
                _metrics.Remove(k);
        }

        private static string MakeKey(string regKey, double top, double bottom)
            => $"{regKey}|{top:F4}|{bottom:F4}";
    }

    /// <summary>
    /// The four confluence factors that drive A-F entry grade computation.
    /// Exported from IOF v2's internal ZoneMetrics struct for cross-indicator access.
    /// </summary>
    public struct ZoneMetricsExport
    {
        public double DepartureMultiplier;   // impulse vol ÷ avg vol (≥2× = bullish)
        public double AbsorptionMultiplier;  // base vol/range ÷ avg  (≥2× = bullish)
        public double MtfcBonus;             // > 0 when zone overlaps higher-TF zone
        public bool   HvnConfluence;         // true when an HVN sits inside the zone
    }
}
