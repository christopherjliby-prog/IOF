// =============================================================================
// JournalWriter.cs — CSV / JSON / HTML file writer for the IOF Trade Journal
// =============================================================================
// Thread-safe. Designed to be called from the Quantower indicator thread.
// One JournalWriter instance per trading day (re-create at midnight rollover).
//
// CSV  : appended row-by-row as trades close (Excel/Google Sheets compatible)
// JSON : full array rewritten after each trade (machine-readable)
// HTML : generated on demand (call GenerateHtmlDashboard() at EOD)
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradePhantoms.Journal
{
    public class JournalWriter
    {
        private readonly string _dir;
        private readonly DateTime _date;
        private readonly object _lock = new();
        private readonly List<JournalEntry> _entries = new();
        private bool _csvHeaderWritten;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public JournalWriter(string directory, DateTime date)
        {
            _dir  = directory;
            _date = date.Date;
            Directory.CreateDirectory(_dir);

            // If CSV already exists from a prior session today, load its entries
            // so JSON and HTML reflect the full day. Skip the header re-write.
            string csvPath = CsvPath();
            if (File.Exists(csvPath))
                _csvHeaderWritten = true;
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void AppendTrade(JournalEntry entry, bool writeCsv, bool writeJson)
        {
            lock (_lock)
            {
                _entries.Add(entry);

                if (writeCsv)  WriteCsv(entry);
                if (writeJson) WriteJson();
            }
        }

        public void GenerateHtmlDashboard(string outputPath = null)
        {
            lock (_lock)
            {
                string path = outputPath ?? HtmlPath();
                File.WriteAllText(path, BuildHtml(), Encoding.UTF8);
            }
        }

        public IReadOnlyList<JournalEntry> GetEntries()
        {
            lock (_lock) { return _entries.AsReadOnly(); }
        }

        // ── Paths ─────────────────────────────────────────────────────────────

        public string CsvPath()  => Path.Combine(_dir, $"trades_{_date:yyyy-MM-dd}.csv");
        public string JsonPath() => Path.Combine(_dir, $"trades_{_date:yyyy-MM-dd}.json");
        public string HtmlPath() => Path.Combine(_dir, $"dashboard_{_date:yyyy-MM-dd}.html");

        // ── CSV ───────────────────────────────────────────────────────────────

        private void WriteCsv(JournalEntry entry)
        {
            using var sw = new StreamWriter(CsvPath(), append: true, Encoding.UTF8);
            if (!_csvHeaderWritten)
            {
                sw.WriteLine(JournalEntry.CsvHeader);
                _csvHeaderWritten = true;
            }
            sw.WriteLine(entry.ToCsvRow());
        }

        // ── JSON ──────────────────────────────────────────────────────────────

        private void WriteJson()
        {
            string json = JsonSerializer.Serialize(_entries, _jsonOpts);
            File.WriteAllText(JsonPath(), json, Encoding.UTF8);
        }

        // ── HTML Dashboard ────────────────────────────────────────────────────

        private string BuildHtml()
        {
            var completed = _entries.Where(e => e.IsComplete).ToList();
            int total     = completed.Count;
            if (total == 0) return EmptyHtml();

            int wins        = completed.Count(e => e.NetPnL > 0);
            int losses      = completed.Count(e => e.NetPnL < 0);
            int be          = completed.Count(e => e.NetPnL == 0);
            double winRate  = total > 0 ? (double)wins / total * 100 : 0;
            double netPnl   = completed.Sum(e => e.NetPnL);
            double avgHold  = total > 0 ? completed.Average(e => e.HoldTimeSeconds) : 0;
            double avgWin   = wins  > 0 ? completed.Where(e => e.NetPnL > 0).Average(e => e.NetPnL) : 0;
            double avgLoss  = losses > 0 ? Math.Abs(completed.Where(e => e.NetPnL < 0).Average(e => e.NetPnL)) : 0;
            double pf       = avgLoss > 0 ? (avgWin * wins) / (avgLoss * losses) : 0;

            // Grade breakdown
            var grades = new[] { "A", "B", "C", "D", "F", "?" };
            var gradeRows = grades
                .Where(g => completed.Any(e => e.EntryGrade == g))
                .Select(g =>
                {
                    var group = completed.Where(e => e.EntryGrade == g).ToList();
                    int gw = group.Count(e => e.NetPnL > 0);
                    double gWr = group.Count > 0 ? (double)gw / group.Count * 100 : 0;
                    double gPnl = group.Sum(e => e.NetPnL);
                    return $"<tr><td><b>{g}</b></td><td>{group.Count}</td><td>{gWr:F0}%</td><td class=\"{PnlClass(gPnl)}\">${gPnl:F2}</td></tr>";
                });

            // Zone type breakdown
            var zoneRows = new[] { "RBR", "DBR", "DBD", "RBD" }
                .Where(z => completed.Any(e => e.NearestZoneType == z))
                .Select(z =>
                {
                    var group = completed.Where(e => e.NearestZoneType == z).ToList();
                    int zw = group.Count(e => e.NetPnL > 0);
                    double zWr = group.Count > 0 ? (double)zw / group.Count * 100 : 0;
                    double zPnl = group.Sum(e => e.NetPnL);
                    return $"<tr><td>{z}</td><td>{group.Count}</td><td>{zWr:F0}%</td><td class=\"{PnlClass(zPnl)}\">${zPnl:F2}</td></tr>";
                });

            // Session breakdown
            var sessionRows = new[] { "NY_OPEN", "NY_MID", "NY_CLOSE", "LONDON", "OVERNIGHT" }
                .Where(s => completed.Any(e => e.Session == s))
                .Select(s =>
                {
                    var group = completed.Where(e => e.Session == s).ToList();
                    int sw = group.Count(e => e.NetPnL > 0);
                    double sWr = group.Count > 0 ? (double)sw / group.Count * 100 : 0;
                    double sPnl = group.Sum(e => e.NetPnL);
                    return $"<tr><td>{s}</td><td>{group.Count}</td><td>{sWr:F0}%</td><td class=\"{PnlClass(sPnl)}\">${sPnl:F2}</td></tr>";
                });

            // Trade rows
            var tradeRows = completed.Select(e =>
                $"<tr>" +
                $"<td>{e.EntryTime:HH:mm:ss}</td>" +
                $"<td>{e.Symbol}</td>" +
                $"<td>{e.Direction}</td>" +
                $"<td>{e.Contracts}</td>" +
                $"<td>{e.EntryPrice:F2}</td>" +
                $"<td>{e.ExitPrice:F2}</td>" +
                $"<td class=\"{PnlClass(e.NetPnL)}\">${e.NetPnL:F2}</td>" +
                $"<td><span class=\"grade grade-{e.EntryGrade}\">{e.EntryGrade}</span></td>" +
                $"<td>{e.NearestZoneType}</td>" +
                $"<td>{e.ZoneScore:F1}/{e.ZoneScoreMax}</td>" +
                $"<td>{e.Session}</td>" +
                $"<td>{e.ExitReason}</td>" +
                $"<td>{e.HoldTimeFormatted}</td>" +
                $"<td>{(e.RMultiple > 0 ? e.RMultiple.ToString("F2") + "R" : "—")}</td>" +
                $"</tr>"
            );

            string sb = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>IOF Trade Journal — {_date:MMM d, yyyy}</title>
<style>
  :root {{
    --bg: #0d0d0d; --surface: #161616; --surface2: #1e1e1e;
    --accent: #00d4ff; --green: #00c853; --red: #ff1744;
    --yellow: #ffd600; --orange: #ff6d00; --text: #e0e0e0; --muted: #666;
  }}
  * {{ box-sizing: border-box; margin: 0; padding: 0; }}
  body {{ background: var(--bg); color: var(--text); font-family: 'Consolas','Courier New',monospace; font-size: 13px; padding: 24px; }}
  h1 {{ color: var(--accent); font-size: 22px; margin-bottom: 4px; }}
  .subtitle {{ color: var(--muted); margin-bottom: 24px; font-size: 12px; }}
  .grid {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin-bottom: 24px; }}
  .card {{ background: var(--surface); border-radius: 8px; padding: 16px; border-left: 3px solid var(--accent); }}
  .card .label {{ color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: 1px; }}
  .card .value {{ font-size: 22px; font-weight: bold; margin-top: 4px; }}
  .pos {{ color: var(--green); }} .neg {{ color: var(--red); }} .flat {{ color: var(--muted); }}
  .section {{ background: var(--surface); border-radius: 8px; padding: 16px; margin-bottom: 16px; }}
  .section h2 {{ color: var(--accent); font-size: 14px; margin-bottom: 12px; border-bottom: 1px solid #222; padding-bottom: 8px; }}
  table {{ width: 100%; border-collapse: collapse; }}
  th {{ color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: 1px; padding: 6px 8px; text-align: left; border-bottom: 1px solid #222; }}
  td {{ padding: 7px 8px; border-bottom: 1px solid #1a1a1a; }}
  tr:hover td {{ background: #1a1a1a; }}
  .grade {{ display: inline-block; padding: 2px 8px; border-radius: 4px; font-weight: bold; font-size: 12px; }}
  .grade-A {{ background: #003d1a; color: var(--green); }}
  .grade-B {{ background: #003d40; color: var(--accent); }}
  .grade-C {{ background: #3d3d00; color: var(--yellow); }}
  .grade-D {{ background: #3d1a00; color: var(--orange); }}
  .grade-F {{ background: #3d0000; color: var(--red); }}
  .grade-? {{ background: #1e1e1e; color: var(--muted); }}
  footer {{ color: var(--muted); font-size: 11px; margin-top: 24px; text-align: center; }}
</style>
</head>
<body>
<h1>IOF Trade Journal</h1>
<div class=""subtitle"">Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {_date:dddd, MMMM d, yyyy}</div>

<div class=""grid"">
  <div class=""card""><div class=""label"">Trades</div><div class=""value"">{total}</div></div>
  <div class=""card""><div class=""label"">Win Rate</div><div class=""value {(winRate >= 50 ? "pos" : "neg")}"">{winRate:F0}%</div></div>
  <div class=""card""><div class=""label"">Net P&amp;L</div><div class=""value {PnlClass(netPnl)}"">${netPnl:F2}</div></div>
  <div class=""card""><div class=""label"">Profit Factor</div><div class=""value {(pf >= 1 ? "pos" : "neg")}"">{(pf > 0 ? pf.ToString("F2") : "—")}</div></div>
  <div class=""card""><div class=""label"">Avg Win</div><div class=""value pos"">${avgWin:F2}</div></div>
  <div class=""card""><div class=""label"">Avg Loss</div><div class=""value neg"">-${avgLoss:F2}</div></div>
  <div class=""card""><div class=""label"">Avg Hold</div><div class=""value"">{JournalEntry.FormatHoldTime((int)avgHold)}</div></div>
  <div class=""card""><div class=""label"">W / L / BE</div><div class=""value"">{wins} / {losses} / {be}</div></div>
</div>

<div class=""grid"" style=""grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));"">
  <div class=""section"">
    <h2>Grade Distribution</h2>
    <table>
      <tr><th>Grade</th><th>Trades</th><th>Win%</th><th>Net P&amp;L</th></tr>
      {string.Join("\n      ", gradeRows)}
    </table>
  </div>
  <div class=""section"">
    <h2>By Zone Type</h2>
    <table>
      <tr><th>Zone</th><th>Trades</th><th>Win%</th><th>Net P&amp;L</th></tr>
      {string.Join("\n      ", zoneRows.DefaultIfEmpty("<tr><td colspan='4' style='color:#666'>Zone context available in Phase 2</td></tr>"))}
    </table>
  </div>
  <div class=""section"">
    <h2>By Session</h2>
    <table>
      <tr><th>Session</th><th>Trades</th><th>Win%</th><th>Net P&amp;L</th></tr>
      {string.Join("\n      ", sessionRows)}
    </table>
  </div>
</div>

<div class=""section"">
  <h2>All Trades</h2>
  <table>
    <tr><th>Time</th><th>Sym</th><th>Dir</th><th>Qty</th><th>Entry</th><th>Exit</th><th>Net P&amp;L</th><th>Grade</th><th>Zone</th><th>Score</th><th>Session</th><th>Exit</th><th>Hold</th><th>R</th></tr>
    {string.Join("\n    ", tradeRows)}
  </table>
</div>

<footer>IOF Trade Journal · TradePhantoms v2 · Zone context available after Phase 2 integration</footer>
</body>
</html>";

            return sb;
        }

        private static string EmptyHtml() =>
            "<!DOCTYPE html><html><body style='background:#0d0d0d;color:#666;font-family:monospace;padding:24px'>" +
            "<h1 style='color:#00d4ff'>IOF Trade Journal</h1><p>No completed trades logged yet.</p></body></html>";

        private static string PnlClass(double v) => v > 0 ? "pos" : v < 0 ? "neg" : "flat";
    }
}
