using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace GroupRun
{
    /// <summary>
    /// The run's history as it happens: one JSON line per message, appended and flushed straight away so a
    /// crash loses nothing. The admin's copy logs every message from everyone (board updates, run control
    /// and each runner's detail stream); each runner also logs their own detail stream, in case the
    /// connection drops. Every line carries both clocks: real time (UTC) and run time in seconds.
    /// Format version 1: {"v":1,"utc":"…","s":12.34,"m":{the message}}.
    /// </summary>
    public sealed class RunLog : IDisposable
    {
        public const int Version = 1;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        readonly StreamWriter writer;
        public readonly string FolderPath;
        public readonly DateTime StartedAt = DateTime.Now;
        public readonly List<(double Seconds, Dictionary<string, object> Message)> Entries =
            new List<(double, Dictionary<string, object>)>();

        public RunLog(string root, string fileName)
        {
            FolderPath = Path.Combine(root, StartedAt.ToString("yyyy-MM-dd_HHmmss"));
            Directory.CreateDirectory(FolderPath);
            writer = new StreamWriter(Path.Combine(FolderPath, fileName), append: true, new UTF8Encoding(false)) { NewLine = "\n" };
        }

        RunLog(string folder, DateTime startedAt)
        {
            FolderPath = folder;
            StartedAt = startedAt;
        }

        /// <summary>A finished run's log, read back from its folder (no writing), to rebuild its report.</summary>
        public static RunLog Load(string folder)
        {
            var started = DateTime.TryParseExact(Path.GetFileName(folder.TrimEnd('\\', '/')), "yyyy-MM-dd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var named)
                ? named : Directory.GetCreationTime(folder);
            var log = new RunLog(folder, started);
            foreach (var line in File.ReadLines(Path.Combine(folder, "events.jsonl")))
            {
                if (line.Trim().Length == 0)
                    continue;
                var entry = Json.Deserialize<Dictionary<string, object>>(line);
                log.Entries.Add((Convert.ToDouble(entry["s"]), (Dictionary<string, object>)entry["m"]));
            }
            return log;
        }

        public static string Root(bool sim) => sim
            ? Path.Combine(Path.GetTempPath(), "GroupRun-sim")
            : Path.Combine(AppContext.BaseDirectory, "runs");

        public void Append(double seconds, Dictionary<string, object> message)
        {
            Entries.Add((seconds, message));
            writer.WriteLine(Json.Serialize(new Dictionary<string, object>
            {
                ["v"] = Version,
                ["utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["s"] = Math.Round(seconds, 2),
                ["m"] = message,
            }));
            writer.Flush();
        }

        public void Dispose() => writer?.Dispose();
    }

    /// <summary>One runner's detail records from a run log, sorted by run time, ready for the report.</summary>
    public sealed class RunnerHistory
    {
        public string Id;
        public string Name;
        public readonly List<Dictionary<string, object>> Records = new List<Dictionary<string, object>>();

        public IEnumerable<Dictionary<string, object>> Of(string kind) => Records.Where(r => (r["k"] as string) == kind);

        public static double Seconds(Dictionary<string, object> record) => Convert.ToDouble(record["s"]);

        /// <summary>Everyone's detail records, grouped by runner.</summary>
        public static List<RunnerHistory> From(RunLog log)
        {
            var byId = new Dictionary<string, RunnerHistory>();
            foreach (var (_, message) in log.Entries)
            {
                if ((message["t"] as string) != "detail")
                    continue;
                var id = (string)message["id"];
                if (!byId.TryGetValue(id, out var history))
                    byId[id] = history = new RunnerHistory { Id = id };
                history.Name = (string)message["name"];
                history.Records.AddRange(((System.Collections.IEnumerable)message["ev"]).Cast<Dictionary<string, object>>());
            }
            foreach (var h in byId.Values)
            {
                var sorted = h.Records.OrderBy(Seconds).ToList(); // stable: a load, its area and its zone keep their order
                h.Records.Clear();
                h.Records.AddRange(sorted);
            }
            return byId.Values.ToList();
        }
    }
}
