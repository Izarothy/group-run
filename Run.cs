using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace GroupRun
{
    public sealed class Runner
    {
        public string Id;
        public string Name;
        public int Level = 1;
        public double? Xp;          // 0-1, null while the bar isn't readable
        public int Deaths;
        public string Zone = "";
        public string ReadyFor = ""; // id of the ready check this runner answered
        public string Problem = "";  // a Problems code: their setup can't be trusted yet
        public DateTime LastSeen;   // local time of the last update
        public int Slot;            // join order; picks the runner's colour

        // for the panel's short-lived highlights
        public DateTime LevelUpAt = DateTime.MinValue;

        public bool IsStale(DateTime now) => (now - LastSeen).TotalSeconds > 20;

        public void SetLevel(int level, DateTime now)
        {
            if (level > Level)
                LevelUpAt = now;
            Level = level;
        }
    }

    public enum Phase { Waiting, Countdown, Running, Paused, Finished }

    /// <summary>The run as everyone sees it: the shared timer, the ready check and the runners.</summary>
    public sealed class Run
    {
        public static readonly TimeSpan StartCountdown = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ResumeCountdown = TimeSpan.FromSeconds(3);
        static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2); // a "not ready" sent just before zero is still on its way

        static readonly Stopwatch Uptime = Stopwatch.StartNew();

        readonly Func<TimeSpan> clock;
        TimeSpan banked = TimeSpan.Zero;
        TimeSpan runningSince;
        TimeSpan countdownEnds;
        Phase phase = Phase.Waiting;

        /// <param name="clock">Monotonic time source; the demo recorder passes its own simulated clock.</param>
        public Run(Func<TimeSpan> clock = null) => this.clock = clock ?? (() => Uptime.Elapsed);

        /// <summary>The run's phase. A countdown turns into Running by itself when it reaches zero, on every
        /// copy of the run at once, so no "go" message is needed.</summary>
        public Phase Phase
        {
            get
            {
                if (phase == Phase.Countdown && clock() >= countdownEnds)
                {
                    phase = Phase.Running;
                    runningSince = countdownEnds;
                    PausedBy = "";
                }
                return phase;
            }
        }

        public string ReadyCheck { get; private set; } = "";
        public string PausedBy { get; private set; } = "";      // runner id of whoever paused
        public string CancelledBy { get; private set; } = "";   // runner id of whoever stopped the last countdown
        public TimeSpan? CancelledAt { get; private set; }
        public bool Resuming { get; private set; }               // the countdown is back from a pause, not the start
        public readonly Dictionary<string, Runner> Runners = new Dictionary<string, Runner>();
        public readonly SplitList Splits = new SplitList();

        public TimeSpan Elapsed => banked + (Phase == Phase.Running ? clock() - runningSince : TimeSpan.Zero);
        public TimeSpan CountdownLeft => Phase == Phase.Countdown ? countdownEnds - clock() : TimeSpan.Zero;
        public TimeSpan SinceCancel => CancelledAt is TimeSpan at ? clock() - at : TimeSpan.MaxValue;


        public bool IsReady(Runner runner) => ReadyCheck.Length > 0 && runner.ReadyFor == ReadyCheck;

        public string NameOf(string id) => Runners.TryGetValue(id, out var r) ? r.Name : "someone";

        /// <summary>Apply an action from anyone in the group. <paramref name="elapsed"/> is the run time when it
        /// was sent and <paramref name="age"/> how long ago that was, so every copy of the timer agrees;
        /// <paramref name="by"/> is the runner who sent it. Every copy applies the same actions in the hub's order,
        /// so an action that no longer fits the phase (a second pause, a resume after Finish) is dropped everywhere.</summary>
        public void Apply(string action, TimeSpan elapsed, TimeSpan age, string check = "", string by = "")
        {
            var current = Phase;
            switch (action)
            {
                case "readycheck":
                    ReadyCheck = check;
                    break;
                case "countdown":
                    if (current != Phase.Waiting && current != Phase.Paused)
                        break;
                    Resuming = current == Phase.Paused;
                    banked = elapsed;
                    countdownEnds = clock() + (Resuming ? ResumeCountdown : StartCountdown) - age;
                    phase = Phase.Countdown;
                    break;
                case "cancel":
                    var justStarted = current == Phase.Running && runningSince == countdownEnds && clock() - age - countdownEnds < CancelGrace;
                    if (current != Phase.Countdown && !justStarted)
                        break;
                    phase = Resuming ? Phase.Paused : Phase.Waiting;
                    CancelledBy = by;
                    CancelledAt = clock();
                    break;
                case "start":
                case "resume":
                    banked = elapsed + age;
                    runningSince = clock();
                    phase = Phase.Running;
                    ReadyCheck = "";
                    PausedBy = "";
                    break;
                case "pause":
                    if (current != Phase.Running)
                        break;
                    banked = elapsed;
                    phase = Phase.Paused;
                    PausedBy = by;
                    break;
                case "finish":
                    if (current == Phase.Waiting || current == Phase.Finished)
                        break;
                    banked = elapsed;
                    phase = Phase.Finished;
                    break;
                case "reset":
                    Splits.Clear();
                    banked = TimeSpan.Zero;
                    phase = Phase.Waiting;
                    ReadyCheck = "";
                    PausedBy = "";
                    break;
            }
        }

        public Runner Upsert(string id, string name)
        {
            if (!Runners.TryGetValue(id, out var runner))
                Runners[id] = runner = new Runner { Id = id, Slot = Runners.Count };
            runner.Name = name;
            return runner;
        }

        /// <summary>Furthest first: level plus the bar, so 12.80 is ahead of 12.40. An unread bar counts as the level's
        /// start; ties go by name, so every board shows the same order.</summary>
        public IEnumerable<Runner> InOrder => Runners.Values
            .OrderByDescending(r => r.Level + Math.Min(r.Xp ?? 0, 0.999))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }
}
