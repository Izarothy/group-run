using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace GroupRun
{
    /// <summary>
    /// Reads how full the XP bar is from a strip of the screen, stored relative to the game window.
    /// The bar's two colours, filled and empty, are learned from the bar itself: while partly filled it splits
    /// cleanly into a left part of one colour and a right part of another, and the first clear split teaches both.
    /// From then on each read finds the split that best separates filled-looking columns on the left from
    /// empty-looking ones on the right (a step fit), so frame and texture details in the bar don't matter. When the
    /// strip matches neither colour well (character select, loading, a panel over the bar) the read is
    /// "not visible" rather than a wrong number.
    /// Run the game windowed or borderless: exclusive fullscreen can't be captured.
    /// </summary>
    public static class XpBar
    {
        /// <summary>Above this mean colour distance per column, what's on screen isn't the calibrated bar.</summary>
        const double MaxFitError = 38;
        /// <summary>A split counts as the bar's fill edge only when the two sides differ at least this much.</summary>
        const double MinContrast = 28; // a dim fill on some setups is only ~47 from the empty colour; noise at 0% stays under ~10

        static readonly string[] ProcessNames =
        {
            "PathOfExile", "PathOfExile_x64", "PathOfExileSteam", "PathOfExile_x64Steam",
            "PathOfExileEGS", "PathOfExile_x64EGS", "PathOfExile_KG", "PathOfExile_x64_KG",
        };

        public sealed class Reading
        {
            public double Fill;     // 0-1
            public double Error;    // mean colour distance of the columns from the colour they were assigned
            public int? Peak;       // before the colours are learned: the brightest column's brightest channel
            public bool Visible => Error <= MaxFitError;
        }

        /// <summary>Top-left of the game's client area on screen, or null when the game isn't running.</summary>
        public static Point? GameOrigin()
        {
            var window = GameWindow();
            if (window == IntPtr.Zero)
                return null;
            var origin = new POINT();
            ClientToScreen(window, ref origin);
            return new Point(origin.X, origin.Y);
        }

        static IntPtr GameWindow() =>
            ProcessNames.SelectMany(Process.GetProcessesByName).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);

        /// <summary>The calibrated strip's columns as they are on screen now, or null without the game or a calibration area.</summary>
        public static Color[] Columns(XpRegion region)
        {
            if (region.Width <= 4 || region.Height <= 0)
                return null;
            var origin = GameOrigin();
            if (origin == null)
                return null;
            try
            {
                using (var strip = Capture(new Rectangle(origin.Value.X + region.X, origin.Value.Y + region.Y, region.Width, region.Height)))
                    return ColumnColors(strip);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null; // the screen can't be copied (locked, a UAC prompt): no reading this time
            }
        }

        /// <summary>
        /// The bar now, or null when the game or the area is missing. Before the colours are learned, a read is
        /// only visible when the strip splits clearly in two; <paramref name="mayLearn"/> lets that split teach
        /// the colours (only once the runner is in a zone, so character select can't).
        /// </summary>
        public static Reading Read(XpRegion region, bool mayLearn = false)
        {
            if (!region.IsSet)
                return null;
            var columns = Columns(region);
            if (columns == null)
                return null;
            if (region.Learned)
                return Fit(columns, Color.FromArgb(region.Filled), Color.FromArgb(region.Empty));
            var unknown = new Reading { Fill = 0, Error = double.MaxValue };
            if (!mayLearn)
                return unknown; // outside a zone a strip that happens to split isn't evidence of anything
            var (split, left, right, atLimit) = Changepoint(columns);
            if (atLimit || Distance(left, right) < MinContrast || Brightest(left) <= Brightest(right))
            {
                var reading = EmptyBar(columns) ?? unknown; // no clear fill edge yet
                reading.Peak = columns.Max(c => Math.Max(c.R, Math.Max(c.G, c.B)));
                return reading;
            }
            region.Filled = left.ToArgb();
            region.Empty = right.ToArgb();
            return Fit(columns, left, right);
        }

        /// <summary>The brightest a column of a still-empty bar gets; PoE's empty bar is near black, its fill bright.</summary>
        const int EmptyAtMost = 70;

        /// <summary>A bar with a little fill already spreads ~5-9 from its mean; a truly empty one ~4. Anything in between is
        /// unknown until the colours are learned, rather than a wrong 0%.</summary>
        const double FlatAtMost = 5;

        /// <summary>A colour's brightest channel. The fill is the brighter side, so a dark empty bar that ends in a
        /// bright piece of frame isn't mistaken for a nearly full one.</summary>
        static int Brightest(Color c) => Math.Max(c.R, Math.Max(c.G, c.B));

        /// <summary>
        /// A fresh character's bar, at 0% in a zone: one even, dark colour with no edge to learn from. Read as empty
        /// until the first kills give it an edge. Never learned from, since it shows only one of the two colours.
        /// </summary>
        static Reading EmptyBar(Color[] columns)
        {
            var mean = Mean(columns, 0, columns.Length);
            var spread = columns.Average(c => Distance(c, mean));
            if (spread > FlatAtMost || columns.Any(c => Math.Max(c.R, Math.Max(c.G, c.B)) > EmptyAtMost))
                return null;
            return new Reading { Fill = 0, Error = spread };
        }

        /// <summary>The split into two parts with the most uniform colour each (least squared error), each at least
        /// 3% wide. A split right at that limit means the real edge may lie beyond it, so it isn't trusted.</summary>
        static (int Split, Color Left, Color Right, bool AtLimit) Changepoint(Color[] columns)
        {
            var n = columns.Length;
            var sum = new double[n + 1, 3];
            var squares = new double[n + 1];
            for (var i = 0; i < n; i++)
            {
                sum[i + 1, 0] = sum[i, 0] + columns[i].R;
                sum[i + 1, 1] = sum[i, 1] + columns[i].G;
                sum[i + 1, 2] = sum[i, 2] + columns[i].B;
                squares[i + 1] = squares[i] + columns[i].R * columns[i].R + columns[i].G * columns[i].G + columns[i].B * columns[i].B;
            }
            double Error(int from, int to)
            {
                var count = to - from;
                double r = sum[to, 0] - sum[from, 0], g = sum[to, 1] - sum[from, 1], b = sum[to, 2] - sum[from, 2];
                return squares[to] - squares[from] - (r * r + g * g + b * b) / count;
            }
            var min = Math.Max(3, n * 3 / 100);
            var best = n / 2;
            var bestError = double.MaxValue;
            for (var split = min; split <= n - min; split++)
            {
                var error = Error(0, split) + Error(split, n);
                if (error < bestError)
                {
                    bestError = error;
                    best = split;
                }
            }
            return (best, Mean(columns, 0, best), Mean(columns, best, n), best <= min || best >= n - min);
        }

        /// <summary>The split that best explains the columns as filled-then-empty, by squared colour distance.</summary>
        public static Reading Fit(Color[] columns, Color filled, Color empty)
        {
            var n = columns.Length;
            var toFilled = new double[n + 1];
            var toEmpty = new double[n + 1];
            for (var i = 0; i < n; i++)
            {
                toFilled[i + 1] = toFilled[i] + Distance(columns[i], filled);
                toEmpty[i + 1] = toEmpty[i] + Distance(columns[i], empty);
            }
            var best = 0;
            var bestCost = double.MaxValue;
            for (var split = 0; split <= n; split++)
            {
                var cost = toFilled[split] + (toEmpty[n] - toEmpty[split]);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = split;
                }
            }
            return new Reading { Fill = best / (double)n, Error = bestCost / n };
        }

        static double Distance(Color a, Color b)
        {
            int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        static Color Mean(Color[] columns, int from, int to)
        {
            long r = 0, g = 0, b = 0;
            for (var i = from; i < to; i++)
            {
                r += columns[i].R;
                g += columns[i].G;
                b += columns[i].B;
            }
            var n = Math.Max(1, to - from);
            return Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
        }

        public static Bitmap Capture(Rectangle screenArea)
        {
            var bitmap = new Bitmap(screenArea.Width, screenArea.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bitmap))
                g.CopyFromScreen(screenArea.Location, Point.Empty, screenArea.Size);
            return bitmap;
        }

        /// <summary>The average colour of each column of the strip.</summary>
        public static Color[] ColumnColors(Bitmap strip)
        {
            var data = strip.LockBits(new Rectangle(0, 0, strip.Width, strip.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var bytes = new byte[data.Stride * strip.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var columns = new Color[strip.Width];
                for (var x = 0; x < strip.Width; x++)
                {
                    long r = 0, g = 0, b = 0;
                    for (var y = 0; y < strip.Height; y++)
                    {
                        var i = y * data.Stride + x * 3;
                        b += bytes[i];
                        g += bytes[i + 1];
                        r += bytes[i + 2];
                    }
                    columns[x] = Color.FromArgb((int)(r / strip.Height), (int)(g / strip.Height), (int)(b / strip.Height));
                }
                return columns;
            }
            finally
            {
                strip.UnlockBits(data);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        /// <summary>Whether the game is the window in front, i.e. the runner hasn't tabbed out.</summary>
        public static bool GameIsForeground()
        {
            var front = GetForegroundWindow();
            return front != IntPtr.Zero && ProcessNames.SelectMany(Process.GetProcessesByName).Any(p => p.MainWindowHandle == front);
        }
    }
}
