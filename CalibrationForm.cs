using System;
using System.Drawing;
using System.Windows.Forms;

namespace GroupRun
{
    /// <summary>
    /// Drag a box over the XP bar on a dimmed copy of the screen. The bar's colours are learned later, from the bar.
    /// </summary>
    public sealed class CalibrationForm : Form
    {
        public XpRegion Result { get; private set; }

        readonly Point gameOrigin;
        readonly Bitmap screenshot;
        Point dragStart;
        Rectangle drag;

        public CalibrationForm(XpRegion current)
        {
            Result = current.Copy();
            gameOrigin = XpBar.GameOrigin() ?? Point.Empty;

            var screen = Screen.FromPoint(gameOrigin).Bounds;
            screenshot = XpBar.Capture(screen);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen;
            TopMost = true;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawImage(screenshot, 0, 0);
            using (var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                e.Graphics.FillRectangle(shade, ClientRectangle);
            if (drag.Width > 0)
            {
                e.Graphics.DrawImage(screenshot, drag, drag, GraphicsUnit.Pixel);
                using (var pen = new Pen(Ui.Accent, 2))
                    e.Graphics.DrawRectangle(pen, drag);
            }
            var hint = XpBar.GameOrigin() == null ? "Path of Exile isn't running  ·  Esc" : "Drag over the XP bar  ·  Esc";
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var brush = new SolidBrush(Ui.Fg))
                e.Graphics.DrawString(hint, Ui.Title, brush, 28, 24);
        }

        protected override void OnMouseDown(MouseEventArgs e) => dragStart = e.Location;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            drag = Rectangle.FromLTRB(Math.Min(dragStart.X, e.X), Math.Min(dragStart.Y, e.Y), Math.Max(dragStart.X, e.X), Math.Max(dragStart.Y, e.Y));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (drag.Width < 20 || drag.Height < 1)
                return;
            var screen = Bounds.Location;
            Result.X = screen.X + drag.X - gameOrigin.X;
            Result.Y = screen.Y + drag.Y - gameOrigin.Y;
            Result.Width = drag.Width;
            Result.Height = drag.Height;
            Result.Filled = Result.Empty = 0; // a new area: learn its colours again
            DialogResult = DialogResult.OK;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                screenshot.Dispose();
            base.Dispose(disposing);
        }
    }
}
