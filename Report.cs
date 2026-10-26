using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace GroupRun
{
    /// <summary>
    /// The post-run report: a single HTML file in the run's folder (opens in any browser, easy to drop in
    /// Discord) with XP/h over the run for every runner, a summary table and everyone's zone splits, all
    /// built from the run log. The numbers are computed here; the page only draws them.
    /// </summary>
    public static class Report
    {
        const double Window = 600; // XP/h is measured over the last 10 minutes

        // categorical slots for a dark surface, validated for colour-blind separation (dataviz palette)
        static readonly string[] Colors = { "#3987e5", "#d95926", "#199e70", "#c98500", "#d55181", "#008300" };

        /// <summary>Write the report into the run's folder and return its path; optionally open it.</summary>
        public static string Save(Run run, RunLog log, bool open)
        {
            var path = Path.Combine(log.FolderPath, "report.html");
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            File.WriteAllText(path, Template.Replace("__DATA__", json.Serialize(Data(run, log))));
            if (open)
                Process.Start(path);
            return path;
        }

        /// <summary>
        /// Total XP over run time from the bar readings and level-ups. A single reading that disagrees with both
        /// neighbours is dropped (median of three), and XP never goes down except across a death.
        /// </summary>
        static List<(double Seconds, double Total)> XpCurve(RunnerHistory h)
        {
            var points = h.Records
                .Where(r => (r["k"] as string) == "xp" && r["bar"] != null && !r.ContainsKey("suspect") || (r["k"] as string) == "levelup")
                .Select(r => (Seconds: RunnerHistory.Seconds(r),
                    Total: XpTable.Total(Convert.ToInt32(r["lvl"]), r.TryGetValue("bar", out var bar) && bar != null ? Convert.ToDouble(bar) : 0)))
                .ToList();
            var deaths = h.Of("death").Select(RunnerHistory.Seconds).ToList();
            var curve = new List<(double, double)>();
            var floor = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var total = points[i].Total;
                if (i > 0 && i < points.Count - 1)
                    total = new[] { points[i - 1].Total, total, points[i + 1].Total }.OrderBy(v => v).ElementAt(1);
                if (i > 0 && deaths.Any(d => d > points[i - 1].Seconds && d <= points[i].Seconds))
                    floor = 0; // a death can cost XP
                floor = Math.Max(floor, total);
                curve.Add((points[i].Seconds, floor));
            }
            return curve;
        }

        static double At(List<(double Seconds, double Total)> curve, double seconds)
        {
            var value = curve.Count > 0 ? curve[0].Total : 0;
            foreach (var p in curve)
            {
                if (p.Seconds > seconds) break;
                value = p.Total;
            }
            return value;
        }

        /// <summary>Loading screens: from "connecting to instance" to the zone line, by the log's millisecond counter when there is one.</summary>
        static List<double> Loads(RunnerHistory h)
        {
            var loads = new List<double>();
            Dictionary<string, object> start = null;
            foreach (var r in h.Records)
            {
                var kind = r["k"] as string;
                if (kind == "load")
                    start = r;
                else if (kind == "zone" && start != null)
                {
                    loads.Add(start.ContainsKey("tick") && r.ContainsKey("tick")
                        ? (Convert.ToInt64(r["tick"]) - Convert.ToInt64(start["tick"])) / 1000.0
                        : RunnerHistory.Seconds(r) - RunnerHistory.Seconds(start));
                    start = null;
                }
            }
            return loads;
        }

        static Dictionary<string, object> Data(Run run, RunLog log)
        {
            var duration = Math.Max(1, run.Elapsed.TotalSeconds);
            var step = Math.Max(15, duration / 150);
            var histories = RunnerHistory.From(log)
                .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var runners = new List<Dictionary<string, object>>();
            for (var i = 0; i < histories.Count; i++)
            {
                var h = histories[i];
                var curve = XpCurve(h);
                var gained = curve.Count == 0 ? 0 : curve[curve.Count - 1].Total - curve[0].Total;
                var series = new List<double[]>();
                for (var t = step; t <= duration + 0.001; t += step)
                {
                    var window = Math.Min(Window, t);
                    series.Add(new[] { Math.Round(t), Math.Round((At(curve, t) - At(curve, t - window)) / window * 3600) });
                }
                var reads = h.Of("xp").ToList();
                // reads that could have worked: the game in front, not loading (a null "why" means a failed read)
                var possible = reads.Where(r => !r.ContainsKey("why")).ToList();
                var lastXp = reads.LastOrDefault(r => r["bar"] != null);
                var loads = Loads(h);
                runners.Add(new Dictionary<string, object>
                {
                    ["name"] = h.Name,
                    ["color"] = Colors[i % Colors.Length],
                    ["level"] = lastXp == null ? 1 : Math.Round(Convert.ToInt32(lastXp["lvl"]) + Convert.ToDouble(lastXp["bar"]), 2),
                    ["gained"] = Math.Round(gained),
                    ["average"] = Math.Round(gained / duration * 3600),
                    ["peak"] = series.Count == 0 ? 0 : series.Max(p => p[1]),
                    ["deaths"] = h.Of("death").Count(),
                    ["loads"] = loads.Count,
                    ["loading"] = Math.Round(loads.Sum()),
                    ["readOk"] = possible.Count == 0 ? 0 : Math.Round(100.0 * possible.Count(r => r["bar"] != null && !r.ContainsKey("suspect")) / possible.Count),
                    ["series"] = series,
                    ["splits"] = h.Of("zone")
                        .GroupBy(r => (string)r["zone"])
                        .Where(g => !g.Key.EndsWith("Hideout"))
                        .ToDictionary(g => g.Key, g => Math.Round(g.Min(RunnerHistory.Seconds))),
                });
            }

            // zones in the order the group reached them
            var zones = histories.SelectMany(h => h.Of("zone"))
                .GroupBy(r => (string)r["zone"])
                .Where(g => !g.Key.EndsWith("Hideout"))
                .OrderBy(g => g.Min(RunnerHistory.Seconds))
                .Select(g => g.Key)
                .ToList();

            return new Dictionary<string, object>
            {
                ["date"] = log.StartedAt.ToString("d MMM yyyy, HH:mm"),
                ["duration"] = Math.Round(duration),
                ["window"] = Window,
                ["runners"] = runners,
                ["zones"] = zones,
            };
        }

        const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Group run report</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Geist:wght@400;500;600&family=Geist+Mono:wght@400;500&display=swap" rel="stylesheet">
<style>
  :root {
    --bg: #0a0a0a; --panel: #111111; --text: #ededed; --secondary: #a1a1a1; --faint: #5c5c5c;
    --line: #1f1f1f; --green: #45c46b;
    --sans: "Geist", "Segoe UI", system-ui, sans-serif; --mono: "Geist Mono", Consolas, monospace;
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.45 var(--sans); }
  main { max-width: 980px; margin: 0 auto; padding: 32px 16px 64px; }
  header { display: flex; justify-content: space-between; align-items: baseline; gap: 16px; flex-wrap: wrap; }
  h1 { font-size: 20px; font-weight: 600; margin: 0; letter-spacing: -0.01em; }
  h2 { font: 500 11px var(--mono); letter-spacing: 0.06em; text-transform: uppercase; color: var(--secondary); margin: 40px 0 12px; }
  .meta { font: 13px var(--mono); color: var(--secondary); }
  .meta b { color: var(--text); font-weight: 500; font-size: 22px; margin-left: 8px; }
  table { width: 100%; border-collapse: collapse; }
  th { font: 500 11px var(--mono); letter-spacing: 0.06em; text-transform: uppercase; color: var(--faint); text-align: right; padding: 0 10px 8px; white-space: nowrap; }
  th:first-child, td:first-child { text-align: left; padding-left: 0; }
  td { border-top: 1px solid var(--line); padding: 7px 10px; text-align: right; font-family: var(--mono); white-space: nowrap; }
  td.name { font-family: var(--sans); font-weight: 600; font-size: 15px; }
  td.zone { font-family: var(--sans); color: var(--secondary); }
  .swatch { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 8px; vertical-align: 0; }
  .gap { color: var(--secondary); font-size: 12px; margin-left: 6px; }
  .best { color: var(--text); }
  .best::after { content: ""; display: inline-block; width: 6px; height: 6px; border-radius: 3px; background: var(--green); margin-left: 6px; vertical-align: 1px; }
  .missing { color: var(--faint); }
  .scroll { overflow-x: auto; }
  .chart { position: relative; background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 12px 8px 4px; }
  .legend { display: flex; gap: 16px; flex-wrap: wrap; font-size: 13px; color: var(--secondary); padding: 0 8px 8px; }
  svg { display: block; width: 100%; height: auto; }
  .tip { position: absolute; pointer-events: none; background: #171717; border: 1px solid #333; border-radius: 6px; padding: 8px 10px; font: 12px var(--mono); display: none; min-width: 150px; }
  .tip div { display: flex; justify-content: space-between; gap: 14px; }
  .tip .t { color: var(--secondary); margin-bottom: 4px; }
  .note { color: var(--faint); font-size: 12px; margin-top: 8px; }
</style>
</head>
<body>
<main>
  <header><h1>Group run</h1><div class="meta" id="meta"></div></header>

  <h2>Results</h2>
  <div class="scroll"><table id="summary"></table></div>

  <h2>XP per hour</h2>
  <div class="chart" id="chart"><div class="legend" id="legend"></div><div class="tip" id="tip"></div></div>
  <div class="note" id="note"></div>

  <h2>Splits · first entry into each zone</h2>
  <div class="scroll"><table id="splits"></table></div>
</main>
<script>
const data = __DATA__;
const runners = data.runners;

const time = s => { s = Math.round(s); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = String(s % 60).padStart(2, "0");
  return h ? `${h}:${String(m).padStart(2, "0")}:${x}` : `${m}:${x}`; };
const xp = v => v >= 1e6 ? (v / 1e6).toFixed(v >= 1e7 ? 1 : 2) + "M" : v >= 1e3 ? Math.round(v / 1e3) + "k" : String(Math.round(v));
const el = (tag, html, cls) => { const e = document.createElement(tag); e.innerHTML = html; if (cls) e.className = cls; return e; };

document.getElementById("meta").innerHTML = `${data.date}<b>${time(data.duration)}</b>`;
document.getElementById("note").textContent =
  `XP/h over the previous ${data.window / 60} minutes, from the in-game XP table. The bar is read every 2 s; single misreads are filtered out.`;

// results
{
  const t = document.getElementById("summary");
  t.append(el("tr", "<th>Runner</th><th>Level</th><th>XP gained</th><th>Avg XP/h</th><th>Peak XP/h</th><th>Deaths</th><th>Loading</th><th>XP reads</th>"));
  runners.forEach(r => {
    const tr = el("tr", `<td class="name"><span class="swatch" style="background:${r.color}"></span>${r.name}</td>
      <td>${r.level.toFixed(2)}</td><td>${xp(r.gained)}</td><td>${xp(r.average)}</td><td>${xp(r.peak)}</td><td>${r.deaths}</td>
      <td>${time(r.loading)}<span class="gap">${r.loads}×</span></td><td>${r.readOk}%</td>`);
    t.append(tr);
  });
}

// XP/h chart: one line per runner, crosshair with every runner's value on hover
{
  const W = 940, H = 320, L = 56, R = 64, T = 12, B = 30;
  const chart = document.getElementById("chart"), tip = document.getElementById("tip");
  runners.forEach(r => document.getElementById("legend").append(el("span", `<span class="swatch" style="background:${r.color}"></span>${r.name}`)));
  const maxY = Math.max(1, ...runners.flatMap(r => r.series.map(p => p[1])));
  const niceStep = v => { const p = Math.pow(10, Math.floor(Math.log10(v))); return [1, 2, 2.5, 5, 10].map(m => m * p).find(s => v / s <= 5); };
  const yStep = niceStep(maxY), yMax = Math.ceil(maxY / yStep) * yStep;
  const x = t => L + t / data.duration * (W - L - R), y = v => T + (1 - v / yMax) * (H - T - B);
  const ns = "http://www.w3.org/2000/svg";
  let svg = `<svg viewBox="0 0 ${W} ${H}" xmlns="${ns}" role="img" aria-label="XP per hour over the run">`;
  for (let v = 0; v <= yMax + 1e-9; v += yStep)
    svg += `<line x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}" stroke="#1f1f1f"/>
            <text x="${L - 8}" y="${y(v) + 4}" text-anchor="end" fill="#5c5c5c" font-family="Geist Mono, monospace" font-size="11">${xp(v)}</text>`;
  const tStep = [60, 120, 300, 600, 900, 1800, 3600].find(s => data.duration / s <= 8) || 7200;
  for (let t = 0; t <= data.duration; t += tStep)
    svg += `<text x="${x(t)}" y="${H - 8}" text-anchor="middle" fill="#5c5c5c" font-family="Geist Mono, monospace" font-size="11">${time(t)}</text>`;
  runners.forEach(r => {
    if (!r.series.length) return;
    const d = r.series.map((p, i) => `${i ? "L" : "M"}${x(p[0]).toFixed(1)},${y(p[1]).toFixed(1)}`).join("");
    svg += `<path d="${d}" fill="none" stroke="${r.color}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>`;
  });
  if (runners.length <= 4) runners.forEach(r => {   // direct labels at the line ends
    const p = r.series[r.series.length - 1]; if (!p) return;
    svg += `<text x="${x(p[0]) + 6}" y="${y(p[1]) + 4}" fill="#a1a1a1" font-family="Geist, sans-serif" font-size="12">${r.name}</text>`;
  });
  svg += `<line id="cross" y1="${T}" y2="${H - B}" stroke="#5c5c5c" stroke-dasharray="3 3" visibility="hidden"/>`;
  svg += `<g id="dots"></g><rect x="${L}" y="${T}" width="${W - L - R}" height="${H - T - B}" fill="transparent" id="hit"/></svg>`;
  chart.insertAdjacentHTML("beforeend", svg);

  const root = chart.querySelector("svg"), cross = root.querySelector("#cross"), dots = root.querySelector("#dots");
  root.querySelector("#hit").addEventListener("mousemove", e => {
    const box = root.getBoundingClientRect(), scale = W / box.width;
    const t = ((e.clientX - box.left) * scale - L) / (W - L - R) * data.duration;
    const rows = runners.map(r => {
      const p = r.series.reduce((best, q) => Math.abs(q[0] - t) < Math.abs(best[0] - t) ? q : best, r.series[0] || [0, 0]);
      return { r, p };
    });
    const at = rows[0] ? rows[0].p[0] : 0;
    cross.setAttribute("x1", x(at)); cross.setAttribute("x2", x(at)); cross.setAttribute("visibility", "visible");
    dots.innerHTML = rows.map(({ r, p }) => `<circle cx="${x(p[0])}" cy="${y(p[1])}" r="4" fill="${r.color}" stroke="#111" stroke-width="2"/>`).join("");
    tip.innerHTML = `<div class="t">${time(at)}</div>` + [...rows].sort((a, b) => b.p[1] - a.p[1])
      .map(({ r, p }) => `<div><span><span class="swatch" style="background:${r.color}"></span>${r.name}</span><span>${xp(p[1])}/h</span></div>`).join("");
    tip.style.display = "block";
    const cb = chart.getBoundingClientRect(), px = e.clientX - cb.left;
    tip.style.left = (px + 16 + tip.offsetWidth > cb.width ? px - 16 - tip.offsetWidth : px + 16) + "px";
    tip.style.top = (e.clientY - cb.top - 20) + "px";
  });
  root.querySelector("#hit").addEventListener("mouseleave", () => { tip.style.display = "none"; cross.setAttribute("visibility", "hidden"); dots.innerHTML = ""; });
}

// splits: each runner's first entry into each zone, the fastest marked, the rest as a gap to it
{
  const t = document.getElementById("splits");
  t.append(el("tr", "<th>Zone</th>" + runners.map(r => `<th><span class="swatch" style="background:${r.color}"></span>${r.name}</th>`).join("")));
  data.zones.forEach(zone => {
    const times = runners.map(r => r.splits[zone]);
    const best = Math.min(...times.filter(v => v !== undefined));
    const cells = times.map(v => v === undefined ? `<td class="missing">—</td>`
      : v === best ? `<td><span class="best">${time(v)}</span></td>`
      : `<td>${time(v)}<span class="gap">+${time(v - best)}</span></td>`).join("");
    t.append(el("tr", `<td class="zone">${zone}</td>${cells}`));
  });
}
</script>
</body>
</html>
""";
    }
}
