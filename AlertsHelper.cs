// ════════════════════════════════════════════════════════════════════════════
// AlertsHelper.cs — Alert dispatch for the IOF v2 indicator
// ════════════════════════════════════════════════════════════════════════════
// 2026-05-06: Added trend events (ControlPointDetected, TrendBroken, TrendChanged)
// to support TrendStateMachine integration. Per TP + PDF trend doctrine.
// ════════════════════════════════════════════════════════════════════════════
//
// Closes the "no alerts wired up yet" open question from
// IOF_QuickEntry_Project_Audit.md §12 item 6. Provides a static helper class
// that fans out a single Fire(...) call to multiple channels:
//
//   1. In-platform     (Quantower Core.Instance.Alerts.AddAlert via reflection)
//   2. Audible         (System.Console.Beep at event-specific frequencies)
//   3. Webhook         (HTTP POST to Discord/Slack/generic JSON, fire-and-forget)
//   4. CSV log file    (append row per alert to a configurable path)
//
// Each channel is wrapped in try/catch so a failing webhook (network down,
// 404 URL, etc.) never propagates back into the indicator's per-bar loop.
// Cooldown dedup is implemented as a static dictionary keyed by
// `eventType + tradeId + symbol`, default 1s window.
//
// The reflection pattern for AddAlert mirrors what TradePhantoms_IOF.cs
// already does (around line 1100): probe Core.Instance for an `Alerts`
// property, look for an `AddAlert(string, string)` method, invoke. This
// keeps us SDK-version-agnostic.
//
// Self-contained: depends only on System.* and the Quantower SDK exposed
// through the rest of the v2 project (only the in-platform channel uses
// reflection against Core.Instance — failure there silently degrades).
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace TradePhantomsIOF.Alerts
{
    // ────────────────────────────────────────────────────────────────────────
    // EVENT MASK — bit-flags so callers can opt in/out per event type.
    // ────────────────────────────────────────────────────────────────────────

    [Flags]
    public enum AlertEventMask
    {
        None            = 0,
        ZoneDetected    = 1 << 0,
        ZoneInvalidated = 1 << 1,
        Armed           = 1 << 2,
        Filled          = 1 << 3,
        TpHit           = 1 << 4,
        SLHit           = 1 << 5,
        Trailed         = 1 << 6,
        BreakEven       = 1 << 7,
        Closed          = 1 << 8,
        ControlPointDetected = 1 << 9,
        TrendBroken          = 1 << 10,
        TrendChanged         = 1 << 11,
        All             = 0xFFFF
    }

    // ────────────────────────────────────────────────────────────────────────
    // CONFIG — passed by value into Fire(...). User-tweakable knobs live here.
    // ────────────────────────────────────────────────────────────────────────

    public class AlertsConfig
    {
        /// <summary>Show the alert in Quantower's Alerts panel.</summary>
        public bool EnableInPlatform = true;

        /// <summary>Beep through the PC speaker / sound device.</summary>
        public bool EnableAudible = true;

        /// <summary>POST a JSON payload to WebhookUrl on each fire.</summary>
        public bool EnableWebhook = false;

        /// <summary>Discord/Slack/custom server endpoint.</summary>
        public string WebhookUrl = "";

        /// <summary>Append a CSV row to LogFilePath for each fire.</summary>
        public bool EnableLogFile = true;

        /// <summary>If empty, defaults to C:\IOF2\indicators\alerts_log.csv.</summary>
        public string LogFilePath = "";

        /// <summary>Bitmask of events that actually trigger; others are dropped.</summary>
        public AlertEventMask EventsToAlert = AlertEventMask.All;

        /// <summary>
        /// Cooldown window per (eventType + tradeId + symbol). Same logical
        /// event firing within this window is suppressed. Default 1 sec.
        /// </summary>
        public TimeSpan CooldownWindow = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Branding label used as the in-platform alert title and the
        /// Discord webhook username.
        /// </summary>
        public string SourceName = "TradePhantoms IOF";
    }

    // ════════════════════════════════════════════════════════════════════════
    // ALERTS MANAGER — static dispatcher.
    // ════════════════════════════════════════════════════════════════════════

    public static class AlertsManager
    {
        // ────────────────────────────────────────────────────────────────────
        // SHARED HTTP CLIENT
        // HttpClient is meant to be reused (one per process). Each Fire(...)
        // sharing a single instance avoids socket exhaustion under load.
        // ────────────────────────────────────────────────────────────────────

        private static readonly HttpClient httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(5);
            return c;
        }

        // ────────────────────────────────────────────────────────────────────
        // COOLDOWN STATE — shared across all callers in this process.
        // Locked via the dictionary itself (sync root). Static lifetime: a
        // chart reload doesn't reset cooldowns, but that's fine — a 1s window
        // is short enough to be irrelevant after a reload pause.
        // ────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<string, DateTime> lastFired
            = new Dictionary<string, DateTime>();

        // ════════════════════════════════════════════════════════════════════
        // PUBLIC: Fire — the universal entry point.
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Dispatch an alert to all enabled channels. Each channel is isolated
        /// in its own try/catch so a single failure (e.g., webhook timeout)
        /// can't take down the others or the caller.
        /// </summary>
        /// <param name="config">Channel toggles + cooldown + webhook URL.</param>
        /// <param name="eventType">Single-flag event identity.</param>
        /// <param name="title">Short summary, ~40 chars (in-platform headline).</param>
        /// <param name="body">Full message body (longer text for webhooks).</param>
        /// <param name="symbol">Ticker (e.g., "ESM5"). May be empty.</param>
        /// <param name="price">Optional price tag.</param>
        /// <param name="tradeId">Optional trade-record ID.</param>
        /// <param name="extraTag">Optional free-form tag (formation code, etc.).</param>
        public static void Fire(
            AlertsConfig config,
            AlertEventMask eventType,
            string title,
            string body,
            string symbol,
            double? price = null,
            int? tradeId = null,
            string extraTag = null)
        {
            if (config == null) return;

            // ── Mask check ──────────────────────────────────────────────────
            if ((config.EventsToAlert & eventType) == 0) return;

            // ── Cooldown dedup ──────────────────────────────────────────────
            string cooldownKey = BuildCooldownKey(eventType, tradeId, symbol);
            DateTime now = DateTime.UtcNow;
            lock (lastFired)
            {
                DateTime prev;
                if (lastFired.TryGetValue(cooldownKey, out prev))
                {
                    if (now - prev < config.CooldownWindow) return;
                }
                lastFired[cooldownKey] = now;

                // Periodic prune: if dict grows beyond 500 entries, drop
                // anything older than 5x the cooldown window. Cheap insurance
                // against unbounded growth across long sessions.
                if (lastFired.Count > 500)
                {
                    PruneCooldownLocked(now, config.CooldownWindow);
                }
            }

            // ── Channel 1: In-platform alert ────────────────────────────────
            if (config.EnableInPlatform)
            {
                try
                {
                    SendInPlatform(config.SourceName ?? "TradePhantoms IOF",
                                   title, body);
                }
                catch
                {
                    // Swallow — the SDK property may not exist on this version.
                }
            }

            // ── Channel 2: Audible beep ─────────────────────────────────────
            if (config.EnableAudible)
            {
                try
                {
                    SendAudible(eventType);
                }
                catch
                {
                    // Console.Beep is unavailable on some environments
                    // (no audio device, headless host, non-Windows runtime).
                }
            }

            // ── Channel 3: Webhook (fire-and-forget) ────────────────────────
            if (config.EnableWebhook && !string.IsNullOrWhiteSpace(config.WebhookUrl))
            {
                try
                {
                    string url = config.WebhookUrl;
                    string payload = BuildWebhookPayload(
                        url, config.SourceName, eventType,
                        title, body, symbol, price, tradeId, extraTag, now);

                    // Fire-and-forget: never await on the indicator thread.
                    Task.Run(() => SendWebhookAsync(url, payload));
                }
                catch
                {
                    // Payload-build failure shouldn't kill other channels.
                }
            }

            // ── Channel 4: CSV log file ─────────────────────────────────────
            if (config.EnableLogFile)
            {
                try
                {
                    string path = string.IsNullOrWhiteSpace(config.LogFilePath)
                        ? @"C:\IOF2\indicators\alerts_log.csv"
                        : config.LogFilePath;
                    AppendLogRow(path, now, eventType, symbol, price,
                                 tradeId, title, body, extraTag);
                }
                catch
                {
                    // Disk full / permission denied / locked file — skip.
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // CHANNEL: In-platform — Core.Instance.Alerts.AddAlert via reflection
        // ════════════════════════════════════════════════════════════════════

        private static void SendInPlatform(string sourceName, string title, string body)
        {
            // Probe for the Quantower SDK's Core.Instance.Alerts.AddAlert at
            // runtime so this file compiles even if the SDK reference is
            // missing or shaped differently. Mirrors the pattern used in
            // TradePhantoms_IOF.cs (~line 1100).
            //
            // We look up TradingPlatform.BusinessLayer.Core via type name to
            // avoid a hard dependency at compile time. If the type isn't
            // loaded in the AppDomain the call silently no-ops.
            Type coreType = FindType("TradingPlatform.BusinessLayer.Core");
            if (coreType == null) return;

            var instProp = coreType.GetProperty("Instance");
            if (instProp == null) return;
            object core = instProp.GetValue(null, null);
            if (core == null) return;

            var alertsProp = core.GetType().GetProperty("Alerts");
            if (alertsProp == null) return;
            object alerts = alertsProp.GetValue(core, null);
            if (alerts == null) return;

            // Compose payload: title doubles as source-tag prefix, body holds
            // the full message. Uses a single-line concatenation since some
            // SDK versions render the whole message in one cell.
            string heading = string.IsNullOrEmpty(title) ? sourceName : title;
            string message = string.IsNullOrEmpty(body) ? heading : body;

            var addMethod = alerts.GetType().GetMethod(
                "AddAlert", new[] { typeof(string), typeof(string) });
            if (addMethod != null)
            {
                addMethod.Invoke(alerts, new object[] { heading, message });
            }
        }

        /// <summary>
        /// Best-effort type lookup across loaded assemblies. Type.GetType
        /// alone fails for types in not-yet-imported assemblies.
        /// </summary>
        private static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName);
            if (t != null) return t;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(fullName);
                    if (t != null) return t;
                }
            }
            catch { /* ignore reflection failures */ }
            return null;
        }

        // ════════════════════════════════════════════════════════════════════
        // CHANNEL: Audible — Console.Beep at event-specific frequencies.
        // ════════════════════════════════════════════════════════════════════
        //
        // Frequency map (chosen to be distinct + tonally meaningful):
        //   ZoneDetected  →  800 Hz, 100 ms   (mid-low chirp, "noticed")
        //   Armed         → 1000 Hz, 150 ms   (clean mid, "ready")
        //   Filled        → 1500 Hz, 200 ms   (high tone, "go")
        //   TpHit         → 1800 Hz, 100 ms   (bright + short, "ding")
        //   SLHit         →  400 Hz, 300 ms   (low + long, "uh oh")
        //   Closed        → 1200 Hz, 250 ms   (rising, "done")
        //   (others fall back to Armed's tone)

        private static void SendAudible(AlertEventMask eventType)
        {
            int freq;
            int durMs;
            switch (eventType)
            {
                case AlertEventMask.ZoneDetected:    freq =  800; durMs = 100; break;
                case AlertEventMask.ZoneInvalidated: freq =  600; durMs = 150; break;
                case AlertEventMask.Armed:           freq = 1000; durMs = 150; break;
                case AlertEventMask.Filled:          freq = 1500; durMs = 200; break;
                case AlertEventMask.TpHit:           freq = 1800; durMs = 100; break;
                case AlertEventMask.SLHit:           freq =  400; durMs = 300; break;
                case AlertEventMask.Trailed:         freq = 1100; durMs =  80; break;
                case AlertEventMask.BreakEven:       freq =  900; durMs = 120; break;
                case AlertEventMask.Closed:          freq = 1200; durMs = 250; break;
                case AlertEventMask.ControlPointDetected: freq = 1300; durMs =  90; break;
                case AlertEventMask.TrendBroken:     freq =  600; durMs = 250; break;
                case AlertEventMask.TrendChanged:    freq =  950; durMs = 180; break;
                default:                             freq = 1000; durMs = 150; break;
            }

            // Console.Beep blocks the calling thread for the duration. To
            // avoid stalling the indicator's per-bar loop on a 300ms SL beep,
            // we offload to a worker. (Most channels are non-blocking; this
            // is the only one that's intrinsically synchronous in .NET.)
            Task.Run(() =>
            {
                try
                {
                    Console.Beep(freq, durMs);
                }
                catch
                {
                    // No audio device or non-Windows runtime — silently skip.
                }
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // CHANNEL: Webhook — POST JSON to Discord/Slack/generic.
        // ════════════════════════════════════════════════════════════════════

        private static string BuildWebhookPayload(
            string url,
            string sourceName,
            AlertEventMask eventType,
            string title,
            string body,
            string symbol,
            double? price,
            int? tradeId,
            string extraTag,
            DateTime nowUtc)
        {
            string evt = eventType.ToString();
            string tsIso = nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ",
                                          CultureInfo.InvariantCulture);

            // Discord-style: a "content" field rendered as a single message.
            // Slack accepts the same `{ "text": "..." }` shape but Discord
            // uses `content` — we detect Discord by URL substring and
            // collapse the rest into the content body.
            if (!string.IsNullOrEmpty(url) &&
                url.IndexOf("discord.com/api/webhooks/",
                            StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var sb = new StringBuilder();
                sb.Append(title ?? "");
                sb.Append('\n');
                sb.Append(body ?? "");
                if (!string.IsNullOrEmpty(symbol) || price.HasValue || tradeId.HasValue)
                {
                    sb.Append("\nSymbol: ");
                    sb.Append(string.IsNullOrEmpty(symbol) ? "—" : symbol);
                    if (price.HasValue)
                    {
                        sb.Append(" | Price: ");
                        sb.Append(price.Value.ToString("0.########",
                                                      CultureInfo.InvariantCulture));
                    }
                    if (tradeId.HasValue)
                    {
                        sb.Append(" | Trade #");
                        sb.Append(tradeId.Value.ToString(CultureInfo.InvariantCulture));
                    }
                }

                var json = new StringBuilder();
                json.Append('{');
                json.Append("\"username\":");
                json.Append(JsonString(string.IsNullOrEmpty(sourceName)
                                       ? "TradePhantoms IOF" : sourceName));
                json.Append(',');
                json.Append("\"content\":");
                json.Append(JsonString(sb.ToString()));
                json.Append('}');
                return json.ToString();
            }

            // Generic JSON shape — flat, machine-parseable.
            var g = new StringBuilder();
            g.Append('{');
            g.Append("\"event\":");      g.Append(JsonString(evt));               g.Append(',');
            g.Append("\"symbol\":");     g.Append(JsonString(symbol ?? ""));      g.Append(',');
            g.Append("\"price\":");      g.Append(price.HasValue
                                                  ? price.Value.ToString("0.########",
                                                      CultureInfo.InvariantCulture)
                                                  : "null");
            g.Append(',');
            g.Append("\"tradeId\":");    g.Append(tradeId.HasValue
                                                  ? tradeId.Value.ToString(CultureInfo.InvariantCulture)
                                                  : "null");
            g.Append(',');
            g.Append("\"title\":");      g.Append(JsonString(title ?? ""));       g.Append(',');
            g.Append("\"body\":");       g.Append(JsonString(body ?? ""));        g.Append(',');
            g.Append("\"timestamp\":");  g.Append(JsonString(tsIso));             g.Append(',');
            g.Append("\"extra\":");      g.Append(JsonString(extraTag ?? ""));
            g.Append('}');
            return g.ToString();
        }

        private static async Task SendWebhookAsync(string url, string jsonPayload)
        {
            try
            {
                using (var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json"))
                using (var response = await httpClient.PostAsync(url, content).ConfigureAwait(false))
                {
                    // We don't care about the response body — Discord returns
                    // 204 on success, Slack returns 200 with "ok". Failures
                    // are logged but never thrown; this runs on a background
                    // task with no observer.
                    if (!response.IsSuccessStatusCode)
                    {
                        // Best-effort logging. If Quantower's logger isn't
                        // available, the type lookup fails and we skip.
                        TryLog("Webhook failed: " +
                               ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (Exception ex)
            {
                TryLog("Webhook exception: " + ex.Message);
            }
        }

        /// <summary>
        /// Minimal JSON string encoder. Escapes the four characters that
        /// would otherwise break the payload. Avoids pulling in Newtonsoft
        /// or System.Text.Json so this file stays self-contained.
        /// </summary>
        private static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    default:
                        if (c < 0x20)
                            sb.AppendFormat(CultureInfo.InvariantCulture,
                                            "\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════════
        // CHANNEL: CSV log file — append a row per alert.
        // ════════════════════════════════════════════════════════════════════

        private static readonly object logFileLock = new object();

        private static void AppendLogRow(
            string path,
            DateTime nowUtc,
            AlertEventMask eventType,
            string symbol,
            double? price,
            int? tradeId,
            string title,
            string body,
            string extraTag)
        {
            // Synchronize to avoid interleaved writes across worker tasks.
            lock (logFileLock)
            {
                bool isNew = !File.Exists(path);

                // Ensure parent directory exists.
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
                catch { /* fall through; the StreamWriter call will error */ }

                using (var sw = new StreamWriter(path, append: true, encoding: Encoding.UTF8))
                {
                    if (isNew)
                    {
                        sw.WriteLine("timestamp_utc,event,symbol,price,tradeId,title,body,extra");
                    }
                    var sb = new StringBuilder();
                    sb.Append(nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                                              CultureInfo.InvariantCulture));
                    sb.Append(',');
                    sb.Append(CsvField(eventType.ToString()));
                    sb.Append(',');
                    sb.Append(CsvField(symbol ?? ""));
                    sb.Append(',');
                    sb.Append(price.HasValue
                              ? price.Value.ToString("0.########",
                                                     CultureInfo.InvariantCulture)
                              : "");
                    sb.Append(',');
                    sb.Append(tradeId.HasValue
                              ? tradeId.Value.ToString(CultureInfo.InvariantCulture)
                              : "");
                    sb.Append(',');
                    sb.Append(CsvField(title ?? ""));
                    sb.Append(',');
                    sb.Append(CsvField(body ?? ""));
                    sb.Append(',');
                    sb.Append(CsvField(extraTag ?? ""));
                    sw.WriteLine(sb.ToString());
                }
            }
        }

        /// <summary>
        /// Quote-and-escape a CSV field. Always wraps in double quotes for
        /// safety; doubles internal quotes per RFC 4180.
        /// </summary>
        private static string CsvField(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\"", "\"\"")
                           .Replace("\r", " ")
                           .Replace("\n", " ") + "\"";
        }

        // ════════════════════════════════════════════════════════════════════
        // COOLDOWN HELPERS
        // ════════════════════════════════════════════════════════════════════

        private static string BuildCooldownKey(
            AlertEventMask eventType, int? tradeId, string symbol)
        {
            return string.Concat(
                ((int)eventType).ToString(CultureInfo.InvariantCulture),
                "|",
                tradeId.HasValue
                    ? tradeId.Value.ToString(CultureInfo.InvariantCulture)
                    : "-",
                "|",
                symbol ?? "");
        }

        /// <summary>
        /// Caller must already hold the <c>lastFired</c> lock.
        /// </summary>
        private static void PruneCooldownLocked(DateTime now, TimeSpan window)
        {
            TimeSpan staleAge = TimeSpan.FromTicks(window.Ticks * 5);
            var stale = new List<string>();
            foreach (var kv in lastFired)
            {
                if (now - kv.Value > staleAge) stale.Add(kv.Key);
            }
            for (int i = 0; i < stale.Count; i++)
                lastFired.Remove(stale[i]);
        }

        // ════════════════════════════════════════════════════════════════════
        // BEST-EFFORT INTERNAL LOGGER — uses Quantower's logger if reachable.
        // ════════════════════════════════════════════════════════════════════

        private static void TryLog(string msg)
        {
            try
            {
                Type coreType = FindType("TradingPlatform.BusinessLayer.Core");
                if (coreType == null) return;
                var instProp = coreType.GetProperty("Instance");
                if (instProp == null) return;
                object core = instProp.GetValue(null, null);
                if (core == null) return;
                var loggersProp = core.GetType().GetProperty("Loggers");
                if (loggersProp == null) return;
                object loggers = loggersProp.GetValue(core, null);
                if (loggers == null) return;
                var logMethod = loggers.GetType().GetMethod(
                    "Log", new[] { typeof(string) });
                if (logMethod != null)
                    logMethod.Invoke(loggers, new object[] { "[Alerts] " + msg });
            }
            catch { /* logging is best-effort */ }
        }

        // ════════════════════════════════════════════════════════════════════
        // CONVENIENCE WRAPPERS — one per logical event.
        // Format title/body once, then dispatch through Fire().
        // ════════════════════════════════════════════════════════════════════

        public static void FireZoneDetected(
            AlertsConfig cfg, string symbol, string formationCode,
            double score, double price, double zoneTop, double zoneBottom)
        {
            string title = string.Format(CultureInfo.InvariantCulture,
                "Zone {0} {1:0.##} pts", formationCode, score);
            string body = string.Format(CultureInfo.InvariantCulture,
                "New zone detected on {0}: formation {1}, score {2:0.##}. " +
                "Top {3:0.########}, bottom {4:0.########}, current price {5:0.########}.",
                symbol ?? "", formationCode ?? "", score, zoneTop, zoneBottom, price);
            Fire(cfg, AlertEventMask.ZoneDetected, title, body,
                 symbol, price, null, formationCode);
        }

        public static void FireZoneInvalidated(
            AlertsConfig cfg, string symbol, string formationCode,
            double price, int? tradeId)
        {
            string title = string.Format(CultureInfo.InvariantCulture,
                "Zone INVALID {0}", formationCode ?? "");
            string body = string.Format(CultureInfo.InvariantCulture,
                "Zone {0} on {1} invalidated at price {2:0.########}.",
                formationCode ?? "", symbol ?? "", price);
            Fire(cfg, AlertEventMask.ZoneInvalidated, title, body,
                 symbol, price, tradeId, formationCode);
        }

        public static void FireArmed(
            AlertsConfig cfg, string symbol, int tradeId,
            double entry, bool isLong)
        {
            string side = isLong ? "LONG" : "SHORT";
            string title = string.Format(CultureInfo.InvariantCulture,
                "ARMED #{0} {1} @ {2:0.########}", tradeId, side, entry);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Trade #{0} armed on {1}: {2} entry at {3:0.########}.",
                tradeId, symbol ?? "", side, entry);
            Fire(cfg, AlertEventMask.Armed, title, body,
                 symbol, entry, tradeId, side);
        }

        public static void FireFilled(
            AlertsConfig cfg, string symbol, int tradeId,
            double fillPrice, int contracts)
        {
            string title = string.Format(CultureInfo.InvariantCulture,
                "FILLED #{0} {1}c @ {2:0.########}", tradeId, contracts, fillPrice);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Trade #{0} filled on {1}: {2} contracts at {3:0.########}.",
                tradeId, symbol ?? "", contracts, fillPrice);
            Fire(cfg, AlertEventMask.Filled, title, body,
                 symbol, fillPrice, tradeId, contracts.ToString(CultureInfo.InvariantCulture));
        }

        public static void FireTpHit(
            AlertsConfig cfg, string symbol, int tradeId,
            int tpNumber, double tpPrice, double currentR)
        {
            string title = string.Format(CultureInfo.InvariantCulture,
                "TP{0} HIT #{1} @ {2:0.########}", tpNumber, tradeId, tpPrice);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Trade #{0} on {1} reached TP{2} at {3:0.########}. Live R: {4:+0.00;-0.00;0.00}.",
                tradeId, symbol ?? "", tpNumber, tpPrice, currentR);
            Fire(cfg, AlertEventMask.TpHit, title, body,
                 symbol, tpPrice, tradeId, "TP" + tpNumber.ToString(CultureInfo.InvariantCulture));
        }

        public static void FireSLHit(
            AlertsConfig cfg, string symbol, int tradeId,
            double slPrice, double finalR, double finalDollar)
        {
            string title = string.Format(CultureInfo.InvariantCulture,
                "SL HIT #{0} @ {1:0.########}", tradeId, slPrice);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Trade #{0} on {1} stopped out at {2:0.########}. Final R: {3:+0.00;-0.00;0.00}, P&L: {4:+0.00;-0.00;0.00}.",
                tradeId, symbol ?? "", slPrice, finalR, finalDollar);
            Fire(cfg, AlertEventMask.SLHit, title, body,
                 symbol, slPrice, tradeId,
                 finalR.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture));
        }

        public static void FireTrailed(
            AlertsConfig cfg, string symbol, int tradeId,
            double newSL, double currentR)
        {
            string title = string.Format(CultureInfo.InvariantCulture,
                "TRAIL #{0} → {1:0.########}", tradeId, newSL);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Trade #{0} on {1} trailed SL to {2:0.########}. Live R: {3:+0.00;-0.00;0.00}.",
                tradeId, symbol ?? "", newSL, currentR);
            Fire(cfg, AlertEventMask.Trailed, title, body,
                 symbol, newSL, tradeId, null);
        }

        public static void FireBreakEven(
            AlertsConfig cfg, string symbol, int tradeId)
        {
            string title = string.Format(CultureInfo.InvariantCulture,
                "BREAK-EVEN #{0}", tradeId);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Trade #{0} on {1} moved to break-even.",
                tradeId, symbol ?? "");
            Fire(cfg, AlertEventMask.BreakEven, title, body,
                 symbol, null, tradeId, null);
        }

        public static void FireClosed(
            AlertsConfig cfg, string symbol, int tradeId,
            string outcome, double finalR, double finalDollar)
        {
            string title = string.Format(CultureInfo.InvariantCulture,
                "CLOSED #{0} {1} {2:+0.00;-0.00;0.00}R", tradeId, outcome ?? "", finalR);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Trade #{0} on {1} closed: {2}. Final R: {3:+0.00;-0.00;0.00}, P&L: {4:+0.00;-0.00;0.00}.",
                tradeId, symbol ?? "", outcome ?? "", finalR, finalDollar);
            Fire(cfg, AlertEventMask.Closed, title, body,
                 symbol, null, tradeId, outcome);
        }

        // ════════════════════════════════════════════════════════════════════
        // TREND EVENT WRAPPERS — for TrendStateMachine integration.
        // ════════════════════════════════════════════════════════════════════

        public static void FireControlPointDetected(
            AlertsConfig cfg,
            string symbol,
            string direction,
            double price,
            string timeframe = null)
        {
            string dir = string.IsNullOrEmpty(direction) ? "?" : direction.ToUpperInvariant();
            string tf  = string.IsNullOrEmpty(timeframe) ? "" : " " + timeframe;
            string title = string.Format(CultureInfo.InvariantCulture,
                "CP {0}{1} @ {2:0.########}", dir, tf, price);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Control point detected on {0}{1}: {2} engulfing pivot at {3:0.########}.",
                symbol ?? "", tf, dir, price);
            Fire(cfg, AlertEventMask.ControlPointDetected, title, body,
                 symbol, price, null, dir);
        }

        public static void FireTrendBroken(
            AlertsConfig cfg,
            string symbol,
            string oldTrend,
            string brokenLevel,
            double brokenAt,
            int contractsAffected = 0,
            string timeframe = null)
        {
            string oldT = string.IsNullOrEmpty(oldTrend) ? "?" : oldTrend.ToUpperInvariant();
            string lvl  = string.IsNullOrEmpty(brokenLevel) ? "level" : brokenLevel;
            string tf   = string.IsNullOrEmpty(timeframe) ? "" : " " + timeframe;
            string title = string.Format(CultureInfo.InvariantCulture,
                "TREND BROKEN {0}{1} @ {2:0.########}", oldT, tf, brokenAt);
            string body = string.Format(CultureInfo.InvariantCulture,
                "{0} trend broken on {1}{2} via {3} at {4:0.########}. {5} contracts now against trend.",
                oldT, symbol ?? "", tf, lvl, brokenAt, contractsAffected);
            Fire(cfg, AlertEventMask.TrendBroken, title, body,
                 symbol, brokenAt, null, lvl);
        }

        public static void FireTrendChanged(
            AlertsConfig cfg,
            string symbol,
            string oldTrend,
            string newTrend,
            string timeframe = null)
        {
            string oldT = string.IsNullOrEmpty(oldTrend) ? "?" : oldTrend.ToUpperInvariant();
            string newT = string.IsNullOrEmpty(newTrend) ? "?" : newTrend.ToUpperInvariant();
            string tf   = string.IsNullOrEmpty(timeframe) ? "" : " " + timeframe;
            string title = string.Format(CultureInfo.InvariantCulture,
                "TREND {0} → {1}{2}", oldT, newT, tf);
            string body = string.Format(CultureInfo.InvariantCulture,
                "Trend changed on {0}{1}: {2} → {3}.",
                symbol ?? "", tf, oldT, newT);
            Fire(cfg, AlertEventMask.TrendChanged, title, body,
                 symbol, null, null, newT);
        }
    }
}
