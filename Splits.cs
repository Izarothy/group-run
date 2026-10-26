using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml.Linq;

namespace GroupRun
{
    /// <summary>
    /// The group's zone splits: a split's time is the first moment any runner entered its zone, so splits fill in
    /// out of order as runners spread over the act. The last split has no zone of its own; Finish sets it.
    /// Compared against the group's best run (PB) and the best time ever at each split (gold).
    /// </summary>
    public sealed class SplitList
    {
        public string[] Names = new string[0];
        public TimeSpan?[] Times = new TimeSpan?[0];
        public TimeSpan?[] Pb = new TimeSpan?[0];
        public TimeSpan?[] Gold = new TimeSpan?[0];

        public bool Any => Names.Length > 0;

        public void Define(string[] names, TimeSpan?[] pb, TimeSpan?[] gold)
        {
            var same = names.SequenceEqual(Names);
            Names = names;
            Pb = Fit(pb, names.Length);
            Gold = Fit(gold, names.Length);
            if (!same)
                Times = new TimeSpan?[names.Length];
        }

        static TimeSpan?[] Fit(TimeSpan?[] values, int length)
        {
            var fitted = new TimeSpan?[length];
            for (var i = 0; i < length && values != null && i < values.Length; i++)
                fitted[i] = values[i];
            return fitted;
        }

        /// <summary>Keep the earliest time reported for a split; true when it changed.</summary>
        public bool Mark(int index, TimeSpan time)
        {
            if (index < 0 || index >= Times.Length || Times[index] is TimeSpan known && known <= time)
                return false;
            Times[index] = time;
            return true;
        }

        public void Clear() => Times = new TimeSpan?[Names.Length];

        /// <summary>
        /// Splits whose zone this is (all but the last, which Finish sets), timed or not: a runner's batch can arrive
        /// after someone else's later entry, and <see cref="Mark"/> keeps the earliest. Acts 6-10 repeat zone names
        /// from Acts 1-5, so a split whose zone an earlier split also matches opens only once the split before it
        /// has a time: going back to The Coast in Act 1 doesn't time Act 6's.
        /// </summary>
        public IEnumerable<int> OpenFor(string zone) =>
            Enumerable.Range(0, Math.Max(0, Names.Length - 1)).Where(i => Matches(Names[i], zone)
                && (!Names.Take(i).Any(earlier => Matches(earlier, zone)) || Times[i - 1] != null));

        /// <summary>
        /// "Lower Prison" is The Lower Prison and "Belly Level 1" is The Belly of the Beast Level 1: every word of
        /// the split name, in order, among the zone's words.
        /// </summary>
        public static bool Matches(string split, string zone)
        {
            var want = Words(split);
            if (want.Length == 0)
                return false;
            var have = Words(zone);
            var at = 0;
            foreach (var word in have)
                if (at < want.Length && word == want[at])
                    at++;
            return at == want.Length;
        }

        static string[] Words(string text) =>
            text.ToLowerInvariant().Split(new[] { ' ', '\'', '\u2019', '-', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w != "the" && w != "of").ToArray();

        /// <summary>LiveSplit's subsplit marks: "-Lower Prison" inside a group, "{Act 1}Merveil" ending one.</summary>
        static readonly Regex Subsplit = new Regex(@"^\s*(-|\{[^}]*\})");

        /// <summary>The segment names of a LiveSplit splits file, without subsplit marks.</summary>
        public static string[] NamesFromLss(string path) =>
            XDocument.Load(path).Descendants("Segment").Select(s => Subsplit.Replace((string)s.Element("Name") ?? "", "").Trim())
                .Where(n => n.Length > 0).ToArray();
    }

    /// <summary>The group's PB and gold times for one split list, kept on the admin's PC (splits.json next to the exe).</summary>
    public static class SplitRecords
    {
        sealed class Stored
        {
            public string[] Names = new string[0];
            public long?[] Pb = new long?[0];
            public long?[] Gold = new long?[0];
        }

        static string FilePath => Path.Combine(AppContext.BaseDirectory, "splits.json");

        /// <summary>PB and gold for these names, or empty when none were recorded for exactly this list.</summary>
        public static (TimeSpan?[] Pb, TimeSpan?[] Gold) Load(string[] names)
        {
            var stored = Read().FirstOrDefault(s => s.Names.SequenceEqual(names));
            return stored == null
                ? (new TimeSpan?[names.Length], new TimeSpan?[names.Length])
                : (stored.Pb.Select(ToSpan).ToArray(), stored.Gold.Select(ToSpan).ToArray());
        }

        /// <summary>After a finished run, save it: a faster final time becomes the PB, and any faster split a gold.</summary>
        public static void Record(SplitList splits)
        {
            var count = splits.Names.Length;
            if (count == 0 || splits.Times[count - 1] == null)
                return;
            var final = splits.Times[count - 1].Value;
            var newPb = splits.Pb[count - 1] is TimeSpan pbFinal ? final < pbFinal : true;
            var pb = newPb ? splits.Times.ToArray() : splits.Pb.ToArray();
            var gold = splits.Gold.Select((g, i) => Min(g, splits.Times[i])).ToArray();

            var all = Read().Where(s => !s.Names.SequenceEqual(splits.Names)).ToList();
            all.Add(new Stored { Names = splits.Names, Pb = pb.Select(ToMs).ToArray(), Gold = gold.Select(ToMs).ToArray() });
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(all));
            // the finished board keeps comparing against the old PB; the next run loads the new one
        }

        static List<Stored> Read()
        {
            try
            {
                return File.Exists(FilePath)
                    ? (new JavaScriptSerializer().Deserialize<List<Stored>>(File.ReadAllText(FilePath)) ?? new List<Stored>())
                        .Where(s => s != null && s.Names != null && s.Pb != null && s.Gold != null).ToList()
                    : new List<Stored>();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException)
            {
                return new List<Stored>(); // unreadable records start over rather than stop the app
            }
        }

        static TimeSpan? Min(TimeSpan? a, TimeSpan? b) => a == null ? b : b == null ? a : (a < b ? a : b);
        static TimeSpan? ToSpan(long? ms) => ms.HasValue ? TimeSpan.FromMilliseconds(ms.Value) : (TimeSpan?)null;
        public static long? ToMs(TimeSpan? t) => t.HasValue ? (long)t.Value.TotalMilliseconds : (long?)null;
        public static TimeSpan?[] FromMs(IEnumerable<object> values) =>
            values.Select(v => v == null ? (TimeSpan?)null : TimeSpan.FromMilliseconds(Convert.ToDouble(v))).ToArray();
    }
}
