using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GroupRun
{
    /// <summary>
    /// The app window: the board with its control strip, all drawn by <see cref="PanelView"/>. Every runner
    /// sends their own level and XP, answers the ready check (and can take it back, which cancels the
    /// countdown) and can pause the run; the admin also starts, finishes and resets it. When everyone is
    /// ready the admin's copy starts a spoken countdown. Right-click the board or the tray icon for overlay
    /// mode (board only, for OBS), voice, XP bar calibration, settings and updates.
    /// </summary>
    public sealed class MainForm : Form
    {
        static readonly TimeSpan XpUpdateEvery = TimeSpan.FromSeconds(3);
        static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);
        static readonly TimeSpan NoteShown = TimeSpan.FromSeconds(6);
        static readonly TimeSpan AutoStartHoldoff = TimeSpan.FromSeconds(2); // after a "not ready", let everyone's state settle
        static readonly TimeSpan DetailEvery = TimeSpan.FromSeconds(2);
        static readonly TimeSpan ReportAfterFinish = TimeSpan.FromSeconds(5); // the last detail batches are still arriving

        readonly Settings settings;
        readonly IRelay relay;
        readonly IGameFeed feed;
        readonly bool sim;
        readonly Run run = new Run();
        readonly Runner self;   // this runner on the board: what the others were last sent, so every board agrees
        readonly Runner local;  // this runner as the feed reads it, moment to moment
        readonly Chrome chrome = new Chrome();
        readonly Timer tick = new Timer { Interval = 100 };
        readonly Voice voice = new Voice();
        ChatMacro macro;
        readonly NotifyIcon tray;
        readonly List<Dictionary<string, object>> detail = new List<Dictionary<string, object>>();
        DateTime lastDetailSent = DateTime.MinValue;
        bool wasConnected;
        int connectionsSeen;
        RunLog groupLog;   // admin only: every message from everyone, for the run history
        RunLog ownLog;     // this runner's own detail records, kept even if the connection drops
        DateTime? reportDue;
        string hostProblem = "";
        List<PanelButton> buttons = new List<PanelButton>();
        DateTime lastSent = DateTime.MinValue;
        string lastSentKey = "";
        double? lastSentXp;
        string note = "";
        DateTime noteAt;
        Phase lastPhase = Phase.Waiting;
        string lastCheck = "";
        int lastCount;
        Version newestSeen = Updater.Current;
        Version declined;       // turned down this session: the quiet checks don't ask again
        readonly Timer updateCheck = new Timer { Interval = 60 * 60 * 1000 };

        /// <param name="sim">Sim mode: settings, calibration and updates are off, and runs are saved to a temp folder.</param>
        /// <param name="primary">Speaks and has the tray icon; the sim makes only one window primary.</param>
        public MainForm(Settings settings, IRelay relay, IGameFeed feed, bool sim = false, bool primary = true)
        {
            this.settings = settings;
            this.relay = relay;
            this.feed = feed;
            this.sim = sim;
            self = run.Upsert(settings.RunnerId, settings.DisplayName);
            self.LastSeen = DateTime.Now;
            local = new Runner { Id = self.Id, Name = self.Name };
            chrome.IsAdmin = settings.IsAdmin;
            chrome.GroupCode = settings.IsAdmin ? "" : "Waiting for the admin to start";
            chrome.ShowControls = !settings.Overlay;

            Text = sim ? $"{settings.DisplayName}{(settings.IsAdmin ? " · admin" : "")} · sim" : "Group Run";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            DoubleBuffered = true;
            BackColor = PanelView.Background;
            Icon = AppIcon.Create();
            voice.Enabled = primary && settings.Voice;
            ContextMenuStrip = BuildMenu();
            if (primary)
                tray = new NotifyIcon { Icon = Icon, Text = Text, ContextMenuStrip = ContextMenuStrip, Visible = true };
            FitToBoard();

            tick.Tick += (s, e) => Tick();
            if (!sim)
            {
                updateCheck.Tick += async (s, e) => await CheckForUpdates(quiet: true);
                updateCheck.Start();
                Shown += async (s, e) => await CheckForUpdates(quiet: true);
            }
            tick.Start();
            feed.Update(local, run, DateTime.Now, new List<Dictionary<string, object>>());
            SendSelf(force: true);
            if (settings.IsAdmin)
                PublishSplits();
            if (!sim)
            {
                macro = new ChatMacro(settings.MacroKey);
                macro.Pressed += Callout;
            }
        }

        DateTime? calledIn;     // the zone entry (ZoneSince) of the last callout; none yet

        /// <summary>
        /// The callout key. The first press in a zone is "&lt;name&gt; next"; after that, "setup trial" right after
        /// entering a zone with a trial to set up (within 15 s of The Lower Prison or The Crematorium, anywhere in
        /// The Marketplace), otherwise "swirl &lt;name&gt;". Typed in party chat for the game and sent to the group
        /// to be spoken.
        /// </summary>
        void Callout()
        {
            var justEntered = DateTime.Now - local.ZoneSince <= TimeSpan.FromSeconds(15);
            var trial = justEntered && (local.Zone == "The Lower Prison" || local.Zone == "The Crematorium")
                || local.Zone == "The Marketplace";
            var first = calledIn != local.ZoneSince;
            calledIn = local.ZoneSince;
            var text = first ? settings.DisplayName + " next" : trial ? "setup trial" : "swirl " + settings.DisplayName;
            if (!sim)
                ChatMacro.Type("%" + text); // the sim has no game to type into
            relay.Publish(new Dictionary<string, object> { ["t"] = "say", ["from"] = self.Id, ["text"] = text });
        }

        /// <summary>Admin: the split list from the .lss in Settings, with the group's PB and golds, to everyone.</summary>
        void PublishSplits()
        {
            string[] names;
            try
            {
                names = settings.SplitsFile.Length > 0 ? SplitList.NamesFromLss(settings.SplitsFile) : new string[0];
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is System.Xml.XmlException)
            {
                names = new string[0];
                ShowNote("Can't read the splits file");
            }
            var (pb, gold) = SplitRecords.Load(names);
            relay.Publish(new Dictionary<string, object>
            {
                ["t"] = "splits", ["names"] = names,
                ["pb"] = pb.Select(SplitRecords.ToMs).ToArray(), ["gold"] = gold.Select(SplitRecords.ToMs).ToArray(),
            });
        }

        /// <summary>Admin: a runner's zone entries (from their detail) time the splits for those zones.</summary>
        void TimeSplits(Dictionary<string, object> body)
        {
            if (!settings.IsAdmin || run.Phase == Phase.Waiting || !run.Splits.Any)
                return; // batches sent before Finish or during a resume countdown still count
            if (!(body.TryGetValue("ev", out var ev) && ev is System.Collections.IEnumerable records))
                return;
            foreach (var record in records.OfType<Dictionary<string, object>>())
            {
                if (Get(record, "k") != "zone" || !record.TryGetValue("s", out var s))
                    continue;
                var at = TimeSpan.FromSeconds(Convert.ToDouble(s));
                foreach (var index in run.Splits.OpenFor(Get(record, "zone")).ToList())
                    PublishSplit(index, at);
            }
        }

        void PublishSplit(int index, TimeSpan at)
        {
            if (run.Splits.Mark(index, at))
                relay.Publish(new Dictionary<string, object> { ["t"] = "split", ["i"] = index, ["ms"] = (long)at.TotalMilliseconds });
        }

        /// <summary>Tries hosting again (the tunnel); clicking the "can't reach you" notice runs it.</summary>
        public Action HostRetry;

        /// <summary>The admin's join code, for Settings to show and copy, and what still blocks runners, if anything.</summary>
        public void ShowJoinCode(string code, string problem)
        {
            hostedCode = code;
            hostProblem = problem;
        }

        string hostedCode = "";

        /// <summary>The folder this run's history goes to, once a run has started.</summary>
        public string RunFolder => (groupLog ?? ownLog)?.FolderPath;

        /// <summary>The board as a picture, as the window would draw it.</summary>
        public Bitmap Render()
        {
            var bitmap = new Bitmap(PanelView.Width, PanelView.HeightFor(run, chrome));
            using (var g = Graphics.FromImage(bitmap))
                PanelView.Draw(g, run, DateTime.Now, self.Id, chrome);
            return bitmap;
        }

        /// <summary>One line of state for the self-test transcript.</summary>
        public string Describe() =>
            $"{settings.DisplayName} {run.Phase}{(run.Phase == Phase.Countdown ? " " + run.CountdownLeft.TotalSeconds.ToString("0.0") : "")}"
            + $" {PanelView.FormatTime(run.Elapsed)} sees {run.Runners.Count}";

        float DpiScale => DeviceDpi / 96f;

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new UiMenuRenderer(), Font = Ui.Body, BackColor = Ui.Surface, ForeColor = Ui.Fg };
            menu.Items.Add("Settings", null, (s, e) => OpenSettings());
            menu.Items.Add($"Check for updates (v{Updater.Current})", null, async (s, e) => await CheckForUpdates()).Enabled = !sim;
            menu.Items.Add("Past runs", null, (s, e) => OpenPastRuns());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Close());
            return menu;
        }

        void Tick()
        {
            var now = DateTime.Now;
            while (relay.Inbox.TryDequeue(out var message))
            {
                try
                {
                    Receive(message, now);
                }
                catch (Exception e) when (e is KeyNotFoundException || e is FormatException || e is InvalidCastException
                    || e is OverflowException || e is ArgumentException)
                {
                    // a message this version can't read (a field missing or of another type, say from another app
                    // version): skip it. Thrown, it would stop this tick on every board, again on every replay.
                }
            }

            KeepHistory(now); // before reading the feed, so the first zone of a run lands in its log
            var fresh = new List<Dictionary<string, object>>();
            feed.Update(local, run, now, fresh);
            if ((relay.Link == Link.Connected) != wasConnected)
            {
                wasConnected = relay.Link == Link.Connected;
                fresh.Add(new Dictionary<string, object> { ["k"] = "conn", ["up"] = wasConnected });
            }
            CollectDetail(fresh, now);
            self.LastSeen = now;
            var connections = relay.Connections;
            SendSelf(force: connections != connectionsSeen); // just (re)connected: what was sent while offline was lost
            connectionsSeen = connections;
            if (settings.IsAdmin)
                StartWhenEveryoneIsReady(now);
            KeepHistory(now);
            Announce();
            var onTop = settings.OnTop == "always"
                || settings.OnTop == "running" && run.Phase != Phase.Waiting && run.Phase != Phase.Finished;
            if (TopMost != onTop)
                TopMost = onTop;

            chrome.Notice = CurrentNotice(now);
            if (!settings.IsAdmin)
                chrome.GroupCode = relay.Link == Link.Connected ? "Waiting for the admin to start" : "";
            FitToBoard();
            Invalidate();
        }

        /// <summary>The most important thing to tell this runner right now, or null.</summary>
        Notice CurrentNotice(DateTime now)
        {
            if (Problems.All.TryGetValue(feed.Problem, out var problem))
                return new Notice { Title = problem.Title, Hint = problem.Hint, Warning = problem.Warning };
            if (hostProblem.Length > 0 && run.Phase == Phase.Waiting)
                return new Notice { Title = "Runners can't reach you", Hint = hostProblem, Warning = true };
            var retry = Math.Max(1, (int)Math.Ceiling((relay.RetryAt - now).TotalSeconds));
            switch (relay.Link)
            {
                case Link.NoCode:
                    return new Notice { Title = "No join code", Hint = "Settings", Warning = true };
                case Link.Connecting:
                    return new Notice { Title = "Connecting to the admin…", Hint = settings.GroupCode.Split('/')[0], Warning = false };
                case Link.Retrying:
                    return new Notice { Title = "Can't reach the admin", Hint = $"Retrying in {retry} s", Warning = false };
                case Link.Lost:
                    return new Notice { Title = "Lost the admin", Hint = $"Reconnecting in {retry} s", Warning = true };
                case Link.Denied:
                    return new Notice { Title = "Join code rejected", Hint = "", Warning = true };
            }
            if (now - noteAt < NoteShown)
                return new Notice { Title = note, Hint = "", Warning = false };
            return null;
        }

        void Receive(Message message, DateTime now)
        {
            var body = message.Body;
            groupLog?.Append(Math.Max(0, run.Elapsed.TotalSeconds - message.Age.TotalSeconds), body);
            switch (Get(body, "t"))
            {
                case "runner":
                    var id = Get(body, "id");
                    if (id == self.Id) return;
                    var runner = run.Upsert(id, Get(body, "name"));
                    runner.SetLevel(Convert.ToInt32(body["level"]), now);
                    runner.Xp = body.TryGetValue("xp", out var xp) && xp != null ? Convert.ToDouble(xp) : (double?)null;
                    runner.Deaths = Convert.ToInt32(body["deaths"]);
                    runner.Zone = Get(body, "zone");
                    runner.ReadyFor = Get(body, "ready");
                    runner.Problem = Get(body, "problem");
                    runner.LastSeen = now - message.Age;
                    if (Updater.TryParse(Get(body, "ver"), out var version) && version > newestSeen)
                        NewerVersionSeen(version, runner.Name);
                    break;
                case "detail":
                    TimeSplits(body);
                    break;
                case "splits":
                    run.Splits.Define(((System.Collections.IEnumerable)body["names"]).Cast<object>().Select(n => n.ToString()).ToArray(),
                        SplitRecords.FromMs(((System.Collections.IEnumerable)body["pb"]).Cast<object>()),
                        SplitRecords.FromMs(((System.Collections.IEnumerable)body["gold"]).Cast<object>()));
                    FitToBoard();
                    break;
                case "say":
                    voice.Say(Get(body, "text"));
                    ShowNote(run.NameOf(Get(body, "from")) + ": " + Get(body, "text"));
                    break;
                case "split":
                    run.Splits.Mark(Convert.ToInt32(body["i"]), TimeSpan.FromMilliseconds(Convert.ToDouble(body["ms"])));
                    break;
                case "control": // our own too, as the hub ordered it: every board applies the same sequence
                    run.Apply(Get(body, "action"), TimeSpan.FromMilliseconds(Convert.ToDouble(body["elapsedMs"])), message.Age,
                        Get(body, "check"), Get(body, "from"));
                    break;
            }
        }

        static string Get(Dictionary<string, object> body, string key) => body.TryGetValue(key, out var v) && v != null ? v.ToString() : "";

        /// <summary>What a click on one of the board's buttons does; the self-test presses them directly.</summary>
        public void Press(string action)
        {
            switch (action)
            {
                case "ready":
                    local.ReadyFor = run.ReadyCheck;
                    SendSelf(force: true);
                    break;
                case "unready":
                    local.ReadyFor = "";
                    SendSelf(force: true);
                    if (run.Phase == Phase.Countdown)
                        SendControl("cancel");
                    break;
                case "readycheck":
                    SendControl(action, Guid.NewGuid().ToString("N").Substring(0, 8));
                    break;
                case "finish":
                    if (run.Splits.Any)
                        PublishSplit(run.Splits.Names.Length - 1, run.Elapsed); // the last split is the finish
                    SendControl(action);
                    if (!sim)
                    {
                        try
                        {
                            if (run.Splits.Any)
                                SplitRecords.Record(run.Splits);
                            ShowNote("saved to " + System.IO.Path.GetFileName(History.Save(run)));
                        }
                        catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
                        {
                            ShowNote("Can't save the run"); // runs.csv open in Excel, a read-only folder
                        }
                    }
                    break;
                case "reset":
                    SendControl(action);
                    if (settings.IsAdmin)
                        PublishSplits(); // the next run compares against the PB and golds just saved
                    break;
                case "callout":
                    Callout();
                    return;
                case "settings":
                    if (hostProblem.Length > 0 && HostRetry != null && run.Phase == Phase.Waiting)
                        HostRetry(); // the notice on screen is the hosting one: fix that, not settings
                    else
                        OpenSettings();
                    return;
                case "report":
                    if (groupLog != null)
                        Report.Save(run, groupLog, open: true);
                    break;
                default:
                    SendControl(action);
                    break;
            }
            Tick();
        }

        void ShowNote(string text)
        {
            note = text;
            noteAt = DateTime.Now;
        }

        void SendControl(string action, string check = "")
        {
            relay.Publish(new Dictionary<string, object>
            {
                ["t"] = "control", ["from"] = self.Id, ["action"] = action,
                ["elapsedMs"] = (long)run.Elapsed.TotalMilliseconds, ["check"] = check,
            });
        }

        /// <summary>Send this runner's state: right away when something discrete changed, otherwise only
        /// when the XP moved and the last update is old enough, plus a heartbeat so others see we're online.</summary>
        void SendSelf(bool force)
        {
            var now = DateTime.Now;
            var key = $"{local.Level}|{local.Deaths}|{local.Zone}|{local.ReadyFor}|{settings.DisplayName}|{feed.Problem}";
            var xpMoved = local.Xp.HasValue && (!lastSentXp.HasValue || Math.Abs(local.Xp.Value - lastSentXp.Value) >= 0.002);
            var due = force || key != lastSentKey && now - lastSent > TimeSpan.FromSeconds(1)
                || xpMoved && now - lastSent > XpUpdateEvery
                || now - lastSent > HeartbeatEvery;
            if (!due)
                return;
            lastSent = now;
            lastSentKey = key;
            lastSentXp = local.Xp;
            self.SetLevel(local.Level, now);
            self.Xp = local.Xp;
            self.Deaths = local.Deaths;
            self.Zone = local.Zone;
            self.ReadyFor = local.ReadyFor;
            self.Problem = feed.Problem;
            relay.Publish(new Dictionary<string, object>
            {
                ["t"] = "runner", ["id"] = self.Id, ["name"] = settings.DisplayName, ["level"] = local.Level,
                ["xp"] = local.Xp.HasValue ? Math.Round(local.Xp.Value, 4) : (object)null,
                ["deaths"] = local.Deaths, ["zone"] = local.Zone, ["ready"] = local.ReadyFor,
                ["problem"] = feed.Problem, ["ver"] = Updater.Current.ToString(),
            });
        }

        /// <summary>
        /// Stamp what the feed saw with both clocks and, while a run is on, keep it in this runner's own log and
        /// send it to the admin every 2 s. Outside a run the records are dropped.
        /// </summary>
        void CollectDetail(List<Dictionary<string, object>> fresh, DateTime now)
        {
            if (ownLog == null)
                return;
            if (run.Phase == Phase.Finished)
                fresh.Clear(); // the run is over: only what is still waiting goes out
            foreach (var record in fresh)
            {
                record["s"] = Math.Round(run.Elapsed.TotalSeconds, 2);
                record["utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                ownLog.Append(run.Elapsed.TotalSeconds, record);
                detail.Add(record);
            }
            if (detail.Count == 0 || now - lastDetailSent < DetailEvery)
                return;
            lastDetailSent = now;
            relay.Publish(new Dictionary<string, object>
            {
                ["t"] = "detail", ["id"] = self.Id, ["name"] = settings.DisplayName, ["ev"] = detail.ToList(),
            });
            detail.Clear();
        }

        /// <summary>
        /// A run's history starts when it goes live (a cancelled countdown leaves nothing behind) and ends when
        /// the admin resets for a new run. The report is written a few seconds after Finish.
        /// </summary>
        void KeepHistory(DateTime now)
        {
            var phase = run.Phase;
            if (phase == Phase.Running && ownLog == null)
                BeginRunLogs();
            if (phase == Phase.Waiting && ownLog != null)
                EndRunLogs();
            if (phase == Phase.Finished && lastPhase != Phase.Finished && groupLog != null)
                reportDue = now + ReportAfterFinish;
            if (reportDue is DateTime due && now >= due)
            {
                reportDue = null;
                Report.Save(run, groupLog, open: false);
                ShowNote("run saved");
            }
        }

        /// <summary>A fresh run: open its folder. The admin's log starts with who is in the run.</summary>
        void BeginRunLogs()
        {
            EndRunLogs();
            var root = RunLog.Root(sim);
            try
            {
                ownLog = new RunLog(root, SafeFileName(settings.DisplayName) + ".jsonl");
                if (!settings.IsAdmin)
                    return;
                groupLog = new RunLog(root, "events.jsonl");
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                ShowNote("Can't save the run"); // the board goes on; the next tick tries again
                return;
            }
            groupLog.Append(0, new Dictionary<string, object>
            {
                ["t"] = "run", ["app"] = Updater.Current.ToString(), ["admin"] = self.Id,
                ["runners"] = run.Runners.Values.Select(r => new Dictionary<string, object> { ["id"] = r.Id, ["name"] = r.Name }).ToList(),
            });
        }

        void EndRunLogs()
        {
            ownLog?.Dispose();
            ownLog = null;
            groupLog?.Dispose();
            groupLog = null;
            reportDue = null;
            detail.Clear();
        }

        static string SafeFileName(string name) =>
            new string(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        void OpenPastRuns()
        {
            var root = RunLog.Root(sim);
            System.IO.Directory.CreateDirectory(root);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + root + "\"");
        }

        /// <summary>The admin's copy starts the countdown once every runner still online has said they're ready.</summary>
        void StartWhenEveryoneIsReady(DateTime now)
        {
            if (run.Phase != Phase.Waiting || run.ReadyCheck.Length == 0 || run.SinceCancel < AutoStartHoldoff)
                return;
            var online = run.Runners.Values.Where(r => !r.IsStale(now)).ToList();
            if (online.Count > 0 && online.All(run.IsReady))
                SendControl("countdown");
        }

        /// <summary>Say what just happened to the run: the countdown, go, who paused, who isn't ready.</summary>
        void Announce()
        {
            var phase = run.Phase;
            var count = phase == Phase.Countdown ? (int)Math.Ceiling(run.CountdownLeft.TotalSeconds) : 0;
            if (phase == Phase.Countdown && count != lastCount && count > 0)
                voice.Say(count.ToString());
            else if (phase != lastPhase)
            {
                if (lastPhase == Phase.Countdown && phase == Phase.Running)
                    voice.Say("Go!");
                else if (lastPhase == Phase.Countdown || run.SinceCancel < TimeSpan.FromSeconds(1))
                    voice.Say($"{Spoken(run.CancelledBy)} is not ready");
                else if (phase == Phase.Paused)
                    voice.Say(run.PausedBy == self.Id ? "Paused" : $"Paused by {Spoken(run.PausedBy)}");
                else if (phase == Phase.Finished)
                    voice.Say("Run finished");
            }
            else if (phase == Phase.Waiting && run.ReadyCheck.Length > 0 && run.ReadyCheck != lastCheck)
                voice.Say("Ready check");
            lastPhase = phase;
            lastCount = count;
            lastCheck = run.ReadyCheck;
        }

        /// <summary>A runner's name as the voice should read it: "r3" becomes "R 3".</summary>
        string Spoken(string id) => Regex.Replace(run.NameOf(id), @"(?<=\D)(?=\d)", " ");

        void NewerVersionSeen(Version version, string who)
        {
            newestSeen = version;
            if (sim || tray == null)
                return;
            tray.ShowBalloonTip(8000, "Group Run update available",
                $"{who} has v{version}, you have v{Updater.Current}.", ToolTipIcon.Info);
        }

        /// <summary>From the menu: say what was found either way.</summary>
        async Task CheckForUpdates() => await CheckForUpdates(quiet: false);

        /// <summary>
        /// Look for a newer release and offer it. Quiet (the startup and hourly checks): say nothing unless there is
        /// one, never while a run is on, and don't ask again about a version already turned down.
        /// </summary>
        async Task CheckForUpdates(bool quiet)
        {
            if (quiet && (sim || run.Phase == Phase.Countdown || run.Phase == Phase.Running || run.Phase == Phase.Paused))
                return;
            Release release;
            try
            {
                release = await Updater.LatestAsync();
            }
            catch (Exception e)
            {
                if (!quiet) Ui.Tell(this, "Update check failed: " + e.GetBaseException().Message);
                return;
            }
            if (release == null || release.Version <= Updater.Current)
            {
                if (!quiet) Ui.Tell(this, $"Up to date (v{Updater.Current}).");
                return;
            }
            if (quiet && release.Version == declined)
                return;
            if (quiet && (run.Phase == Phase.Countdown || run.Phase == Phase.Running || run.Phase == Phase.Paused))
                return; // a run started while checking
            if (!Ui.Tell(this, $"v{Updater.Current} → v{release.Version}", "Update", "Later"))
            {
                declined = release.Version;
                return;
            }
            if (run.Phase == Phase.Countdown || run.Phase == Phase.Running || run.Phase == Phase.Paused)
                return; // the run started while the question was up: restarting now would drop out of it
            try
            {
                await Updater.InstallAsync(release);
                Close();
            }
            catch (Exception e)
            {
                Ui.Tell(this, "Update failed: " + e.GetBaseException().Message);
            }
        }

        void OpenSettings()
        {
            var before = (settings.DisplayName, settings.ClientLogPath, settings.GroupCode, settings.IsAdmin);
            var splitsBefore = settings.SplitsFile;
            using (var form = new SetupForm(settings, settings.IsAdmin && hostedCode.Contains("/") ? hostedCode : ""))
            {
                if (form.ShowDialog(this) != DialogResult.OK)
                    return;
            }
            voice.Enabled = settings.Voice;
            chrome.ShowControls = !settings.Overlay;
            FitToBoard();
            if (sim)
                return;
            if (!settings.Save())
                Ui.Tell(this, Settings.CantSave);
            if (settings.IsAdmin && settings.SplitsFile != splitsBefore && (run.Phase == Phase.Waiting || run.Phase == Phase.Finished))
                PublishSplits();
            if ((settings.DisplayName, settings.ClientLogPath, settings.GroupCode, settings.IsAdmin) != before)
                Ui.Tell(this, "Restart to apply.");
        }

        void FitToBoard()
        {
            var size = new Size((int)Math.Ceiling(PanelView.Width * DpiScale), (int)Math.Ceiling(PanelView.HeightFor(run, chrome) * DpiScale));
            if (ClientSize != size)
                ClientSize = size;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.ScaleTransform(DpiScale, DpiScale);
            buttons = PanelView.Draw(e.Graphics, run, DateTime.Now, self.Id, chrome);
        }

        PanelButton ButtonAt(Point p) => buttons.FirstOrDefault(b => b.Bounds.Contains(p.X / DpiScale, p.Y / DpiScale));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = ButtonAt(e.Location) != null ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        DateTime lastClick = DateTime.MinValue;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            // the second click of a double-click would land on whatever button replaced the first (Ready → Not ready)
            var now = DateTime.Now;
            if (e.Button == MouseButtons.Left && ButtonAt(e.Location) is PanelButton button
                && (now - lastClick).TotalMilliseconds > SystemInformation.DoubleClickTime)
            {
                lastClick = now;
                Press(button.Action);
            }
            base.OnMouseUp(e);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            var dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE: a dark title bar to match the board
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            base.OnHandleCreated(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop();
            updateCheck.Stop();
            EndRunLogs();
            relay.Dispose();
            voice.Dispose();
            macro?.Dispose();
            if (tray != null)
                tray.Visible = false;
            base.OnFormClosed(e);
        }
    }
}
