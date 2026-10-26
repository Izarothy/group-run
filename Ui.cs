using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GroupRun
{
    /// <summary>
    /// The app's dialogs in the board's look: near-black surfaces, Geist, 1px borders, white for the one thing that
    /// matters. Plain WinForms controls only where they're invisible (the text box inside a field).
    /// </summary>
    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(10, 10, 10);
        public static readonly Color Surface = Color.FromArgb(22, 22, 22);
        public static readonly Color Hover = Color.FromArgb(30, 30, 30);
        public static readonly Color Line = Color.FromArgb(44, 44, 44);
        public static readonly Color LineStrong = Color.FromArgb(70, 70, 70);
        public static readonly Color Fg = Color.FromArgb(237, 237, 237);
        public static readonly Color Muted = Color.FromArgb(143, 143, 143);
        public static readonly Color Faint = Color.FromArgb(92, 92, 92);
        public static readonly Color Accent = Color.FromArgb(82, 168, 255);

        public static readonly Font Body = Fonts.Sans(14);
        public static readonly Font BodyMedium = Fonts.SansMedium(14);
        public static readonly Font Title = Fonts.SansSemiBold(20);
        public static readonly Font Label = Fonts.MonoMedium(11);
        public static readonly Font Big = Fonts.MonoMedium(30);

        /// <summary>Dark surface, Geist, dark title bar.</summary>
        public static void Dress(Form form)
        {
            form.BackColor = Bg;
            form.ForeColor = Fg;
            form.Font = Body;
            form.Icon = AppIcon.Create();
            form.HandleCreated += (s, e) => DarkTitleBar(form.Handle);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void DarkTitleBar(IntPtr handle)
        {
            var dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
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

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        /// <summary>Small caps label above a control.</summary>
        public static System.Windows.Forms.Label Caption(string text) => new System.Windows.Forms.Label
        {
            Text = text.ToUpperInvariant(), Font = Label, ForeColor = Muted, AutoSize = true, Margin = new Padding(0, 14, 0, 6),
        };

        /// <summary>A short message with OK, or a question with two answers; returns true for the first.</summary>
        public static bool Tell(IWin32Window owner, string text, string yes = "OK", string no = null)
        {
            using (var dialog = new Form { FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, Text = "Group Run", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20) })
            {
                Dress(dialog);
                var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Bg };
                layout.Controls.Add(new System.Windows.Forms.Label { Text = text, AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Fg, Font = Body, Margin = new Padding(0, 0, 0, 18) });
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, BackColor = Bg, Margin = Padding.Empty };
                var ok = new UiButton { Text = yes, Primary = true, DialogResult = DialogResult.OK };
                buttons.Controls.Add(ok);
                if (no != null)
                    buttons.Controls.Add(new UiButton { Text = no, DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, 8, 0) });
                layout.Controls.Add(buttons);
                dialog.Controls.Add(layout);
                dialog.AcceptButton = ok;
                return dialog.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }

    /// <summary>Flat rounded button: outlined, or filled white for the primary action.</summary>
    sealed class UiButton : Control, IButtonControl
    {
        bool hover, down;
        public bool Primary;
        public DialogResult DialogResult { get; set; }

        public UiButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Font = Ui.BodyMedium;
            Cursor = Cursors.Hand;
            Height = 34;
            BackColor = Color.Transparent;
            Margin = Padding.Empty;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Width = TextRenderer.MeasureText(Text, Font).Width + 32;
        }

        public void NotifyDefault(bool value) { }

        public void PerformClick() => OnClick(EventArgs.Empty);

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var form = FindForm();
            if (form != null && DialogResult != DialogResult.None)
                form.DialogResult = DialogResult;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Smooth(g);
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Ui.Rounded(r, 6))
            {
                var fill = Primary ? (down ? Color.FromArgb(200, 200, 200) : hover ? Color.White : Ui.Fg) : (down ? Ui.Line : hover ? Ui.Hover : Ui.Bg);
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                if (!Primary)
                    using (var pen = new Pen(hover ? Ui.LineStrong : Ui.Line)) g.DrawPath(pen, path);
            }
            var color = !Enabled ? Ui.Faint : Primary ? Ui.Bg : Ui.Fg;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Ui.Accent, 2)) using (var path = Ui.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 6)) g.DrawPath(pen, path);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) PerformClick();
            base.OnKeyUp(e);
        }
    }

    /// <summary>A borderless text box in a rounded, dark frame that lights up on focus.</summary>
    sealed class UiField : Panel
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Ui.Surface, ForeColor = Ui.Fg, Font = Ui.Body };

        public UiField(int width)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(width, 36);
            Padding = new Padding(10, 9, 10, 0);
            Margin = Padding.Empty;
            BackColor = Ui.Bg;
            Box.Dock = DockStyle.Fill;
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Cursor = Cursors.IBeam;
            Click += (s, e) => Box.Focus();
        }

        public override string Text { get => Box.Text; set => Box.Text = value; }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Box.ForeColor = Enabled ? Ui.Fg : Ui.Faint;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Smooth(g);
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            using (var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 6))
            {
                using (var brush = new SolidBrush(Ui.Surface)) g.FillPath(brush, path);
                using (var pen = new Pen(Box.Focused ? Ui.Accent : Ui.Line)) g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>A row of options, one selected, like the board's own group-size switch.</summary>
    sealed class UiSegment : Control
    {
        readonly string[] options;
        int selected, hover = -1;
        public event EventHandler SelectedChanged;

        public UiSegment(params string[] options)
        {
            this.options = options;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Font = Ui.BodyMedium;
            Height = 36;
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
            var width = 6;
            foreach (var o in options) width += TextRenderer.MeasureText(o, Font).Width + 26;
            Width = width;
        }

        public int Selected
        {
            get => selected;
            set { if (value == selected) return; selected = value; Invalidate(); SelectedChanged?.Invoke(this, EventArgs.Empty); }
        }

        RectangleF Slot(int i)
        {
            float x = 3;
            for (var k = 0; k < i; k++) x += TextRenderer.MeasureText(options[k], Font).Width + 26;
            return new RectangleF(x, 3, TextRenderer.MeasureText(options[i], Font).Width + 26, Height - 6);
        }

        int At(Point p)
        {
            for (var i = 0; i < options.Length; i++) if (Slot(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { var h = At(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { var i = At(e.Location); if (i >= 0) Selected = i; Focus(); base.OnMouseDown(e); }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left && selected > 0) Selected--;
            if (e.KeyCode == Keys.Right && selected < options.Length - 1) Selected++;
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Smooth(g);
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            using (var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8))
            {
                using (var brush = new SolidBrush(Ui.Surface)) g.FillPath(brush, path);
                using (var pen = new Pen(Focused ? Ui.Accent : Ui.Line)) g.DrawPath(pen, path);
            }
            for (var i = 0; i < options.Length; i++)
            {
                var slot = Slot(i);
                if (i == selected)
                    using (var path = Ui.Rounded(slot, 6)) using (var brush = new SolidBrush(Ui.Fg)) g.FillPath(brush, path);
                else if (i == hover)
                    using (var path = Ui.Rounded(slot, 6)) using (var brush = new SolidBrush(Ui.Hover)) g.FillPath(brush, path);
                TextRenderer.DrawText(g, options[i], Font, Rectangle.Round(slot), i == selected ? Ui.Bg : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>A thin track with a round thumb; arrow keys step by one.</summary>
    sealed class UiSlider : Control
    {
        int value;
        public int Minimum = 0, Maximum = 100;
        public event EventHandler ValueChanged;

        public UiSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            Height = 28;
            Cursor = Cursors.Hand;
        }

        public int Value
        {
            get => value;
            set { var v = Math.Max(Minimum, Math.Min(Maximum, value)); if (v == this.value) return; this.value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
        }

        float Fraction => Maximum == Minimum ? 0 : (value - Minimum) / (float)(Maximum - Minimum);
        void SetFrom(int x) => Value = Minimum + (int)Math.Round((x - 9f) / (Width - 18f) * (Maximum - Minimum));

        protected override void OnMouseDown(MouseEventArgs e) { SetFrom(e.X); Focus(); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (e.Button == MouseButtons.Left) SetFrom(e.X); base.OnMouseMove(e); }
        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) Value--;
            if (e.KeyCode == Keys.Right) Value++;
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ui.Smooth(g);
            g.Clear(Parent?.BackColor ?? Ui.Bg);
            var y = Height / 2f;
            var x = 9 + Fraction * (Width - 18);
            using (var path = Ui.Rounded(new RectangleF(9, y - 2, Width - 18, 4), 2)) using (var brush = new SolidBrush(Ui.Line)) g.FillPath(brush, path);
            using (var path = Ui.Rounded(new RectangleF(9, y - 2, Math.Max(4, x - 9), 4), 2)) using (var brush = new SolidBrush(Ui.Fg)) g.FillPath(brush, path);
            using (var brush = new SolidBrush(Ui.Fg)) g.FillEllipse(brush, x - 8, y - 8, 16, 16);
            if (Focused)
                using (var pen = new Pen(Ui.Accent, 2)) g.DrawEllipse(pen, x - 10, y - 10, 20, 20);
        }
    }

    /// <summary>Dark right-click menu.</summary>
    sealed class UiMenuRenderer : ToolStripProfessionalRenderer
    {
        public UiMenuRenderer() : base(new Colors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ui.Fg : Ui.Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            Ui.Smooth(g);
            var r = e.ImageRectangle;
            using (var pen = new Pen(Ui.Accent, 2))
                g.DrawLines(pen, new[] { new PointF(r.Left + 3, r.Top + r.Height / 2f), new PointF(r.Left + r.Width / 2.6f, r.Bottom - 4), new PointF(r.Right - 3, r.Top + 4) });
        }

        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Ui.Surface;
            public override Color MenuBorder => Ui.Line;
            public override Color MenuItemBorder => Ui.Hover;
            public override Color MenuItemSelected => Ui.Hover;
            public override Color MenuItemSelectedGradientBegin => Ui.Hover;
            public override Color MenuItemSelectedGradientEnd => Ui.Hover;
            public override Color MenuItemPressedGradientBegin => Ui.Hover;
            public override Color MenuItemPressedGradientEnd => Ui.Hover;
            public override Color ImageMarginGradientBegin => Ui.Surface;
            public override Color ImageMarginGradientMiddle => Ui.Surface;
            public override Color ImageMarginGradientEnd => Ui.Surface;
            public override Color SeparatorDark => Ui.Line;
            public override Color SeparatorLight => Ui.Surface;
            public override Color CheckBackground => Ui.Surface;
            public override Color CheckSelectedBackground => Ui.Hover;
            public override Color CheckPressedBackground => Ui.Hover;
        }
    }
}
