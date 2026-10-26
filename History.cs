using System;
using System.IO;
using System.Linq;
using System.Text;

namespace GroupRun
{
    /// <summary>Appends each finished run to runs.csv next to the exe: one line per runner.</summary>
    public static class History
    {
        public static string Save(Run run)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "runs.csv");
            var lines = new StringBuilder();
            if (!File.Exists(path))
                lines.AppendLine("finished_at,run_time,runner,level,xp_percent,deaths,zone");
            var finished = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            foreach (var r in run.InOrder)
                lines.AppendLine(string.Join(",", finished, PanelView.FormatTime(run.Elapsed), Csv(r.Name), r.Level,
                    r.Xp.HasValue ? (r.Xp.Value * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "",
                    r.Deaths, Csv(r.Zone)));
            File.AppendAllText(path, lines.ToString());
            return path;
        }

        static string Csv(string value) =>
            value.Any(c => c == ',' || c == '"' || c == '\r' || c == '\n') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
