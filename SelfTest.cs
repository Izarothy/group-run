using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace GroupRun
{
    /// <summary>
    /// GroupRun.exe --selftest out-folder: the sim without anything on screen. Three hidden windows on a real
    /// hub over localhost play through a scripted run (ready check, a "not ready" that cancels the countdown,
    /// a pause by a runner, finish) and the boards are saved as PNGs at each step, next to a log of what
    /// happened and the run folder with its logs and report.
    /// </summary>
    public static class SelfTest
    {
        public static void Run(string output)
        {
            Directory.CreateDirectory(output);
            var transcript = new List<string>();
            var hub = new Hub(0, "selftest");
            var forms = new List<MainForm>();
            var feeds = new List<SimFeed>();
            transcript.AddRange(Checks(output));
            var splits = Path.Combine(output, "splits.lss"); // zones the sim reaches in its first minute
            File.WriteAllText(splits, "<Run><Segments>" + string.Concat(new[] { "Coast", "Mud Flats", "Submerged Passage", "Lower Prison", "Kitava kill" }
                .Select(n => $"<Segment><Name>{n}</Name></Segment>")) + "</Segments></Run>");
            for (var i = 0; i < 3; i++)
            {
                var settings = new Settings { DisplayName = "r" + (i + 1), RunnerId = "test" + i, IsAdmin = i == 0, Voice = false, OnTop = "never", SplitsFile = i == 0 ? splits : "" };
                IRelay relay = i == 0
                    ? (IRelay)new HubRelay(hub)
                    : new WsRelay(new JoinCode { Host = "127.0.0.1:" + hub.Port, Secret = "selftest" });
                feeds.Add(new SimFeed(i));
                forms.Add(new MainForm(settings, relay, feeds[i], sim: true, primary: false));
            }
            var admin = forms[0];

            // seconds from the start, and what happens then
            var script = new List<(double At, string Step, Action Act)>
            {
                (3, "joined", null),
                (4, "admin ready check", () => admin.Press("readycheck")),
                (5, "all ready", () => forms.ForEach(f => f.Press("ready"))),
                (7, "r3 not ready", () => forms[2].Press("unready")),
                (8, "after cancel", null),
                (10, "r3 ready again", () => forms[2].Press("ready")),
                (12, "countdown", null),
                (40, "r2 pauses", () => forms[1].Press("pause")),
                (42, "paused", null),
                (44, "r2 resumes", () => forms[1].Press("countdown")),
                (46, "resume countdown", null),
                (50, "r2 callout", () => forms[1].Press("callout")),
                (51, "callout heard", null),
                (52, "r2 callout again", () => forms[1].Press("callout")),
                (53, "second callout heard", null),
                (54, "admin chat", () => admin.Chat("kitava next, pack up")),
                (54.5, "chat heard", () => SaveWindow(admin, Path.Combine(output, "admin-window.png"))),
                (54.8, "admin party chat", () => feeds[0].Type("tp to town")),
                (55.3, "party chat heard", null),
                (55.5, "admin callout", () => admin.Press("callout")),
                (55.8, "callout shows in party chat", () => feeds[0].Type(admin.LastSaid)),
                (56.3, "not said twice", null),
                (56.5, "r2 party chat", () => feeds[1].Type("not the admin")),
                (57, "only the admin's is said", null),
                (57.5, "admin finishes", () => admin.Press("finish")),
                (64, "finished", null),
                (65, "admin opens a strand", () => feeds[0].Enter("Strand", "MapWorldsStrand", 11)),
                (66, "unsorted called", null),
                (66.5, "r2 joins the strand", () => feeds[1].Enter("Strand", "MapWorldsStrand", 11)),
                (67, "r2 sorts it long", () => forms[1].SortMap(Bucket.Long)),
                (67.5, "admin to hideout", () => feeds[0].Enter("Hideout", "HideoutFelled", 12)),
                (68.5, "admin opens another strand", () => feeds[0].Enter("Strand", "MapWorldsStrand", 13)),
                (69.5, "long called", null),
                (70, "admin to hideout again", () => feeds[0].Enter("Hideout", "HideoutFelled", 12)),
                (71, "portal back into the same strand", () => feeds[0].Enter("Strand", "MapWorldsStrand", 13)),
                (72.5, "not called again", null),
            };

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var next = 0;
            var timer = new Timer { Interval = 50 };
            timer.Tick += (s, e) =>
            {
                if (next == script.Count)
                {
                    timer.Stop();
                    Finish(output, forms, transcript);
                    Application.ExitThread();
                    return;
                }
                var (at, step, act) = script[next];
                if (clock.Elapsed.TotalSeconds < at)
                    return;
                next++;
                act?.Invoke();
                transcript.Add($"{at,5:0.0}s  {step}: " + string.Join(" | ", forms.Select(f => f.Describe())));
                Snapshot(forms, Path.Combine(output, $"{next:00}-{step.Replace(' ', '-')}.png"));
            };
            timer.Start();
            Application.Run(new ApplicationContext());
        }

        /// <summary>The voice prompts and the party chat parser, without windows: one line each, "ok" or what went wrong.</summary>
        static List<string> Checks(string output)
        {
            var lines = new List<string>();
            void Check(string what, object got, object want) =>
                lines.Add($"check {what}: " + (Equals(got, want) ? "ok" : $"FAILED, got \"{got}\", want \"{want}\""));

            var calls = new GroupCalls();
            var now = new DateTime(2026, 11, 13, 20, 0, 0);
            var me = new Runner { Id = "me", Name = "me", Level = 10 };
            var a = new Runner { Id = "a", Name = "a", Level = 12 };
            var b = new Runner { Id = "b", Name = "b", Level = 13 };
            var group = new[] { me, a, b };
            string Calls(double seconds, string zoneMe, string zoneA, string zoneB)
            {
                now = now.AddSeconds(seconds);
                (me.Zone, a.Zone, b.Zone) = (zoneMe, zoneA, zoneB);
                foreach (var r in group) r.LastSeen = now;
                return string.Join(" / ", calls.Update(group, me.Id, now, r => r.Name));
            }
            Check("calls: the strand", Calls(0, "The Coast", "The Coast", "The Coast"), "");
            Check("calls: first into act 2 town, highest level", Calls(1, "The Southern Forest", "The Forest Encampment", "The Southern Forest"), "b weaver");
            Check("calls: weaver once", Calls(1, "The Forest Encampment", "The Forest Encampment", "The Forest Encampment"), "");
            Check("calls: wetlands, not everyone in", Calls(1, "The Wetlands", "The Wetlands", "The Western Forest"), "");
            Check("calls: wetlands, all in, waiting", Calls(1, "The Wetlands", "The Wetlands", "The Wetlands"), "");
            Check("calls: wetlands, 14 s on", Calls(14, "The Wetlands", "The Wetlands", "The Wetlands"), "");
            Check("calls: wetlands, 15 s on", Calls(1, "The Vaal Ruins", "The Wetlands", "The Wetlands"), "b vaal ruins");
            a.Level = 18;
            Check("calls: a reaches 18 first", Calls(1, "The Vaal Ruins", "The Vaal Ruins", "The Vaal Ruins"), "");
            me.Level = 21; b.Level = 18;
            Calls(1, "The Northern Forest", "The Northern Forest", "The Northern Forest");
            Check("calls: oversoul goes to the first to 18, not the highest", Calls(15, "The Northern Forest", "The Northern Forest", "The Northern Forest"), "a pushes oversoul");
            Check("calls: res, town before the temple", Calls(1, "The Sarn Encampment", "The Northern Forest", "The Sarn Encampment"), "");
            Check("calls: res, temple", Calls(1, "The Sarn Encampment", GroupCalls.Temple, "The Sarn Encampment"), "");
            Check("calls: res, already in town is no entry", Calls(1, "The Sarn Encampment", GroupCalls.Temple, "The Sarn Encampment"), "");
            Check("calls: res, town after the temple", Calls(1, "The Sarn Encampment", "The Sarn Encampment", "The Sarn Encampment"), "check res");
            Check("calls: res once", Calls(1, "Highgate", "Highgate", "Highgate"), "");
            b.Level = 30;
            Calls(1, "The Dried Lake", "The Dried Lake", "Highgate");
            now = now.AddSeconds(30);
            me.LastSeen = a.LastSeen = now; // b went offline: the two in the lake are everyone
            Check("calls: daresso, everyone online in, waiting", string.Join(" / ", calls.Update(group, me.Id, now, r => r.Name)), "");
            now = now.AddSeconds(15);
            me.LastSeen = a.LastSeen = now;
            Check("calls: daresso, highest online that isn't the admin", string.Join(" / ", calls.Update(group, me.Id, now, r => r.Name)), "a pushes daresso");
            calls.Reset();
            Check("calls: after a reset, nothing before the temple", Calls(1, "The Sarn Encampment", "Lioneye's Watch", "Lioneye's Watch"), "");

            var gamble = new GamblePrompts();
            Check("gamble: level 2 town", gamble.Update("Lioneye's Watch", 2), null);
            Check("gamble: level 3 out of town", gamble.Update("The Coast", 3), null);
            Check("gamble: first town after 3", gamble.Update("Lioneye's Watch", 3), "gamble axe");
            Check("gamble: same town again", gamble.Update("Lioneye's Watch", 4), null);
            gamble.Update("The Mud Flats", 7);
            Check("gamble: 7 and 11 passed between towns, one call", gamble.Update("Lioneye's Watch", 12), "gamble sword");
            gamble.Update("The Coast", 12);
            Check("gamble: 11 already called", gamble.Update("Lioneye's Watch", 12), null);
            gamble.Update("The Ledge", 24);
            Check("gamble: 24", gamble.Update("Highgate", 24), "gamble 4links");
            gamble.Update("The Ledge", 28);
            Check("gamble: 28", gamble.Update("Highgate", 28), "gamble axe");
            gamble.Update("The Twilight Strand", 1);
            gamble.Update("The Coast", 3);
            Check("gamble: new character starts over", gamble.Update("Lioneye's Watch", 3), "gamble axe");

            var path = Path.Combine(output, "Client.txt");
            File.WriteAllText(path, "");
            var log = new GameLog(path, "hero");
            File.AppendAllLines(path, new[]
            {
                "2026/11/13 20:00:00 1000 abc [INFO Client 1] %<GG> Hero: tp to town ",
                "2026/11/13 20:00:01 1001 abc [INFO Client 1] %Other: hi",
                "2026/11/13 20:00:02 1002 abc [INFO Client 1] #Hero: in global",
                "2026/11/13 20:00:03 1003 abc [INFO Client 1] %Hero: kitava: next",
            });
            var said = log.ReadNew().Where(e => e.Kind == GameEventKind.PartyChat).Select(e => e.Said);
            Check("party chat: own lines only, guild tag and colons handled", string.Join(" / ", said), "tp to town / kitava: next");
            File.Delete(path);
            return lines;
        }

        static void Finish(string output, List<MainForm> forms, List<string> transcript)
        {
            var folder = forms[0].RunFolder;
            transcript.Add("run folder: " + folder);
            if (folder != null)
                foreach (var file in Directory.GetFiles(folder))
                    transcript.Add($"  {Path.GetFileName(file)}: {new FileInfo(file).Length} bytes, {CountLines(file)} lines");
            File.WriteAllLines(Path.Combine(output, "transcript.txt"), transcript);
            foreach (var form in forms)
                form.Close();
        }

        /// <summary>The logs are still open for writing, so read them with sharing allowed.</summary>
        static int CountLines(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        static void Snapshot(List<MainForm> forms, string path)
        {
            var boards = forms.Select(f => f.Render()).ToList();
            using (var sheet = new Bitmap(boards.Sum(b => b.Width) + 16 * (boards.Count - 1), boards.Max(b => b.Height)))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(40, 40, 40));
                var x = 0;
                foreach (var board in boards)
                {
                    g.DrawImage(board, x, 0);
                    x += board.Width + 16;
                    board.Dispose();
                }
                sheet.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>The whole window, child controls included (the admin's chat box), not just the board.</summary>
        static void SaveWindow(Form form, string path)
        {
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>GroupRun.exe --dialogs out-folder: the setup windows rendered to PNGs, never shown.</summary>
        public static void Dialogs(string output)
        {
            Directory.CreateDirectory(output);
            Save(new SetupForm(new Settings { DisplayName = "r1", ClientLogPath = Settings.GuessClientLog(), GroupCode = "83.21.4.17:47321/k3f9x2qa1b" }), Path.Combine(output, "setup-runner.png"));
            Save(new SetupForm(new Settings { DisplayName = "r1", IsAdmin = true }), Path.Combine(output, "setup-admin.png"));
        }

        static void Save(Form form, string path)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.Opacity = 0; // shown, so the controls paint, but invisible and off-screen
            form.Show();
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(path, ImageFormat.Png);
            }
            form.Dispose();
        }

        /// <summary>
        /// GroupRun.exe --tunneltest out-file: hosting end to end, no windows. Gets cloudflared if needed, opens a real
        /// Cloudflare tunnel to a hub, posts the address to a fresh ntfy topic, then a runner finds it through the
        /// permanent join code and connects over wss://; messages go both ways. Everything is written to out-file.
        /// </summary>
        public static async System.Threading.Tasks.Task<int> TunnelTest(string output)
        {
            var log = new List<string>();
            void Say(string line) { log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}"); File.WriteAllLines(output, log); }
            Tunnel tunnel = null;
            Hub hub = null;
            WsRelay runner = null;
            try
            {
                var cloudflared = Tunnel.Find();
                if (cloudflared == null)
                {
                    Say("cloudflared missing: downloading from Cloudflare's GitHub release");
                    cloudflared = await Tunnel.DownloadAsync();
                    Say($"downloaded and checksum ok: {new FileInfo(cloudflared).Length / 1048576.0:0.0} MB");
                }
                else Say("cloudflared: " + cloudflared);

                var secret = JoinCode.NewSecret();
                var topic = Pointer.NewTopic();
                hub = new Hub(0, secret);
                Say($"hub on 127.0.0.1:{hub.Port}");
                var started = DateTime.Now;
                var (t, host) = await Tunnel.StartAsync(cloudflared, hub.Port);
                tunnel = t;
                Say($"tunnel up after {(DateTime.Now - started).TotalSeconds:0.0} s: {host}");
                await Pointer.PublishAsync(topic, secret, host);
                Say("address posted to the topic");
                var resolved = await Pointer.ResolveAsync(topic, secret);
                Say("address read back: " + (resolved.FirstOrDefault() == host ? "matches" : "MISMATCH: " + string.Join(", ", resolved)));
                var forged = await Pointer.ResolveAsync(topic, "wrong-secret");
                Say("with a wrong secret: " + (forged.Count == 0 ? "nothing (signature check works)" : "ACCEPTED " + string.Join(", ", forged)));

                var code = new JoinCode { Host = topic, Secret = secret };
                Say("permanent join code: " + code);
                runner = new WsRelay(code);
                var connectStart = DateTime.Now;
                while (runner.Link != Link.Connected && DateTime.Now - connectStart < TimeSpan.FromSeconds(60))
                    await System.Threading.Tasks.Task.Delay(100);
                if (runner.Link != Link.Connected)
                {
                    Say($"runner did not connect: {runner.Link} {runner.LastError}");
                    return 1;
                }
                Say($"runner connected through the tunnel after {(DateTime.Now - connectStart).TotalSeconds:0.0} s");

                hub.Publish(new Dictionary<string, object> { ["t"] = "control", ["from"] = "admin", ["action"] = "readycheck", ["elapsedMs"] = 0, ["check"] = "abc" });
                var sent = DateTime.Now;
                Message got = null;
                while (DateTime.Now - sent < TimeSpan.FromSeconds(15))
                {
                    if (runner.Inbox.TryDequeue(out var m) && (m.Body["t"] as string) == "control") { got = m; break; }
                    await System.Threading.Tasks.Task.Delay(20);
                }
                Say(got != null ? $"admin -> runner: ready check arrived in {(DateTime.Now - sent).TotalMilliseconds:0} ms" : "admin -> runner: NOTHING ARRIVED");

                while (hub.Local.TryDequeue(out _)) { }
                runner.Publish(new Dictionary<string, object> { ["t"] = "runner", ["id"] = "r2", ["name"] = "r2", ["level"] = 2, ["xp"] = 0.5, ["deaths"] = 0, ["zone"] = "", ["ready"] = "abc" });
                sent = DateTime.Now;
                got = null;
                while (DateTime.Now - sent < TimeSpan.FromSeconds(15))
                {
                    if (hub.Local.TryDequeue(out var m) && (m.Body["t"] as string) == "runner") { got = m; break; }
                    await System.Threading.Tasks.Task.Delay(20);
                }
                Say(got != null ? $"runner -> admin: runner update arrived in {(DateTime.Now - sent).TotalMilliseconds:0} ms" : "runner -> admin: NOTHING ARRIVED");
                return got != null ? 0 : 1;
            }
            catch (Exception e)
            {
                Say("FAILED: " + e.GetBaseException().Message);
                return 1;
            }
            finally
            {
                runner?.Dispose();
                tunnel?.Dispose();
                hub?.Dispose();
                Say("closed");
            }
        }

        /// <summary>A feed stuck on one setup problem, for showing that state.</summary>
        sealed class StuckFeed : IGameFeed
        {
            public StuckFeed(string problem) => Problem = problem;
            public string Problem { get; }
            public void Update(Runner self, GroupRun.Run run, DateTime now, List<Dictionary<string, object>> detail) { }
            public List<string> TakePartyChat() => new List<string>();
        }

        /// <summary>
        /// GroupRun.exe --states out-folder: every setup and connection problem as the board shows it, from the
        /// real code paths (a hub refusing a code, a port nobody listens on, a hub that goes away), as PNGs.
        /// </summary>
        public static void States(string output)
        {
            Directory.CreateDirectory(output);
            var hub = new Hub(0, "right");
            var doomed = new Hub(0, "right"); // closed after the runner connects: "lost"
            var nobody = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            nobody.Start();
            var closedPort = ((System.Net.IPEndPoint)nobody.LocalEndpoint).Port;
            nobody.Stop(); // nothing listens here: "can't reach"

            JoinCode Code(Hub h, string secret) => new JoinCode { Host = "127.0.0.1:" + h.Port, Secret = secret };
            MainForm Runner(string name, IRelay relay, IGameFeed feed, string code = "") => new MainForm(
                new Settings { DisplayName = name, RunnerId = name, Voice = false, OnTop = "never", GroupCode = code },
                relay, feed, sim: true, primary: false);

            var admin = new MainForm(new Settings { DisplayName = "r1", RunnerId = "r1", IsAdmin = true, Voice = false, OnTop = "never" },
                new HubRelay(hub), new SimFeed(0), sim: true, primary: false);
            admin.ShowJoinCode("grouprun-8f3k2a9c1d7e4b6f/k3f9x2qa1b", "cloudflared missing");
            var states = new List<(string Name, MainForm Form)>
            {
                ("admin-port-closed", admin),
                ("no-client-txt", Runner("r2", new WsRelay(Code(hub, "right")), new StuckFeed("log"))),
                ("log-quiet", Runner("r3", new WsRelay(Code(hub, "right")), new StuckFeed("log-quiet"))),
                ("game-not-running", Runner("r5", new WsRelay(Code(hub, "right")), new StuckFeed("game"))),
                ("no-join-code", Runner("r6", new WsRelay(null), new SimFeed(1))),
                ("cant-reach-admin", Runner("r7", new WsRelay(new JoinCode { Host = "127.0.0.1:" + closedPort, Secret = "right" }), new SimFeed(1), "127.0.0.1:" + closedPort + "/right")),
                ("code-rejected", Runner("r8", new WsRelay(Code(hub, "wrong")), new SimFeed(1))),
                ("lost-admin", Runner("r9", new WsRelay(Code(doomed, "right")), new SimFeed(1))),
            };

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var timer = new Timer { Interval = 100 };
            timer.Tick += (s, e) =>
            {
                if (clock.Elapsed.TotalSeconds > 2 && doomed != null)
                {
                    doomed.Dispose();
                    doomed = null;
                }
                if (clock.Elapsed.TotalSeconds < 4.5)
                    return;
                timer.Stop();
                foreach (var (name, form) in states)
                    using (var board = form.Render())
                        board.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
                Snapshot(states.Select(st => st.Form).ToList(), Path.Combine(output, "all.png"));
                Application.ExitThread();
            };
            timer.Start();
            Application.Run(new ApplicationContext());
        }
    }
}
