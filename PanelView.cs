using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace GroupRun
{
    /// <summary>A clickable button the panel drew, for the window to hit-test.</summary>
    public sealed class PanelButton
    {
        public string Action;
        public RectangleF Bounds;
    }

    /// <summary>A message above the buttons: amber for something to fix, grey for something to wait for.</summary>
    public sealed class Notice
    {
        public string Title = "";
        public string Hint = "";
        public bool Warning;
    }

    /// <summary>What the window wants drawn around the standings.</summary>
    public sealed class Chrome
    {
        public bool ShowControls = true; // off in overlay mode, so OBS only captures the board
        public bool IsAdmin;
        public string GroupCode = "";
        public Notice Notice;            // shown above the buttons: what's wrong and how to fix it
    }

    /// <summary>
    /// The board, in the "2a" system picked on the design canvas: Geist and Geist Mono on neutral black,
    /// one colour per state (green live, amber paused or levelled up, blue ready check, red for Finish).
    /// The label and timer at the top carry the run's state; rows stay plain unless something happens to
    /// that runner. Used by the window, the sim and the demo recorder.
    /// </summary>
    public static class PanelView
    {
        public const int Width = 300;
        const int Pad = 14;
        const int HeaderHeight = 44;
        const int RowHeight = 28;
        const int FooterHeight = 34;
        const int BottomPad = 10;
        const int StripHeight = 52;
        const int NoticeTop = 30;        // title baseline area above the hint
        const int HintLine = 17;
        const float HintX = Pad + 14;
        const float HintWidth = Width - Pad - HintX;

        const float NameX = Pad;
        const float LevelRight = Width - Pad - 82;
        const float GapRight = Width - Pad;

        static readonly TimeSpan LevelUpShown = TimeSpan.FromSeconds(3);
        static readonly TimeSpan CancelShown = TimeSpan.FromSeconds(4);

        public static readonly Color Background = Color.FromArgb(10, 10, 10);
        static readonly Color StripBackground = Color.FromArgb(17, 17, 17);
        static readonly Color Text = Color.FromArgb(237, 237, 237);
        static readonly Color Secondary = Color.FromArgb(161, 161, 161);
        static readonly Color Faint = Color.FromArgb(92, 92, 92);
        static readonly Color Line = Color.FromArgb(31, 31, 31);
        static readonly Color ButtonBorder = Color.FromArgb(51, 51, 51);
        static readonly Color Green = Color.FromArgb(69, 196, 107);
        static readonly Color Amber = Color.FromArgb(245, 165, 36);
        static readonly Color Blue = Color.FromArgb(82, 168, 255);
        static readonly Color Red = Color.FromArgb(255, 97, 102);

        static readonly Font LabelFont = Fonts.Mono(11);
        static readonly Font TimerFont = Fonts.MonoMedium(22);
        static readonly Font NameFont = Fonts.SansSemiBold(16);
        static readonly Font LevelFont = Fonts.Mono(16);
        static readonly Font GapFont = Fonts.Mono(13);
        static readonly Font FooterFont = Fonts.Sans(13);
        static readonly Font CodeFont = Fonts.Mono(12);
        static readonly Font ButtonFont = Fonts.SansMedium(13);
        static readonly Font HintFont = Fonts.Sans(12);
        static readonly Graphics Measurer = Graphics.FromImage(new Bitmap(1, 1));

        static bool ShowsFooter(Run run, Chrome chrome) => run.Phase == Phase.Waiting && run.ReadyCheck.Length == 0 && chrome.GroupCode.Length > 0;

        /// <summary>The hint wraps to a second line when it doesn't fit on one.</summary>
        static int HintLines(Notice notice) =>
            notice.Hint.Length == 0 ? 0 : Measure(Measurer, notice.Hint, HintFont) > HintWidth ? 2 : 1;

        static int NoticeHeight(Notice notice) => NoticeTop + HintLines(notice) * HintLine + 6;

        public static int HeightFor(Run run, Chrome chrome) =>
            HeaderHeight + Math.Max(1, run.Runners.Count) * RowHeight + (ShowsFooter(run, chrome) ? FooterHeight : 0) + BottomPad
            + (chrome.ShowControls ? StripHeight + (chrome.Notice != null ? NoticeHeight(chrome.Notice) : 0) : 0);

        /// <summary>Draw the board; returns the buttons it drew.</summary>
        public static List<PanelButton> Draw(Graphics g, Run run, DateTime now, string selfId, Chrome chrome)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Background);

            DrawHeader(g, run);
            var top = HeaderHeight;
            foreach (var runner in run.InOrder)
            {
                DrawRow(g, run, runner, top, now);
                top += RowHeight;
            }
            if (run.Runners.Count == 0)
            {
                Rule(g, top);
                top += RowHeight;
            }
            if (ShowsFooter(run, chrome))
            {
                Rule(g, top);
                DrawClipped(g, chrome.GroupCode, CodeFont, Secondary, Pad, top + 22, Width - 2 * Pad, rightAlign: false);
                top += FooterHeight;
            }
            top += BottomPad;
            if (!chrome.ShowControls)
                return new List<PanelButton>();
            RectangleF? notice = null;
            if (chrome.Notice != null)
            {
                DrawNotice(g, chrome.Notice, top);
                notice = new RectangleF(0, top, Width, NoticeHeight(chrome.Notice));
                top += NoticeHeight(chrome.Notice);
            }
            var buttons = DrawStrip(g, run, now, selfId, chrome, top);
            if (notice is RectangleF bounds)
                buttons.Add(new PanelButton { Action = "settings", Bounds = bounds });
            return buttons;
        }

        static void DrawHeader(Graphics g, Run run)
        {
            const float baseline = 32;
            var timer = FormatTime(run.Elapsed);
            switch (run.Phase)
            {
                case Phase.Running:
                    Dot(g, Green, Pad, baseline - 7);
                    DrawText(g, "LIVE", LabelFont, Green, Pad + 12, baseline - 2);
                    RightAligned(g, timer, TimerFont, Text, Width - Pad, baseline);
                    break;
                case Phase.Countdown:
                    var left = Math.Max(1, (int)Math.Ceiling(run.CountdownLeft.TotalSeconds));
                    DrawText(g, run.Resuming ? "RESUMING" : "STARTING", LabelFont, Blue, Pad, baseline - 2);
                    RightAligned(g, "−0:0" + left, TimerFont, Blue, Width - Pad, baseline);
                    break;
                case Phase.Paused:
                    Fill(g, Amber, Pad, baseline - 11, 2, 8);
                    Fill(g, Amber, Pad + 4, baseline - 11, 2, 8);
                    var by = run.PausedBy.Length > 0 ? " · " + run.NameOf(run.PausedBy).ToUpperInvariant() : "";
                    DrawText(g, "PAUSED" + by, LabelFont, Amber, Pad + 12, baseline - 2);
                    RightAligned(g, timer, TimerFont, Amber, Width - Pad, baseline);
                    break;
                case Phase.Finished:
                    using (var pen = new Pen(Text, 1.6f))
                        g.DrawLines(pen, new[] { new PointF(Pad + 1, baseline - 6), new PointF(Pad + 3.5f, baseline - 3.5f), new PointF(Pad + 8, baseline - 9) });
                    DrawText(g, "FINAL", LabelFont, Text, Pad + 14, baseline - 2);
                    RightAligned(g, timer, TimerFont, Text, Width - Pad, baseline);
                    break;
                default:
                    var check = run.ReadyCheck.Length > 0;
                    if (run.SinceCancel < CancelShown)
                        DrawText(g, run.NameOf(run.CancelledBy).ToUpperInvariant() + " NOT READY", LabelFont, Amber, Pad, baseline - 2);
                    else
                        DrawText(g, check ? $"READY {run.Runners.Values.Count(run.IsReady)}/{run.Runners.Count}" : "WAITING",
                            LabelFont, check ? Blue : Secondary, Pad, baseline - 2);
                    RightAligned(g, timer, TimerFont, Secondary, Width - Pad, baseline);
                    break;
            }
        }

        static void DrawRow(Graphics g, Run run, Runner runner, int top, DateTime now)
        {
            Rule(g, top);
            var baseline = top + 19;
            var offline = runner.IsStale(now);
            var paused = run.Phase == Phase.Paused;
            var main = offline ? Faint : paused ? Secondary : Text;

            DrawText(g, runner.Name, NameFont, main, NameX, baseline);

            var levelUp = !offline && now - runner.LevelUpAt < LevelUpShown && run.Phase == Phase.Running;
            RightAligned(g, Progress(runner), LevelFont, levelUp ? Amber : main, LevelRight, baseline);

            string tail;
            Color tailColor;
            var problem = runner.Problem.Length > 0 && Problems.All.TryGetValue(runner.Problem, out var p) ? p : null;
            if (problem != null && run.Phase == Phase.Waiting && !run.IsReady(runner) && !offline)
                (tail, tailColor) = (problem.Short, problem.Warning ? Amber : Faint);
            else if (run.ReadyCheck.Length > 0 && (run.Phase == Phase.Waiting || run.Phase == Phase.Countdown && !run.Resuming))
                (tail, tailColor) = run.IsReady(runner) ? ("ready", Blue)
                    : runner.Id == run.CancelledBy && run.SinceCancel < CancelShown ? ("not ready", Amber)
                    : ("waiting", Faint);
            else if (paused && runner.Id == run.PausedBy)
                (tail, tailColor) = ("paused", Amber);
            else if (offline)
                (tail, tailColor) = ("offline", Faint);
            else
                (tail, tailColor) = ("", Secondary);
            RightAligned(g, tail, GapFont, tailColor, GapRight, baseline - 1);
        }

        static void DrawNotice(Graphics g, Notice notice, int top)
        {
            Fill(g, StripBackground, 0, top, Width, NoticeHeight(notice));
            Fill(g, Line, 0, top, Width, 1);
            Dot(g, notice.Warning ? Amber : Secondary, Pad, top + 13);
            DrawClipped(g, notice.Title, ButtonFont, Text, HintX, top + 20, HintWidth, rightAlign: false);
            using (var brush = new SolidBrush(Secondary))
            using (var format = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisWord })
                g.DrawString(notice.Hint, HintFont, brush, new RectangleF(HintX, top + NoticeTop - 2, HintWidth, HintLines(notice) * HintLine), format);
        }

        static List<PanelButton> DrawStrip(Graphics g, Run run, DateTime now, string selfId, Chrome chrome, int top)
        {
            Fill(g, StripBackground, 0, top, Width, StripHeight);
            Fill(g, Line, 0, top, Width, 1);

            var actions = new List<(string Action, string Label, Color Color)>();
            var admin = chrome.IsAdmin;
            switch (run.Phase)
            {
                case Phase.Waiting:
                    var readyCheck = run.ReadyCheck.Length > 0;
                    var self = run.Runners.TryGetValue(selfId, out var me) ? me : null;
                    if (readyCheck && self != null)
                        actions.Add(run.IsReady(self) ? ("unready", "Not ready", Text) : ("ready", "I'm ready", Text));
                    if (admin && !readyCheck) actions.Add(("readycheck", "Ready check", Text));
                    if (admin) actions.Add(("countdown", "Start", Text));
                    break;
                case Phase.Countdown:
                    if (!run.Resuming) actions.Add(("unready", "Not ready", Text)); // the start countdown can only be called off
                    break;
                case Phase.Running:
                    actions.Add(("pause", "Pause", Text));
                    if (admin) actions.Add(("finish", "Finish", Red));
                    break;
                case Phase.Paused:
                    if (admin || run.PausedBy == selfId) actions.Add(("countdown", "Resume", Text));
                    if (admin) actions.Add(("finish", "Finish", Red));
                    break;
                case Phase.Finished:
                    if (admin) actions.Add(("report", "Report", Text));
                    if (admin) actions.Add(("reset", "New run", Text));
                    break;
            }

            var buttons = new List<PanelButton>();
            float x = Pad;
            foreach (var (action, label, color) in actions)
            {
                var bounds = new RectangleF(x, top + 11, Measure(g, label, ButtonFont) + 28, 30);
                using (var path = Rounded(bounds, 6))
                {
                    using (var fill = new SolidBrush(Background))
                        g.FillPath(fill, path);
                    using (var pen = new Pen(ButtonBorder))
                        g.DrawPath(pen, path);
                }
                DrawText(g, label, ButtonFont, color, bounds.X + 14, bounds.Y + 20);
                buttons.Add(new PanelButton { Action = action, Bounds = bounds });
                x = bounds.Right + 8;
            }

            var online = run.Runners.Values.Count(r => !r.IsStale(now));
            DrawClipped(g, $"{online}/{run.Runners.Count} online", LabelFont, Secondary, x + 8, top + 31, Width - Pad - x - 8, rightAlign: true);
            return buttons;
        }

        public static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";


        /// <summary>Level and XP as one number: 17.53 = level 17, 53% of the way to 18.</summary>
        static string Progress(Runner r) => r.Xp is double xp ? (r.Level + Math.Min(xp, 0.999)).ToString("0.00") : r.Level + ".—";

        static void Rule(Graphics g, float top) => Fill(g, Line, Pad, top, Width - 2 * Pad, 1);

        static void Dot(Graphics g, Color color, float x, float y)
        {
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, x, y, 6, 6);
        }

        static void Fill(Graphics g, Color color, float x, float y, float w, float h)
        {
            using (var brush = new SolidBrush(color))
                g.FillRectangle(brush, x, y, w, h);
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static float Measure(Graphics g, string text, Font font) =>
            g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

        /// <summary>Draw text with its baseline at <paramref name="baseline"/>, so different fonts line up.</summary>
        static void DrawText(Graphics g, string text, Font font, Color color, float x, float baseline)
        {
            var family = font.FontFamily;
            var ascent = font.Size * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style);
            using (var brush = new SolidBrush(color))
                g.DrawString(text, font, brush, x, baseline - ascent, StringFormat.GenericTypographic);
        }

        static void DrawClipped(Graphics g, string text, Font font, Color color, float x, float baseline, float width, bool rightAlign)
        {
            if (width < 20)
                return;
            var family = font.FontFamily;
            var ascent = font.Size * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style);
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, Alignment = rightAlign ? StringAlignment.Far : StringAlignment.Near })
                g.DrawString(text, font, brush, new RectangleF(x, baseline - ascent, width, font.Height + 4), format);
        }

        static void RightAligned(Graphics g, string text, Font font, Color color, float right, float baseline) =>
            DrawText(g, text, font, color, right - Measure(g, text, font), baseline);
    }
}
