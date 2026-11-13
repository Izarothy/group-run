using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace GroupRun
{
    /// <summary>How long a map takes the group, boss included: the chain plans rounds by it.</summary>
    public enum Bucket { None, Rush, Mid, Long }

    /// <summary>
    /// The group's map sorting: map name to bucket. Anyone can sort the map they're in; every board applies the
    /// same "bucket" messages in hub order, and the admin keeps the table in maps.json and publishes it whole.
    /// </summary>
    public sealed class MapBuckets
    {
        static string FilePath => Path.Combine(AppContext.BaseDirectory, "maps.json");

        readonly Dictionary<string, Bucket> buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A map, not a hideout, town or campaign zone: its generated area id starts with "Map".</summary>
        public static bool IsMap(string area) => area.StartsWith("Map", StringComparison.Ordinal);

        public Bucket Of(string map) => buckets.TryGetValue(map, out var b) ? b : Bucket.None;

        public void Set(string map, Bucket bucket)
        {
            if (map.Length == 0) return;
            if (bucket == Bucket.None) buckets.Remove(map);
            else buckets[map] = bucket;
        }

        public static string Name(Bucket bucket) => bucket == Bucket.None ? "" : bucket.ToString().ToLowerInvariant();

        public static Bucket Parse(string name) =>
            Enum.TryParse(name, ignoreCase: true, out Bucket b) ? b : Bucket.None;

        public Dictionary<string, object> ToMessage() => new Dictionary<string, object>
        {
            ["t"] = "maps",
            ["buckets"] = buckets.ToDictionary(p => p.Key, p => (object)Name(p.Value)),
        };

        /// <summary>The whole table, as the admin published it: replaces what this board had.</summary>
        public void Replace(Dictionary<string, object> table)
        {
            buckets.Clear();
            foreach (var p in table)
                Set(p.Key, Parse(p.Value?.ToString() ?? ""));
        }

        public static MapBuckets Load()
        {
            var maps = new MapBuckets();
            try
            {
                if (File.Exists(FilePath))
                    maps.Replace(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath)));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException)
            {
                // unreadable: start unsorted, the group sorts again as it plays
            }
            return maps;
        }

        public void Save()
        {
            var table = buckets.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => Name(p.Value));
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(table));
        }
    }
}
