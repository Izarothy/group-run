using System;
using System.Drawing;
using System.Net.Sockets;
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
            if (args.Length == 2 && args[0] == "--selftest")
            {
                SelfTest.Run(args[1]);
                return;
            }

            var settings = Settings.Load();
            if (!settings.IsComplete)
            {
                using (var setup = new SetupForm(settings))
                    if (setup.ShowDialog() != DialogResult.OK || !settings.IsComplete)
                        return;
                settings.Save();
            }
            Application.Run(settings.IsAdmin ? Host(settings) : Join(settings));
        }

        /// <summary>The admin's app: runs the hub on this PC, opens the port, shows the join code.</summary>
        static Form Host(Settings settings)
        {
            if (settings.HostSecret.Length == 0 || settings.HostTopic.Length == 0)
            {
                if (settings.HostSecret.Length == 0) settings.HostSecret = JoinCode.NewSecret();
                if (settings.HostTopic.Length == 0) settings.HostTopic = Pointer.NewTopic();
                settings.Save();
            }
            var stable = new JoinCode { Host = settings.HostTopic, Secret = settings.HostSecret }.ToString();
            Hub hub;
            try
            {
                hub = new Hub(settings.HostPort, settings.HostSecret);
            }
            catch (SocketException)
            {
                hub = new Hub(0, settings.HostSecret); // the usual port is taken: any free one works behind the tunnel
            }
            var form = new MainForm(settings, new HubRelay(hub), new GameFeed(settings));
            Tunnel tunnel = null;
            string current = null; // the tunnel's address now
            var opening = false;

            async Task Open()
            {
                if (opening || tunnel != null) return;
                opening = true;
                try
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
                    tunnel = started;
                    current = host;
                    await Pointer.PublishAsync(settings.HostTopic, settings.HostSecret, host);
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
