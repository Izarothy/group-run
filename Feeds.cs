using System;
using System.Collections.Generic;
using System.Linq;

namespace GroupRun
{
    /// <summary>Where this runner's own numbers come from: the game, or a simulation.</summary>
    public interface IGameFeed
    {
        /// <summary>
        /// Bring <paramref name="self"/> up to date (level, XP, zone) and add a record to <paramref name="detail"/>
        /// for everything observed, for the run history. Records carry their own fields; the window stamps the time.
        /// </summary>
        void Update(Runner self, Run run, DateTime now, List<Dictionary<string, object>> detail);

        /// <summary>What stops this runner's numbers from being trusted (a <see cref="Problems"/> code), or "".</summary>
        string Problem { get; }
    }

    /// <summary>The things that can be wrong with a runner's setup: what the board says, and how to fix it.</summary>
    public static class Problems
    {
        public sealed class Info
        {
            public string Title;
            public string Hint;
            public string Short;    // on the runner's row, on everyone's board
            public bool Warning;    // false: expected for now (the game isn't open yet)
        }

        public static readonly Dictionary<string, Info> All = new Dictionary<string, Info>
        {
            ["log"] = new Info { Title = "Client.txt not found", Hint = "Settings", Short = "no log", Warning = true },
            ["xp-setup"] = new Info { Title = "XP bar not set up", Hint = "Settings", Short = "no XP", Warning = true },
            ["xp-off"] = new Info { Title = "XP bar reading looks off", Hint = "Settings", Short = "check XP", Warning = true },
            ["game"] = new Info { Title = "Path of Exile isn't running", Hint = "", Short = "no game", Warning = false },
        };
    }

    /// <summary>
    /// The real thing: Client.txt for levels, zones and the rest, the XP bar on screen every 2 s. A bar reading
    /// is only trusted when the game is in front and not on a loading screen; otherwise the record says why
    /// there's no reading. The calibration is checked as the run goes: right after a level-up the bar must be
    /// nearly empty, and within a level it never goes backwards unless the character died.
    /// Which character is this runner's needs no setup: party members' level-ups appear in the same log, so the
    /// first level-up followed by this runner's own XP bar dropping to nearly empty names the character. Without
    /// a calibrated bar the first level-up seen is taken (at the start everyone levels in their own instance).
    /// </summary>
    public sealed class GameFeed : IGameFeed
    {
        static readonly TimeSpan XpReadEvery = TimeSpan.FromSeconds(2);
        const int StrikesForProblem = 3;

        readonly GameLog log;
        readonly Settings settings;
        DateTime lastXpRead = DateTime.MinValue;
        bool loading;
        bool expectEmptyBar;    // the first trusted reading after a level-up
        double? lastBar;
        int lastBarLevel;
        readonly Queue<DateTime> strikes = new Queue<DateTime>(); // readings that contradicted the calibration, recently
        static readonly TimeSpan StrikeWindow = TimeSpan.FromMinutes(2);
        int calibrationVersion; // a new calibration clears the strikes
        DateTime barSeenAt = DateTime.Now; // the last reading, or the last time none was expected (tabbed out, loading, character select)
        static readonly TimeSpan BlindAtMost = TimeSpan.FromSeconds(90); // longer than the passive tree stays open
        bool gameRunning;
        bool inZone;            // entered a zone since starting: past character select
        DateTime? diedAt;       // the death screen darkens the bar: no readings until we're back
        double? beforeDeath;    // the bar just before dying
        static readonly TimeSpan DeathScreenAtMost = TimeSpan.FromSeconds(60);
        string me;              // this runner's character, once known
        readonly List<(string Name, int Level, DateTime At)> pending = new List<(string, int, DateTime)>(); // level-ups waiting for the bar to say whose
        static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(10);
        bool starting;          // the countdown to a new run (not a resume) is on
        List<GameEvent> earlier; // the log before the app started, until the first update has used it

        public GameFeed(Settings settings)
        {
            this.settings = settings;
            me = settings.CharacterName.Trim().Length > 0 ? settings.CharacterName.Trim() : null;
            log = new GameLog(settings.ClientLogPath, me);
            earlier = log.ReadEarlier(4 << 20);
        }

        public string Problem =>
            !log.Exists ? "log"
            : !settings.Xp.IsSet ? "xp-setup"
            : !gameRunning ? "game"
            : RecentStrikes() >= StrikesForProblem || DateTime.Now - barSeenAt > BlindAtMost ? "xp-off"
            : "";

        public void Update(Runner self, Run run, DateTime now, List<Dictionary<string, object>> detail)
        {
            if (settings.Xp.GetHashCode() != calibrationVersion)
            {
                calibrationVersion = settings.Xp.GetHashCode();
                strikes.Clear();
                lastBar = null;
                barSeenAt = DateTime.Now;
            }
            var countdown = run.Phase == Phase.Countdown && !run.Resuming; // a replay after reconnecting is past it already
            if (countdown && !starting)
            {
                // a new run, often on a fresh character: find it again, and count its level and deaths from scratch;
                // the next level-up sets the real level (a 5 on an old character overrides the 1)
                if (settings.CharacterName.Trim().Length == 0) me = null;
                self.Level = 1;
                self.Xp = 0;
                self.Deaths = 0;
                lastBar = null;
                expectEmptyBar = false;
            }
            starting = countdown;
            if (earlier != null)
            {
                // started mid-session (a restart, a crash): where the character already is, until the log says more
                if (earlier.LastOrDefault(e => e.Kind == GameEventKind.Zone) is GameEvent zone)
                    self.Zone = zone.Text;
                if (me != null && earlier.LastOrDefault(e => e.Kind == GameEventKind.LevelUp && string.Equals(e.Text, me, StringComparison.OrdinalIgnoreCase)) is GameEvent level)
                    self.Level = level.Level;
                earlier = null;
            }

            foreach (var e in log.ReadNew())
            {
                var record = new Dictionary<string, object>
                {
                    ["k"] = e.Kind.ToString().ToLowerInvariant(),
                    ["logged"] = e.LoggedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["tick"] = e.Tick,
                };
                switch (e.Kind)
                {
                    case GameEventKind.LevelUp:
                        if (me == null)
                        {
                            if (!settings.Xp.IsSet) me = e.Text; // nothing to check against: the first one is ours
                            else { pending.Add((e.Text, e.Level, now)); continue; }
                        }
                        if (!string.Equals(e.Text, me, StringComparison.OrdinalIgnoreCase)) continue; // a party member's
                        self.SetLevel(e.Level, now);
                        self.Xp = 0; // the bar starts over: not the old level's fill on the new level until the next reading
                        expectEmptyBar = true;
                        record["lvl"] = e.Level;
                        break;
                    case GameEventKind.Death:
                        if (!string.Equals(e.Text, me, StringComparison.OrdinalIgnoreCase)) continue; // someone else's, or ours before we know who we are
                        self.Deaths++;
                        diedAt = now;
                        beforeDeath = lastBar;
                        lastBar = null; // XP can drop on death
                        break;
                    case GameEventKind.Load:
                        loading = true;
                        break;
                    case GameEventKind.Zone:
                        loading = false;
                        inZone = true;
                        diedAt = null; // a new zone after dying: resurrected in town
                        if (e.Text != self.Zone) self.ZoneSince = now;
                        self.Zone = e.Text;
                        record["zone"] = e.Text;
                        break;
                    case GameEventKind.Area:
                        record["area"] = e.Text;
                        record["alvl"] = e.Level;
                        record["seed"] = e.Seed;
                        break;
                    case GameEventKind.Joined:
                    case GameEventKind.Left:
                        record["player"] = e.Text;
                        break;
                    case GameEventKind.Afk:
                        record["afk"] = e.Text;
                        break;
                    case GameEventKind.System:
                        record["text"] = e.Text;
                        break;
                }
                detail.Add(record);
            }

            if (now - lastXpRead > XpReadEvery)
            {
                lastXpRead = now;
                detail.Add(ReadBar(self));
            }
        }

        Dictionary<string, object> ReadBar(Runner self)
        {
            var record = new Dictionary<string, object> { ["k"] = "xp", ["lvl"] = self.Level };
            gameRunning = XpBar.GameOrigin() != null;
            var front = gameRunning && XpBar.GameIsForeground();
            record["fg"] = front;
            var why = !settings.Xp.IsSet ? "uncalibrated" : !gameRunning ? "no game" : !front ? "background" : loading ? "loading" : "";
            if (why.Length > 0)
            {
                barSeenAt = DateTime.Now;
                record["bar"] = null;
                record["why"] = why;
                return record;
            }

            var learned = settings.Xp.Learned;
            var read = XpBar.Read(settings.Xp, mayLearn: inZone && diedAt == null); // the death screen's darkened bar isn't the bar
            if (read?.Peak is int peak) record["peak"] = peak;
            if (!learned && settings.Xp.Learned)
            {
                settings.Save(); // learned the bar's colours: keep them
                record["learned"] = true;
            }
            if (read == null || !read.Visible)
            {
                // character select, loading, a panel over the bar: not the bar, so no reading and no strike
                record["bar"] = null;
                record["why"] = "not visible";
                if (read != null) record["err"] = Math.Round(read.Error, 1);
                lastBar = null;
                if (!inZone) barSeenAt = DateTime.Now;
                return record;
            }
            barSeenAt = DateTime.Now;
            var bar = read.Fill;
            if (diedAt is DateTime died)
            {
                // back once the bar shows about what it did before dying (a death costs up to 15% from Act 6 on)
                var back = DateTime.Now - died > DeathScreenAtMost
                    || beforeDeath is double before && bar <= before + 0.01 && bar >= before - 0.15;
                if (!back)
                {
                    record["bar"] = null;
                    record["why"] = "dead";
                    return record;
                }
                diedAt = null;
            }
            record["bar"] = Math.Round(bar, 4);
            record["err"] = Math.Round(read.Error, 1);
            ConfirmPending(self, bar, record);
            if (expectEmptyBar)
            {
                expectEmptyBar = false;
                if (bar > 0.3) Strike(record, "high after level-up");
            }
            else if (lastBar.HasValue && lastBarLevel == self.Level && bar < lastBar.Value - 0.05)
                Strike(record, "went backwards");
            lastBar = bar;
            lastBarLevel = self.Level;
            self.Xp = bar;
            return record;
        }

        /// <summary>
        /// A level-up whose name isn't known yet is ours if our bar went from well along to nearly empty right after
        /// it, and no other name levelled in that time: a party member levelling while our bar happens to be low, or
        /// at the same moment as us, names nobody.
        /// </summary>
        void ConfirmPending(Runner self, double bar, Dictionary<string, object> record)
        {
            if (pending.Count == 0)
                return;
            var recent = pending.Where(p => DateTime.Now - p.At <= PendingFor).ToList();
            pending.Clear();
            // no reading before (the colours not learned yet, early on a fresh character): a low bar now is all there is
            var reset = bar <= 0.2 && (!(lastBar is double before) || before - bar > 0.5);
            if (!reset || recent.Select(p => p.Name).Distinct().Count() != 1)
                return; // too late to tell, our bar didn't reset, or two names levelled: wait for the next level-up
            var (name, level, _) = recent[recent.Count - 1];
            me = name;
            self.SetLevel(level, DateTime.Now);
            record["lvl"] = level;
            record["character"] = "recognised"; // the name itself isn't stored
        }

        int RecentStrikes()
        {
            while (strikes.Count > 0 && DateTime.Now - strikes.Peek() > StrikeWindow)
                strikes.Dequeue();
            return strikes.Count;
        }

        void Strike(Dictionary<string, object> record, string reason)
        {
            strikes.Enqueue(DateTime.Now);
            record["suspect"] = reason; // kept in the history, so the report can leave it out
        }
    }

    /// <summary>
    /// A fake runner for sim mode: levels up and moves through the Act 1-2 zones while the shared timer
    /// runs, each at its own pace (level 2 in about half a minute, level 10 around minute nine), and
    /// reports the same records the real feed does.
    /// </summary>
    public sealed class SimFeed : IGameFeed
    {
        static readonly (string Name, string Area, int Level)[] Zones =
        {
            ("The Twilight Strand", "1_1_1", 1), ("Lioneye's Watch", "1_1_town", 1), ("The Coast", "1_1_2", 2),
            ("The Mud Flats", "1_1_3", 3), ("The Submerged Passage", "1_1_4_0", 4), ("The Ledge", "1_1_5", 5),
            ("The Climb", "1_1_6", 6), ("The Lower Prison", "1_1_7_1", 7), ("The Upper Prison", "1_1_7_2", 8),
            ("Prisoner's Gate", "1_1_8", 9), ("The Ship Graveyard", "1_1_9", 10), ("The Cavern of Wrath", "1_1_11_1", 11),
            ("The Cavern of Anger", "1_1_11_2", 12), ("The Southern Forest", "1_2_1", 13), ("The Forest Encampment", "1_2_town", 13),
            ("The Riverways", "1_2_5_1", 15), ("The Western Forest", "1_2_2", 16), ("The Weaver's Chambers", "1_2_2a", 17),
            ("The Wetlands", "1_2_7", 18), ("The Vaal Ruins", "1_2_8", 19), ("The Northern Forest", "1_2_9", 20),
        };
        static readonly TimeSpan XpReadEvery = TimeSpan.FromSeconds(2);

        readonly double pace;
        DateTime lastXpRead = DateTime.MinValue;

        public SimFeed(int index) => pace = 1.6 - 0.11 * index + 0.06 * Math.Sin(index * 2.1);

        public string Problem => "";

        public void Update(Runner self, Run run, DateTime now, List<Dictionary<string, object>> detail)
        {
            var progress = 1 + pace * Math.Pow(run.Elapsed.TotalMinutes, 0.8);
            if ((int)progress > self.Level)
                detail.Add(new Dictionary<string, object> { ["k"] = "levelup", ["lvl"] = (int)progress });
            self.SetLevel((int)progress, now);
            self.Xp = progress - (int)progress;

            var live = run.Elapsed > TimeSpan.Zero; // from the first moment of the run, through pauses
            var zone = live ? Zones[Math.Min(Zones.Length - 1, (int)((progress - 1) * 2.2))] : default;
            if (live && zone.Name != self.Zone)
            {
                detail.Add(new Dictionary<string, object> { ["k"] = "load" });
                detail.Add(new Dictionary<string, object> { ["k"] = "area", ["area"] = zone.Area, ["alvl"] = zone.Level, ["seed"] = 0 });
                detail.Add(new Dictionary<string, object> { ["k"] = "zone", ["zone"] = zone.Name });
            }
            self.Zone = zone.Name ?? "";

            if (now - lastXpRead > XpReadEvery)
            {
                lastXpRead = now;
                detail.Add(new Dictionary<string, object>
                {
                    ["k"] = "xp", ["lvl"] = self.Level, ["bar"] = Math.Round(self.Xp.Value, 4), ["fg"] = true,
                });
            }
        }
    }
}
