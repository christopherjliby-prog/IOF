// =============================================================================
// JournalWriter.cs — CSV / JSON / HTML file writer for the IOF Trade Journal
// =============================================================================
// Thread-safe. One JournalWriter instance per trading day.
//
// CSV  : appended row-by-row as trades close
// JSON : full array rewritten after each trade
// HTML : generated on demand (EOD at 4 PM ET or on dispose)
//        Phase 4 sections: D:×/ABS:× breakdown, MTFC/HVN comparison,
//        zone freshness, time-of-day P&L, full trade log
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
            if (File.Exists(CsvPath()))
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

            int    wins    = completed.Count(e => e.NetPnL > 0);
            int    losses  = completed.Count(e => e.NetPnL < 0);
            int    be      = completed.Count(e => e.NetPnL == 0);
            double winRate = total > 0 ? (double)wins / total * 100 : 0;
            double netPnl  = completed.Sum(e => e.NetPnL);
            double avgHold = total > 0 ? completed.Average(e => e.HoldTimeSeconds) : 0;
            double avgWin  = wins   > 0 ? completed.Where(e => e.NetPnL > 0).Average(e => e.NetPnL) : 0;
            double avgLoss = losses > 0 ? Math.Abs(completed.Where(e => e.NetPnL < 0).Average(e => e.NetPnL)) : 0;
            double pf      = avgLoss > 0 && losses > 0 ? (avgWin * wins) / (avgLoss * losses) : 0;

            // ── Grade breakdown ───────────────────────────────────────────────
            var gradeRows = new[] { "A", "B", "C", "D", "F", "?" }
                .Where(g => completed.Any(e => e.EntryGrade == g))
                .Select(g =>
                {
                    var grp = completed.Where(e => e.EntryGrade == g).ToList();
                    return AnalysisRow(g, grp);
                });

            // ── Zone type breakdown ───────────────────────────────────────────
            var zoneRows = new[] { "RBR", "DBR", "DBD", "RBD" }
                .Where(z => completed.Any(e => e.NearestZoneType == z))
                .Select(z => AnalysisRow(z, completed.Where(e => e.NearestZoneType == z).ToList()));

            // ── Session breakdown ─────────────────────────────────────────────
            var sessionRows = new[] { "NY_OPEN", "NY_MID", "NY_CLOSE", "LONDON", "OVERNIGHT" }
                .Where(s => completed.Any(e => e.Session == s))
                .Select(s => AnalysisRow(s, completed.Where(e => e.Session == s).ToList()));

            // ── Phase 4: Departure multiplier tiers ───────────────────────────
            var deptRows = MultiplierTierRows(completed, e => e.DepartureMultiplier,
                new[] { ("<1.5", 0, 1.5), ("1.5–2.0×", 1.5, 2.0), ("2.0–3.0×", 2.0, 3.0), (">3.0×", 3.0, double.MaxValue) });

            // ── Phase 4: Absorption multiplier tiers ─────────────────────────
            var absRows = MultiplierTierRows(completed, e => e.AbsorptionMultiplier,
                new[] { ("<1.5", 0, 1.5), ("1.5–2.0×", 1.5, 2.0), ("2.0–3.0×", 2.0, 3.0), (">3.0×", 3.0, double.MaxValue) });

            // ── Phase 4: MTFC comparison ──────────────────────────────────────
            var mtfcWith    = completed.Where(e => e.MtfcBonus > 0).ToList();
            var mtfcWithout = completed.Where(e => e.MtfcBonus <= 0).ToList();
            string mtfcRows = (mtfcWith.Count > 0 ? AnalysisRow("With MTFC", mtfcWith) : "") +
                              (mtfcWithout.Count > 0 ? AnalysisRow("No MTFC", mtfcWithout) : "");

            // ── Phase 4: HVN comparison ───────────────────────────────────────
            var hvnWith    = completed.Where(e => e.HvnConfluence).ToList();
            var hvnWithout = completed.Where(e => !e.HvnConfluence).ToList();
            string hvnRows = (hvnWith.Count > 0 ? AnalysisRow("With HVN", hvnWith) : "") +
                             (hvnWithout.Count > 0 ? AnalysisRow("No HVN", hvnWithout) : "");

            // ── Phase 4: Zone freshness ───────────────────────────────────────
            var freshnessRows = new[] { "FRESH", "TESTED", "DEGRADED" }
                .Where(f => completed.Any(e => e.ZonePurityAtEntry == f))
                .Select(f => AnalysisRow(f, completed.Where(e => e.ZonePurityAtEntry == f).ToList()));

            // ── Phase 4: Time of day P&L ──────────────────────────────────────
            var todRows = completed
                .Where(e => !string.IsNullOrEmpty(e.TimeOfDayBucket))
                .GroupBy(e => e.TimeOfDayBucket)
                .OrderBy(g => g.Key)
                .Select(g => AnalysisRow(g.Key, g.ToList()));

            // ── Phase 4: ITF trend comparison ─────────────────────────────────
            var trendRows = new[] { "BULL", "BEAR", "FLAT" }
                .Where(t => completed.Any(e => e.ItfTrend == t))
                .Select(t =>
                {
                    var grp = completed.Where(e => e.ItfTrend == t).ToList();
                    int withTrend    = grp.Count(e => e.TradeWithTrend);
                    int againstTrend = grp.Count(e => !e.TradeWithTrend);
                    return AnalysisRow($"{t} ({withTrend}↗/{againstTrend}↘)", grp);
                });

            // ── Phase 4: MAE/MFE summary ──────────────────────────────────────
            var withMfe  = completed.Where(e => e.MaxFavorableExcursion > 0).ToList();
            double avgMae = withMfe.Count > 0 ? withMfe.Average(e => e.MaxAdverseExcursion) : 0;
            double avgMfe = withMfe.Count > 0 ? withMfe.Average(e => e.MaxFavorableExcursion) : 0;
            int tp1Count  = completed.Count(e => e.HitTp1);
            int tp2Count  = completed.Count(e => e.HitTp2);
            int tp3Count  = completed.Count(e => e.HitTp3);
            int earlyCount = completed.Count(e => e.EarlyExit);

            // ── Full trade log ────────────────────────────────────────────────
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
                $"<td>{e.ZonePurityAtEntry}</td>" +
                $"<td>{e.Session}</td>" +
                $"<td>{e.ItfTrend}/{e.HtfTrend}</td>" +
                $"<td>{e.DepartureMultiplier:F1}×</td>" +
                $"<td>{e.AbsorptionMultiplier:F1}×</td>" +
                $"<td>{(e.HvnConfluence ? "✓" : "")}</td>" +
                $"<td>{(e.MtfcBonus > 0 ? "✓" : "")}</td>" +
                $"<td class=\"{PnlClass(e.MaxFavorableExcursion - e.MaxAdverseExcursion)}\">{e.MaxFavorableExcursion:F1}/{e.MaxAdverseExcursion:F1}</td>" +
                $"<td>{(e.RMultiple != 0 ? e.RMultiple.ToString("F2") + "R" : "—")}</td>" +
                $"<td>{e.ExitReason}</td>" +
                $"<td>{e.HoldTimeFormatted}</td>" +
                $"</tr>"
            );

            string html = $@"<!DOCTYPE html>
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
  .grid-3 {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 12px; margin-bottom: 16px; }}
  .card {{ background: var(--surface); border-radius: 8px; padding: 16px; border-left: 3px solid var(--accent); }}
  .card .label {{ color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: 1px; }}
  .card .value {{ font-size: 22px; font-weight: bold; margin-top: 4px; }}
  .pos {{ color: var(--green); }} .neg {{ color: var(--red); }} .flat {{ color: var(--muted); }}
  .section {{ background: var(--surface); border-radius: 8px; padding: 16px; margin-bottom: 16px; }}
  .section h2 {{ color: var(--accent); font-size: 14px; margin-bottom: 12px; border-bottom: 1px solid #222; padding-bottom: 8px; }}
  .section h3 {{ color: var(--muted); font-size: 12px; margin: 16px 0 8px; text-transform: uppercase; letter-spacing: 1px; }}
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
  .two-col {{ display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }}
  footer {{ color: var(--muted); font-size: 11px; margin-top: 24px; text-align: center; }}
  .mfe-stat {{ font-size: 18px; font-weight: bold; }}
  .tp-grid {{ display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; margin-top: 8px; }}
  .tp-box {{ background: var(--surface2); border-radius: 6px; padding: 12px; text-align: center; }}
  .tp-box .tpval {{ font-size: 20px; font-weight: bold; }}
  .tp-box .tplabel {{ color: var(--muted); font-size: 11px; margin-top: 2px; }}
  .scroll-x {{ overflow-x: auto; }}
</style>
</head>
<body>
<h1>WTF Are You Doing?! — IOF Trade Journal</h1>
<div class=""subtitle"">Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {_date:dddd, MMMM d, yyyy}</div>

<!-- ── Summary cards ── -->
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

<!-- ── Phase 3: MAE / MFE / TP cards ── -->
<div class=""section"">
  <h2>Excursion &amp; Target Analysis</h2>
  <div class=""grid"">
    <div class=""card""><div class=""label"">Avg MFE</div><div class=""value pos mfe-stat"">{avgMfe:F1}pt</div></div>
    <div class=""card""><div class=""label"">Avg MAE</div><div class=""value neg mfe-stat"">{avgMae:F1}pt</div></div>
    <div class=""card""><div class=""label"">MFE:MAE Ratio</div><div class=""value {(avgMae > 0 && avgMfe / avgMae >= 1.5 ? "pos" : "neg")}"">{(avgMae > 0 ? (avgMfe / avgMae).ToString("F2") : "—")}</div></div>
    <div class=""card""><div class=""label"">Early Exits</div><div class=""value {(earlyCount > total / 3 ? "neg" : "flat")}"">{earlyCount} / {wins}</div></div>
  </div>
  <div class=""tp-grid"">
    <div class=""tp-box""><div class=""tpval pos"">{tp1Count}</div><div class=""tplabel"">Hit TP1 (1× zone)</div></div>
    <div class=""tp-box""><div class=""tpval pos"">{tp2Count}</div><div class=""tplabel"">Hit TP2 (2× zone)</div></div>
    <div class=""tp-box""><div class=""tpval pos"">{tp3Count}</div><div class=""tplabel"">Hit TP3 (3× zone)</div></div>
    <div class=""tp-box""><div class=""tpval {(earlyCount > 0 ? "neg" : "pos")}"">{earlyCount}</div><div class=""tplabel"">Left Early (profitable but &lt;TP1)</div></div>
  </div>
</div>

<!-- ── Grade + Zone + Session ── -->
<div class=""grid-3"">
  <div class=""section"">
    <h2>Grade Distribution</h2>
    <table><tr><th>Grade</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
      {string.Join("\n      ", gradeRows)}
    </table>
  </div>
  <div class=""section"">
    <h2>By Zone Type</h2>
    <table><tr><th>Zone</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
      {string.Join("\n      ", zoneRows.DefaultIfEmpty("<tr><td colspan='5' style='color:#666'>No zone data</td></tr>"))}
    </table>
  </div>
  <div class=""section"">
    <h2>By Session</h2>
    <table><tr><th>Session</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
      {string.Join("\n      ", sessionRows)}
    </table>
  </div>
</div>

<!-- ── Phase 4: Zone quality analytics ── -->
<div class=""section"">
  <h2>Zone Quality Analytics</h2>
  <div class=""two-col"">
    <div>
      <h3>Departure Strength (D:×)</h3>
      <table><tr><th>Range</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
        {string.Join("\n        ", deptRows.DefaultIfEmpty("<tr><td colspan='5' style='color:#666'>No departure data</td></tr>"))}
      </table>
    </div>
    <div>
      <h3>Absorption Strength (ABS:×)</h3>
      <table><tr><th>Range</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
        {string.Join("\n        ", absRows.DefaultIfEmpty("<tr><td colspan='5' style='color:#666'>No absorption data</td></tr>"))}
      </table>
    </div>
  </div>
  <div class=""two-col"" style=""margin-top:16px"">
    <div>
      <h3>MTFC Confluence</h3>
      <table><tr><th>Condition</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
        {(string.IsNullOrEmpty(mtfcRows) ? "<tr><td colspan='5' style='color:#666'>No MTFC data</td></tr>" : mtfcRows)}
      </table>
    </div>
    <div>
      <h3>HVN Confluence</h3>
      <table><tr><th>Condition</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
        {(string.IsNullOrEmpty(hvnRows) ? "<tr><td colspan='5' style='color:#666'>No HVN data</td></tr>" : hvnRows)}
      </table>
    </div>
  </div>
  <div class=""two-col"" style=""margin-top:16px"">
    <div>
      <h3>Zone Freshness</h3>
      <table><tr><th>Purity</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
        {string.Join("\n        ", freshnessRows.DefaultIfEmpty("<tr><td colspan='5' style='color:#666'>No freshness data</td></tr>"))}
      </table>
    </div>
    <div>
      <h3>ITF Trend at Entry</h3>
      <table><tr><th>Trend</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
        {string.Join("\n        ", trendRows.DefaultIfEmpty("<tr><td colspan='5' style='color:#666'>No trend data</td></tr>"))}
      </table>
    </div>
  </div>
</div>

<!-- ── Phase 4: Time of day P&L ── -->
<div class=""section"">
  <h2>Time of Day P&amp;L (30-min buckets)</h2>
  <table><tr><th>Bucket</th><th>#</th><th>Win%</th><th>Avg P&amp;L</th><th>Net P&amp;L</th></tr>
    {string.Join("\n    ", todRows.DefaultIfEmpty("<tr><td colspan='5' style='color:#666'>No time-of-day data</td></tr>"))}
  </table>
</div>

<!-- ── Full trade log ── -->
<div class=""section"">
  <h2>All Trades</h2>
  <div class=""scroll-x"">
  <table>
    <tr>
      <th>Time</th><th>Sym</th><th>Dir</th><th>Qty</th>
      <th>Entry</th><th>Exit</th><th>Net P&amp;L</th>
      <th>Grade</th><th>Zone</th><th>Purity</th><th>Session</th>
      <th>ITF/HTF</th><th>D:×</th><th>ABS:×</th><th>HVN</th><th>MTFC</th>
      <th>MFE/MAE</th><th>R</th><th>Exit</th><th>Hold</th>
    </tr>
    {string.Join("\n    ", tradeRows)}
  </table>
  </div>
</div>

<footer>WTF Are You Doing?! IOF Trade Journal · TradePhantoms v2 · {_date:yyyy-MM-dd}</footer>
</body>
</html>";

            return html;
        }

        // ── HTML helpers ──────────────────────────────────────────────────────

        private static string AnalysisRow(string label, List<JournalEntry> group)
        {
            if (group.Count == 0) return "";
            int wins    = group.Count(e => e.NetPnL > 0);
            double wr   = (double)wins / group.Count * 100;
            double net  = group.Sum(e => e.NetPnL);
            double avg  = group.Average(e => e.NetPnL);
            return $"<tr><td>{label}</td><td>{group.Count}</td><td>{wr:F0}%</td>" +
                   $"<td class=\"{PnlClass(avg)}\">${avg:F2}</td>" +
                   $"<td class=\"{PnlClass(net)}\">${net:F2}</td></tr>";
        }

        private static IEnumerable<string> MultiplierTierRows(
            List<JournalEntry> entries,
            Func<JournalEntry, double> selector,
            (string label, double lo, double hi)[] tiers)
        {
            foreach (var (label, lo, hi) in tiers)
            {
                var grp = entries.Where(e =>
                {
                    double v = selector(e);
                    return v >= lo && v < hi;
                }).ToList();
                if (grp.Count == 0) continue;
                yield return AnalysisRow(label, grp);
            }
        }

        private static string EmptyHtml() =>
            "<!DOCTYPE html><html><body style='background:#0d0d0d;color:#666;font-family:monospace;padding:24px'>" +
            "<h1 style='color:#00d4ff'>WTF Are You Doing?! — IOF Trade Journal</h1>" +
            "<p>No completed trades logged yet.</p></body></html>";

        private static string PnlClass(double v) => v > 0 ? "pos" : v < 0 ? "neg" : "flat";
    }
}
