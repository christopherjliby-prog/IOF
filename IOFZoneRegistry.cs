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
// Key: "{SymbolName}_{TimeframePeriod}" e.g. "MNQ_5" or "MES_15"
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
        /// Key convention: $"{symbol.Name}_{period}" e.g. "MNQ_5"
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
}
