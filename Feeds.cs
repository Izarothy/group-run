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

        /// <summary>The lines this runner typed in party chat since the last call. Never part of the run history.</summary>
        List<string> TakePartyChat();
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
            ["game"] = new Info { Title = "Path of Exile isn't running", Hint = "", Short = "no game", Warning = false },
            ["log-quiet"] = new Info { Title = "Client.txt has loads but no level-up or zone lines", Hint = "", Short = "log quiet", Warning = true },
        };
    }

    /// <summary>
    /// The real thing: everything from Client.txt. Level comes from the log's level-ups only; nothing is read
    /// from the screen. Which character is this runner's needs no setup: level 2 is taken on the Twilight Strand,
    /// always solo, so that name is ours; before then (the app started mid-run) the first level-up seen is taken.
    /// </summary>
    public sealed class GameFeed : IGameFeed
    {
        static readonly TimeSpan GameCheckEvery = TimeSpan.FromSeconds(2);

        readonly GameLog log;
        readonly Settings settings;
        DateTime lastGameCheck = DateTime.MinValue;
        bool gameRunning;
        string me;              // this runner's character, once known
        bool starting;          // the countdown to a new run (not a resume) is on
        List<string> partyChat = new List<string>();
        List<GameEvent> earlier; // the log before the app started, until the first update has used it
        int loadsWithoutMessage; // areas generated since the log last wrote a message line (zone, level-up, chat)
        const int QuietAfterLoads = 3; // some clients stop writing message lines but keep writing loads
        bool LogQuiet => loadsWithoutMessage >= QuietAfterLoads;

        public GameFeed(Settings settings)
        {
            this.settings = settings;
            me = settings.CharacterName.Trim().Length > 0 ? settings.CharacterName.Trim() : null;
            log = new GameLog(settings.ClientLogPath, me);
            earlier = log.ReadEarlier(4 << 20);
        }

        public string Problem =>
            !log.Exists ? "log"
            : !gameRunning ? "game"
            : LogQuiet ? "log-quiet"
            : "";

        public List<string> TakePartyChat()
        {
            var lines = partyChat;
            partyChat = new List<string>();
            return lines;
        }

        public void Update(Runner self, Run run, DateTime now, List<Dictionary<string, object>> detail)
        {
            if (now - lastGameCheck > GameCheckEvery)
            {
                lastGameCheck = now;
                gameRunning = Game.IsRunning();
            }
            var countdown = run.Phase == Phase.Countdown && !run.Resuming; // a replay after reconnecting is past it already
            if (countdown && !starting)
                NewCharacter(self, "countdown", detail); // a new run, often on a fresh character; a 5 on an old one overrides the 1 at its next level-up
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
                NoteLogHealth(e, detail);
                if (e.Kind == GameEventKind.PartyChat)
                {
                    if (string.Equals(e.Text, me, StringComparison.OrdinalIgnoreCase) && e.Said.Length > 0)
                        partyChat.Add(e.Said);
                    continue; // what was said stays out of the run's records
                }
                switch (e.Kind)
                {
                    case GameEventKind.LevelUp:
                        // names aren't stored: "who" says how the level-up was taken
                        // level 2 happens on the Twilight Strand, always solo: that name is ours, a fresh character if it's a new one
                        if (e.Level == 2 && settings.CharacterName.Trim().Length == 0)
                        {
                            if (me != null && !string.Equals(e.Text, me, StringComparison.OrdinalIgnoreCase))
                                NewCharacter(self, "level 2 under a new name", detail);
                            if (me == null) record["who"] = "level 2: solo, so ours";
                            me = e.Text;
                        }
                        if (me == null)
                        {
                            me = e.Text; // nothing to check against: the first one is ours
                            record["who"] = "first seen, so ours";
                        }
                        if (!string.Equals(e.Text, me, StringComparison.OrdinalIgnoreCase))
                        {
                            // a party member's: once we know who we are, never someone else (only a new character asks again)
                            record["who"] = "other";
                            record["to"] = e.Level;
                            detail.Add(record);
                            continue;
                        }
                        self.SetLevel(e.Level, now);
                        record["lvl"] = e.Level;
                        break;
                    case GameEventKind.Death:
                        if (!string.Equals(e.Text, me, StringComparison.OrdinalIgnoreCase)) continue; // someone else's, or ours before we know who we are
                        self.Deaths++;
                        break;
                    case GameEventKind.Zone:
                        if (e.Text == "The Twilight Strand" && self.Zone != e.Text)
                            NewCharacter(self, "twilight strand", detail); // every character starts there
                        if (e.Text != self.Zone) self.ZoneSince = now;
                        self.Zone = e.Text;
                        record["zone"] = e.Text;
                        break;
                    case GameEventKind.Area:
                        self.Area = e.Text;
                        self.Seed = e.Seed;
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
        }

        /// <summary>Count a fresh character from scratch, and find out its name again from its level-ups.</summary>
        void NewCharacter(Runner self, string reason, List<Dictionary<string, object>> detail)
        {
            var forget = settings.CharacterName.Trim().Length == 0;
            detail.Add(new Dictionary<string, object>
            {
                ["k"] = "character",
                ["why"] = reason,
                ["from"] = self.Level,
                ["name"] = forget ? (me == null ? "still unknown" : "forgotten, found again at the next level-up") : "set in settings",
            });
            if (forget) me = null;
            self.Level = 1;
            self.Xp = null;
            self.Deaths = 0;
        }

        /// <summary>
        /// Some clients keep writing loads ("Generating level ...") but stop writing message lines: no zones, no
        /// level-ups, so the level sticks. Recorded when it starts and stops.
        /// </summary>
        void NoteLogHealth(GameEvent e, List<Dictionary<string, object>> detail)
        {
            var wasQuiet = LogQuiet;
            if (e.Kind == GameEventKind.Area) loadsWithoutMessage++;
            else if (e.Kind != GameEventKind.Load) loadsWithoutMessage = 0;
            if (LogQuiet != wasQuiet)
                detail.Add(new Dictionary<string, object>
                {
                    ["k"] = "log",
                    ["why"] = LogQuiet ? $"quiet: {QuietAfterLoads} loads with no zone, level-up or chat line" : "messages back",
                });
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

        List<string> partyChat = new List<string>();

        (string Name, string Area, long Seed)? map; // the self-test put this runner somewhere outside the campaign

        /// <summary>The self-test's way to move this runner into a map or hideout instance.</summary>
        public void Enter(string zone, string area, long seed) => map = (zone, area, seed);

        /// <summary>The self-test's way to type a line in this runner's party chat.</summary>
        public void Type(string line) => partyChat.Add(line);

        public List<string> TakePartyChat()
        {
            var lines = partyChat;
            partyChat = new List<string>();
            return lines;
        }

        public void Update(Runner self, Run run, DateTime now, List<Dictionary<string, object>> detail)
        {
            var progress = 1 + pace * Math.Pow(run.Elapsed.TotalMinutes, 0.8);
            if ((int)progress > self.Level)
                detail.Add(new Dictionary<string, object> { ["k"] = "levelup", ["lvl"] = (int)progress });
            self.SetLevel((int)progress, now);
            self.Xp = progress - (int)progress;

            if (map is (string name, string area, long seed))
            {
                if (name != self.Zone || seed != self.Seed)
                    self.ZoneSince = now;
                (self.Zone, self.Area, self.Seed) = (name, area, seed);
                return;
            }
            var live = run.Elapsed > TimeSpan.Zero; // from the first moment of the run, through pauses
            var zone = live ? Zones[Math.Min(Zones.Length - 1, (int)((progress - 1) * 2.2))] : default;
            if (live && zone.Name != self.Zone)
            {
                self.ZoneSince = now;
                self.Area = zone.Area;
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
