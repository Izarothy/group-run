using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GroupRun
{
    /// <summary>
    /// Fake runners for showing the panel off, in real time. Two scenes: the ready check before a run (6 s),
    /// then a cut to twenty minutes in, early Act 2, where runners level at different rates: a few level up
    /// and r4 overtakes r3. Level-ups land at once; XP % moves in 10 s steps, as the app sends it.
    /// </summary>
    public sealed class DemoScript
    {
        const double ReadyScene = 6;
        static readonly TimeSpan LiveFrom = new TimeSpan(0, 19, 42);
        static readonly TimeSpan XpUpdateEvery = TimeSpan.FromSeconds(10);

        // live scene: level (with progress) at the cut, and levels gained per minute
        static readonly (double Start, double PerMinute)[] Live =
        {
            (17.40, 0.55), (17.05, 0.60), (16.80, 0.50), (16.74, 0.75), (16.30, 0.55), (15.85, 0.70),
        };
        static readonly double[] ReadyAt = { 0.8, 2.0, 1.4, 3.4, 2.7, 4.3 };

        public const double Length = 46;
        public double Seconds { get; private set; }
        public DateTime Now => DateTime.Today.AddHours(20) + sim;
        public Run Run => Seconds < ReadyScene ? before : live;

        /// <summary>Panel height with the whole cast connected.</summary>
        public static int FullHeight => PanelView.HeightFor(new DemoScript().live, Board);

        /// <summary>The demo shows the board alone, as an OBS overlay would.</summary>
        public static readonly Chrome Board = new Chrome { ShowControls = false };

        readonly Run before, live;
        TimeSpan sim;
        TimeSpan? lastXpUpdate;

        public DemoScript()
        {
            before = new Run(() => sim);
            live = new Run(() => sim);
            foreach (var run in new[] { before, live })
                for (var i = 0; i < Live.Length; i++)
                    run.Upsert("demo" + i, "r" + (i + 1)).Xp = 0;
            before.Apply("readycheck", TimeSpan.Zero, TimeSpan.Zero, "demo");
            live.Apply("start", LiveFrom, TimeSpan.Zero);
            Step(0);
            foreach (var runner in live.Runners.Values)
                runner.LevelUpAt = DateTime.MinValue; // the cut is not a level-up
        }

        /// <summary>Advance the demo by <paramref name="dt"/> seconds (real time).</summary>
        public void Step(double dt)
        {
            Seconds += dt;
            sim += TimeSpan.FromSeconds(dt);
            for (var i = 0; i < Live.Length; i++)
            {
                var runner = before.Runners["demo" + i];
                runner.LastSeen = Now;
                if (Seconds >= ReadyAt[i])
                    runner.ReadyFor = before.ReadyCheck;
            }

            var minutes = Math.Max(0, Seconds - ReadyScene) / 60;
            var xpDue = lastXpUpdate == null || live.Elapsed - lastXpUpdate.Value >= XpUpdateEvery;
            for (var i = 0; i < Live.Length; i++)
            {
                var runner = live.Runners["demo" + i];
                runner.LastSeen = Now;
                var progress = Live[i].Start + Live[i].PerMinute * minutes;
                var levelUp = (int)progress != runner.Level;
                runner.SetLevel((int)progress, Now);
                if (levelUp || xpDue)
                    runner.Xp = progress - runner.Level;
            }
            if (xpDue)
                lastXpUpdate = live.Elapsed;
        }
    }

    /// <summary>The demo in a window (GroupRun.exe --demo), looping.</summary>
    public sealed class DemoForm : Form
    {
        DemoScript script = new DemoScript();
        readonly Stopwatch frame = Stopwatch.StartNew();

        public DemoForm()
        {
            Text = "Group Run (demo)";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            DoubleBuffered = true;
            ClientSize = new Size(PanelView.Width, DemoScript.FullHeight);
            var timer = new Timer { Interval = 33 };
            timer.Tick += (s, e) =>
            {
                script.Step(frame.Elapsed.TotalSeconds);
                frame.Restart();
                if (script.Seconds > DemoScript.Length)
                    script = new DemoScript();
                Invalidate();
            };
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e) =>
            PanelView.Draw(e.Graphics, script.Run, script.Now, "", DemoScript.Board);
    }

    /// <summary>Renders the demo straight to an mp4 through ffmpeg (GroupRun.exe --record out.mp4).</summary>
    public static class DemoRecorder
    {
        const int Fps = 30;
        const int Scale = 2;

        public static void Record(string output)
        {
            var script = new DemoScript();
            int width = PanelView.Width * Scale, height = DemoScript.FullHeight * Scale;
            var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-y -loglevel error -f rawvideo -pix_fmt bgr24 -s {width}x{height} -r {Fps} -i - " +
                $"-c:v libx264 -preset slow -crf 20 -pix_fmt yuv420p -movflags +faststart \"{output}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            });
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(bitmap))
            using (var stdin = ffmpeg.StandardInput.BaseStream)
            {
                g.ScaleTransform(Scale, Scale);
                var row = new byte[width * 3];
                for (var i = 0; i < DemoScript.Length * Fps; i++)
                {
                    script.Step(1.0 / Fps);
                    PanelView.Draw(g, script.Run, script.Now, "", DemoScript.Board);
                    var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    for (var y = 0; y < height; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        stdin.Write(row, 0, row.Length);
                    }
                    bitmap.UnlockBits(data);
                }
            }
            ffmpeg.WaitForExit();
        }
    }
}
