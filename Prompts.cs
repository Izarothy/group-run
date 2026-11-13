using System;
using System.Collections.Generic;
using System.Linq;

namespace GroupRun
{
    /// <summary>The campaign towns, where vendors and the stash are.</summary>
    public static class Towns
    {
        static readonly HashSet<string> Names = new HashSet<string>
        {
            "Lioneye's Watch", "The Forest Encampment", "The Sarn Encampment", "Highgate",
            "Overseer's Tower", "The Bridge Encampment", "Oriath Docks", "Oriath",
        };

        public static bool Contains(string zone) => zone != null && Names.Contains(zone);
    }

    /// <summary>
    /// Admin: the calls the whole group hears on the way through the campaign, each once a run.
    /// <list type="bullet">
    /// <item>"check res": a runner enters a town after anyone has been in The Lunaris Temple Level 2 (Act 3;
    /// the Act 8 one comes later and the call is used up by then).</item>
    /// <item>"&lt;name&gt; weaver": anyone first enters The Forest Encampment; the highest level then.</item>
    /// <item>"&lt;name&gt; vaal ruins": 15 s after everyone has entered The Wetlands; the highest level.</item>
    /// <item>"&lt;name&gt; pushes oversoul": 15 s after everyone has entered The Northern Forest; the first runner
    /// to reach level 18 (said as soon as someone does, if nobody has yet).</item>
    /// <item>"&lt;name&gt; pushes daresso": 15 s after everyone has entered The Dried Lake; the highest level other
    /// than the admin.</item>
    /// </list>
    /// "Everyone" is the runners online now; the highest level breaks ties on the XP bar.
    /// </summary>
    public sealed class GroupCalls
    {
        public const string Temple = "The Lunaris Temple Level 2";
        static readonly TimeSpan Delay = TimeSpan.FromSeconds(15);

        readonly Dictionary<string, string> zoneOf = new Dictionary<string, string>();                    // runner id: last zone seen
        readonly Dictionary<string, HashSet<string>> entered = new Dictionary<string, HashSet<string>>(); // zone: runner ids that entered it
        readonly Dictionary<string, DateTime> allInAt = new Dictionary<string, DateTime>();             // zone: when the last online runner entered it
        readonly HashSet<string> said = new HashSet<string>();
        bool templeSeen;
        string firstTo18;   // runner id

        public void Reset()
        {
            zoneOf.Clear();
            entered.Clear();
            allInAt.Clear();
            said.Clear();
            templeSeen = false;
            firstTo18 = null;
        }

        /// <summary>The lines to say now, in order.</summary>
        /// <param name="name">A runner's name as the voice should say it.</param>
        public List<string> Update(IEnumerable<Runner> runners, string adminId, DateTime now, Func<Runner, string> name)
        {
            var lines = new List<string>();
            void Say(string call, Func<string> line)
            {
                if (said.Contains(call) || !(line() is string text))
                    return;
                said.Add(call);
                lines.Add(text);
            }
            string Named(Runner runner, string rest) => runner == null ? null : name(runner) + " " + rest;

            var online = runners.Where(r => !r.IsStale(now)).ToList();
            var townEntered = false;
            foreach (var runner in online)
            {
                zoneOf.TryGetValue(runner.Id, out var before);
                zoneOf[runner.Id] = runner.Zone;
                if (runner.Zone == before || string.IsNullOrEmpty(runner.Zone))
                    continue;
                if (!entered.TryGetValue(runner.Zone, out var who))
                    entered[runner.Zone] = who = new HashSet<string>();
                who.Add(runner.Id);
                if (runner.Zone == Temple)
                    templeSeen = true;
                else if (templeSeen && Towns.Contains(runner.Zone))
                    townEntered = true;
            }
            if (firstTo18 == null)
                firstTo18 = Highest(online.Where(r => r.Level >= 18))?.Id;

            if (townEntered)
                Say("res", () => "check res");
            if (entered.ContainsKey("The Forest Encampment"))
                Say("weaver", () => Named(Highest(online), "weaver"));
            if (AllInFor("The Wetlands", online, now))
                Say("vaal ruins", () => Named(Highest(online), "vaal ruins"));
            if (AllInFor("The Northern Forest", online, now))
                Say("oversoul", () => Named(online.FirstOrDefault(r => r.Id == firstTo18), "pushes oversoul"));
            if (AllInFor("The Dried Lake", online, now))
                Say("daresso", () => Named(Highest(online.Where(r => r.Id != adminId)), "pushes daresso"));
            return lines;
        }

        /// <summary>Every online runner has entered <paramref name="zone"/>, the last of them at least 15 s ago.</summary>
        bool AllInFor(string zone, List<Runner> online, DateTime now)
        {
            if (!allInAt.ContainsKey(zone))
            {
                if (online.Count == 0 || !entered.TryGetValue(zone, out var who) || !online.All(r => who.Contains(r.Id)))
                    return false;
                allInAt[zone] = now;
            }
            return now - allInAt[zone] >= Delay;
        }

        static Runner Highest(IEnumerable<Runner> runners) =>
            runners.OrderByDescending(r => r.Level).ThenByDescending(r => r.Xp ?? 0).FirstOrDefault();
    }

    /// <summary>
    /// A runner's own vendor reminders, in the first town entered after reaching each level. Levels passed
    /// between two towns give one call, the latest. A level back under the last one is a new character.
    /// </summary>
    public sealed class GamblePrompts
    {
        static readonly (int Level, string Line)[] Steps =
        {
            (3, "gamble axe"), (7, "gamble sword"), (11, "gamble sword"), (24, "gamble 4links"), (28, "gamble axe"),
        };

        int next;           // the first step not called yet
        int lastLevel;
        string lastZone;

        /// <summary>The line to say now, or null.</summary>
        public string Update(string zone, int level)
        {
            if (level < lastLevel)
                next = 0;
            lastLevel = level;
            var entered = zone != lastZone;
            lastZone = zone;
            if (!entered || !Towns.Contains(zone))
                return null;
            string line = null;
            while (next < Steps.Length && level >= Steps[next].Level)
                line = Steps[next++].Line;
            return line;
        }
    }
}
