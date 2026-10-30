using System;
using System.Drawing;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GroupRun
{
    static class Program
    {
        /// <summary>
        /// GroupRun.exe                   the app (asks for setup on first launch)
        /// GroupRun.exe --sim 4           2-6 app windows talking to each other in-process: r1 is the admin, all fake
        /// GroupRun.exe --demo            the scripted demo in a window
        /// GroupRun.exe --record out.mp4  the scripted demo rendered to a video (needs ffmpeg on PATH)
        /// GroupRun.exe --selftest dir    the sim with hidden windows and a scripted run, boards saved as PNGs
        /// GroupRun.exe --states dir      every setup and connection problem as the board shows it, as PNGs
        /// </summary>
        /// <summary>
        /// Every launch starts the latest release: nothing is running yet, so a newer one is installed without
        /// asking. Offline or slow, the launch goes on with this version.
        /// </summary>
        static bool UpdatedOnLaunch()
        {
            try
            {
                var check = Updater.LatestAsync();
                if (!check.Wait(TimeSpan.FromSeconds(4)) || check.Result == null || check.Result.Version <= Updater.Current)
                    return false;
                using (var slow = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    Updater.InstallAsync(check.Result, slow.Token).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void LogError(Exception e)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "errors.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} v{Updater.Current} {e}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // nowhere to write: carry on regardless
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            SetProcessDPIAware(); // draw crisp at the screen's scale instead of being stretched by Windows
            Updater.CleanUp();
            if (args.Length == 2 && args[0] == "--record")
            {
                DemoRecorder.Record(args[1]);
                return;
            }

            // a run is live while this is open: an unexpected error is written down, never a dialog in the way
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogError(e.Exception);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 1 && args[0] == "--demo")
            {
                Application.Run(new DemoForm());
                return;
            }
            if (args.Length >= 1 && args[0] == "--sim")
            {
                var count = args.Length > 1 && int.TryParse(args[1], out var n) ? Math.Max(2, Math.Min(6, n)) : 4;
                Simulate(count);
                return;
            }

            if (args.Length == 2 && args[0] == "--tunneltest")
            {
                Environment.ExitCode = SelfTest.TunnelTest(args[1]).GetAwaiter().GetResult();
                return;
            }
            if (args.Length == 2 && args[0] == "--dialogs")
            {
                SelfTest.Dialogs(args[1]);
                return;
            }
            if (args.Length == 2 && args[0] == "--states")
            {
                SelfTest.States(args[1]);
                return;
            }
            if (args.Length == 2 && args[0] == "--report")
            {
                var log = RunLog.Load(args[1]);
                Report.Save(TimeSpan.FromSeconds(log.Entries.Count == 0 ? 0 : log.Entries.Max(e => e.Seconds)), log, open: false);
                return;
            }
            if (args.Length == 2 && args[0] == "--selftest")
            {
                SelfTest.Run(args[1]);
                return;
            }

            var single = new Mutex(false, "Local\\GroupRun " + AppContext.BaseDirectory.ToLowerInvariant().Replace('\\', '/'));
            if (!FirstInstance(single))
                return;
            if (UpdatedOnLaunch())
                return; // the new version is starting in our place

            var settings = Settings.Load();
            if (!settings.IsComplete)
            {
                using (var setup = new SetupForm(settings))
                    if (setup.ShowDialog() != DialogResult.OK || !settings.IsComplete)
                        return;
                if (!settings.Save())
                    Ui.Tell(null, Settings.CantSave);
            }
            Application.Run(settings.IsAdmin ? Host(settings) : Join(settings));
            GC.KeepAlive(single);
        }

        /// <summary>
        /// One app per folder: a second one on the same settings would host a second tunnel on the group's topic
        /// and split the runners between two boards. An update's restart waits here for the old one to close.
        /// </summary>
        static bool FirstInstance(Mutex single)
        {
            try
            {
                return single.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                return true; // the last one exited without letting go: a crash, or the update that started us
            }
        }

        /// <summary>The admin's app: runs the hub on this PC, opens the port, shows the join code.</summary>
        static Form Host(Settings settings)
        {
            if (settings.HostSecret.Length == 0 || settings.HostTopic.Length == 0)
            {
                if (settings.HostSecret.Length == 0) settings.HostSecret = JoinCode.NewSecret();
                if (settings.HostTopic.Length == 0) settings.HostTopic = Pointer.NewTopic();
                if (!settings.Save())
                    Ui.Tell(null, Settings.CantSave + " The join code changes every launch until then.");
            }
            var stable = new JoinCode { Host = settings.HostTopic, Secret = settings.HostSecret }.ToString();
            var journal = System.IO.Path.Combine(AppContext.BaseDirectory, "hub.jsonl"); // next to the exe, as settings.json
            Hub hub;
            try
            {
                hub = new Hub(settings.HostPort, settings.HostSecret, journal);
            }
            catch (Exception e) when (e is SocketException || e is ArgumentOutOfRangeException)
            {
                hub = new Hub(0, settings.HostSecret, journal); // the usual port is taken: any free one works behind the tunnel
            }
            var form = new MainForm(settings, new HubRelay(hub), new GameFeed(settings));
            Tunnel tunnel = null;
            string current = null; // the tunnel's address now
            var opening = false;

            async Task Open()
            {
                if (opening) return;
                opening = true;
                try
                {
                    if (tunnel == null) // up already when only posting the address failed
                    {
                        var cloudflared = Tunnel.Find();
                        if (cloudflared == null)
                        {
                            if (!Ui.Tell(form, "Hosting goes through a Cloudflare tunnel, so runners never see your IP. Get cloudflared from Cloudflare's GitHub (about 60 MB)?", "Download", "Later"))
                            {
                                form.ShowJoinCode("", "cloudflared missing");
                                return;
                            }
                            form.ShowJoinCode("downloading cloudflared…", "");
                            cloudflared = await Tunnel.DownloadAsync();
                        }
                        form.ShowJoinCode("starting tunnel…", "");
                        var (started, host) = await Tunnel.StartAsync(cloudflared, hub.Port);
                        started.Died += () => form.BeginInvoke(new Action(() => Closed(started)));
                        tunnel = started;
                        current = host;
                    }
                    await Pointer.PublishAsync(settings.HostTopic, settings.HostSecret, current);
                    form.ShowJoinCode(stable, "");
                }
                catch (Exception e)
                {
                    form.ShowJoinCode("", "Tunnel failed: " + e.GetBaseException().Message);
                }
                finally
                {
                    opening = false;
                }
            }

            // cloudflared quit mid-session: runners reconnect to wherever the topic points, so open a new tunnel and post it
            void Closed(Tunnel dead)
            {
                if (tunnel != dead) return;
                tunnel.Dispose();
                tunnel = null;
                current = null;
                form.ShowJoinCode("", "Tunnel closed");
                _ = Open();
            }

            form.HostRetry = () => _ = Open();
            form.Shown += async (s, e) => await Open();
            form.FormClosed += (s, e) => tunnel?.Dispose();
            // ntfy keeps a message 12 hours: post the address again well before it lapses
            var repost = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds };
            repost.Tick += async (s, e) =>
            {
                if (current == null) return;
                try { await Pointer.PublishAsync(settings.HostTopic, settings.HostSecret, current); }
                catch (Exception) { } // the next tick tries again; runners still connected aren't affected
            };
            repost.Start();
            return form;
        }

        /// <summary>A runner's app: connects to the admin's hub with the join code.</summary>
        static Form Join(Settings settings)
        {
            JoinCode.TryParse(settings.GroupCode, out var code); // null when missing or mistyped: the board says so
            return new MainForm(settings, new WsRelay(code), new GameFeed(settings));
        }

        /// <summary>One window per runner, side by side, wrapping to a second row if the screen is narrow.
        /// r1 hosts a real hub on this PC only; the others connect to it like runners would.</summary>
        static void Simulate(int count)
        {
            var hub = new Hub(0, "sim");
            var area = Screen.PrimaryScreen.WorkingArea;
            var forms = new MainForm[count];
            var origin = new Point(area.Left + 24, area.Top + 24);
            var x = origin.X;
            var y = origin.Y;
            var rowHeight = 0;
            for (var i = 0; i < count; i++)
            {
                var settings = new Settings { DisplayName = "r" + (i + 1), RunnerId = "sim" + i, IsAdmin = i == 0 };
                var relay = i == 0
                    ? (IRelay)new HubRelay(hub)
                    : new WsRelay(new JoinCode { Host = "127.0.0.1:" + hub.Port, Secret = "sim" });
                var form = forms[i] = new MainForm(settings, relay, new SimFeed(i), sim: true, primary: i == 0)
                {
                    StartPosition = FormStartPosition.Manual,
                };
                if (x + form.Width > area.Right && x > origin.X)
                {
                    x = origin.X;
                    y += rowHeight + 16;
                    rowHeight = 0;
                }
                form.Location = new Point(x, y);
                x += form.Width + 16;
                rowHeight = Math.Max(rowHeight, form.Height + 160); // room for the board to grow as runners join
            }
            for (var i = 1; i < count; i++)
                forms[i].Show();
            Application.Run(forms[0]);
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }
}
