using System;
using System.Collections.Generic;
using System.Linq;

namespace GroupRun
{
    /// <summary>
    /// The run's key moments, found from where everyone went: pushes (the first entry into an act's gate zones),
    /// chain starts (a boss chain's owner entering its approach zone) and swirls (two or more others popping into
    /// a boss zone for a short visit). The owner is the first runner who stayed in the boss zone to fight.
    /// </summary>
    public static class Moments
    {
        public sealed class Moment
        {
            public double Seconds;
            public string Kind;  // push, chain, swirl
            public string Label;
            public string Owner;
            public string Zone;
        }

        /// <summary>
        /// A pacing check: by the moment <see cref="At"/> (a zone first entered by anyone, or a boss swirl), the
        /// <see cref="Count"/> best levelled runners should be at least <see cref="Need"/>.
        /// </summary>
        public sealed class Gate
        {
            public string Label;
            public string At;
            public bool Swirl;
            public int Count;
            public double Need;
        }

        public static readonly Gate[] Gates =
        {
            new Gate { Label = "Act 2 town", At = "The Forest Encampment", Count = 2, Need = 12 },
            new Gate { Label = "Vaal Ruins", At = "The Vaal Ruins", Count = 2, Need = 15 },
            new Gate { Label = "Caverns", At = "The Caverns", Count = 1, Need = 18 },
            new Gate { Label = "Act 3", At = "The City of Sarn", Count = 1, Need = 20 },
            new Gate { Label = "Piety call", At = "Crematorium", Swirl = true, Count = 2, Need = 21 },
            new Gate { Label = "Battlefront", At = "The Battlefront", Count = 2, Need = 23 },
            new Gate { Label = "Ebony", At = "The Ebony Barracks", Count = 1, Need = 25 },
            new Gate { Label = "Dominus", At = "Dominus", Swirl = true, Count = 2, Need = 27 },
            new Gate { Label = "Act 4", At = "The Aqueduct", Count = 2, Need = 28 },
        };

        /// <summary>When a gate's moment happened, or null when the run never got there.</summary>
        public static double? When(Gate gate, List<RunnerHistory> histories, List<Moment> moments) => gate.Swirl
            ? moments.FirstOrDefault(m => m.Kind == "swirl" && m.Label == gate.At)?.Seconds
            : histories.SelectMany(h => h.Of("zone")).Where(r => (string)r["zone"] == gate.At)
                .Select(RunnerHistory.Seconds).Cast<double?>().DefaultIfEmpty(null).Min();

        static readonly (string Zone, string Label)[] Pushes =
        {
            ("The Lower Prison", "Lower Prison"), ("The Southern Forest", "Act 2"), ("The Vaal Ruins", "Vaal Ruins"),
            ("The City of Sarn", "Act 3"), ("The Docks", "Docks"), ("The Ebony Barracks", "Ebony"),
            ("The Aqueduct", "Act 4"), ("The Crystal Veins", "Crystal Veins"), ("The Belly of the Beast Level 1", "Belly"),
            ("The Ascent", "Act 5"), ("The Chamber of Innocence", "Innocence"),
        };

        // boss or job zone -> (name, the zone its chain starts from)
        static readonly (string Zone, string Label, string Approach)[] Bosses =
        {
            ("The Upper Prison", "Brutus", "The Lower Prison"),
            ("The Flooded Depths", "Dweller", null),
            ("The Cavern of Anger", "Merveil", "The Cavern of Wrath"),
            ("The Chamber of Sins Level 2", "Fidelitas", "The Chamber of Sins Level 1"),
            ("The Weaver's Chambers", "Weaver", "The Western Forest"),
            ("The Broken Bridge", "Kraityn", null),
            ("The Wetlands", "Oak", null),
            ("The Western Forest", "Alira", null),
            ("The Crypt Level 1", "Crypt trial", null),
            ("The Ancient Pyramid", "Oversoul", "The Caverns"),
            ("The Crematorium", "Crematorium", null),
            ("The Catacombs", "Catacombs trial", null),
            ("The Solaris Temple Level 2", "Solaris", "The Solaris Temple Level 1"),
            ("The Lunaris Temple Level 2", "Piety", "The Lunaris Temple Level 1"),
            ("The Imperial Gardens", "Gardens trial", null),
            ("The Upper Sceptre of God", "Dominus", "The Sceptre of God"),
            ("The Grand Arena", "Daresso", "Daresso's Dream"),
            ("Kaom's Stronghold", "Kaom", "Kaom's Dream"),
            ("The Dried Lake", "Voll", null),
            ("The Harvest", "Malachai", "The Belly of the Beast Level 1"),
            ("The Chamber of Innocence", "Innocence", "The Templar Courts"),
            ("The Ossuary", "Ossuary", null),
            ("The Reliquary", "Reliquary", null),
            ("The Cathedral Rooftop", "Kitava", null),
        };

        const double FightAtLeast = 20; // a stay this long in a boss zone is the fight, not a farm-through
        const double SwirlAtMost = 45;
        const double SwirlTogether = 40;

        static List<(double Start, string Zone, double Stay)> Visits(RunnerHistory h)
        {
            var zones = h.Of("zone").Select(r => (Start: RunnerHistory.Seconds(r), Zone: (string)r["zone"])).ToList();
            return zones.Select((z, i) => (z.Start, z.Zone, (i + 1 < zones.Count ? zones[i + 1].Start : z.Start + 30) - z.Start)).ToList();
        }

        public static List<Moment> Find(List<RunnerHistory> histories)
        {
            var visits = histories.ToDictionary(h => h.Name, Visits);
            var stays = visits.SelectMany(v => v.Value.Select(s => (s.Start, Runner: v.Key, s.Zone, s.Stay)))
                .GroupBy(s => s.Zone)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Start).ToList());
            var moments = new List<Moment>();

            foreach (var (zone, label) in Pushes)
                if (stays.TryGetValue(zone, out var entries))
                    moments.Add(new Moment { Seconds = entries[0].Start, Kind = "push", Label = label, Zone = zone });

            foreach (var (zone, label, approach) in Bosses)
            {
                if (!stays.TryGetValue(zone, out var entries))
                    continue;
                var fight = entries.FirstOrDefault(s => s.Stay >= FightAtLeast);
                var owner = fight.Runner ?? entries[0].Runner;
                if (approach != null && visits[owner].Any(v => v.Zone == approach))
                    moments.Add(new Moment { Seconds = visits[owner].First(v => v.Zone == approach).Start, Kind = "chain", Label = label, Owner = owner, Zone = approach });
                var others = entries.Where(s => s.Runner != owner && s.Stay <= SwirlAtMost).ToList();
                foreach (var first in others)
                {
                    var group = others.Where(s => s.Start >= first.Start && s.Start - first.Start <= SwirlTogether);
                    if (group.Select(s => s.Runner).Distinct().Count() < 2)
                        continue;
                    moments.Add(new Moment { Seconds = first.Start, Kind = "swirl", Label = label, Owner = owner, Zone = zone });
                    break; // later swirls into the same zone are re-entries
                }
            }
            return moments.OrderBy(m => m.Seconds).ToList();
        }

        /// <summary>Monster level of each zone, from the area line the game writes just before entering it.</summary>
        public static Dictionary<string, int> ZoneLevels(List<RunnerHistory> histories)
        {
            var levels = new Dictionary<string, int>();
            foreach (var h in histories)
            {
                int? last = null;
                foreach (var r in h.Records)
                {
                    var kind = r["k"] as string;
                    if (kind == "area")
                    {
                        var area = ((r["area"] as string) ?? "").ToLowerInvariant();
                        last = area.Contains("town") || area.Contains("hideout") ? (int?)null : Convert.ToInt32(r["alvl"]);
                    }
                    else if (kind == "zone" && last is int level && !levels.ContainsKey((string)r["zone"]))
                        levels[(string)r["zone"]] = level;
                }
            }
            return levels;
        }
    }
}
