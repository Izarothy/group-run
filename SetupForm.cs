using System;
using System.Drawing;
using System.Windows.Forms;

namespace GroupRun
{
    /// <summary>
    /// First-run and Settings dialog: name, Client.txt, runner or admin (join code to paste, or the hosted one to
    /// copy), XP bar calibration, on top, voice, buttons. Changes apply on Save.
    /// </summary>
    public sealed class SetupForm : Form
    {
        const int FieldWidth = 380;
        static readonly string[] OnTopValues = { "running", "always", "never" };

        readonly Settings settings;
        readonly string hostedCode;
        readonly UiField name = new UiField(FieldWidth), log = new UiField(FieldWidth - 88), code = new UiField(FieldWidth - 74);
        readonly UiSegment role = new UiSegment("Runner", "Admin");
        readonly UiSegment onTop = new UiSegment("While live", "Always", "Never");
        readonly UiSegment voice = new UiSegment("On", "Off");
        readonly UiSegment buttons = new UiSegment("Show", "Hide");
        readonly UiButton copy = new UiButton { Text = "Copy", Margin = new Padding(8, 1, 0, 0) };
        readonly Label xpState = new Label { AutoSize = true, Font = Ui.BodyMedium, Margin = new Padding(0, 9, 12, 0) };
        XpRegion xp;

        /// <param name="hostedCode">The admin's current join code, to copy; "" when not hosting.</param>
        public SetupForm(Settings settings, string hostedCode = "")
        {
            this.settings = settings;
            this.hostedCode = hostedCode;
            xp = settings.Xp;
            Text = "Group Run";
            Ui.Dress(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(0, 0, 24, 22);

            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Ui.Bg, Location = new Point(24, 6) };

            name.Text = settings.DisplayName;
            layout.Controls.Add(Ui.Caption("Name"));
            layout.Controls.Add(name);

            log.Text = settings.ClientLogPath;
            layout.Controls.Add(Ui.Caption("Client.txt"));
            var browse = new UiButton { Text = "Browse", Margin = new Padding(8, 1, 0, 0) };
            browse.Click += Browse;
            layout.Controls.Add(Row(log, browse));

            layout.Controls.Add(Ui.Caption("Role"));
            layout.Controls.Add(role);
            layout.Controls.Add(Ui.Caption("Join code"));
            copy.Click += (s, e) => { if (hostedCode.Length > 0) Clipboard.SetText(hostedCode); copy.Text = "Copied"; };
            layout.Controls.Add(Row(code, copy));
            role.SelectedChanged += (s, e) => ShowRole();
            role.Selected = settings.IsAdmin ? 1 : 0;
            ShowRole();

            layout.Controls.Add(Ui.Caption("XP bar"));
            var calibrate = new UiButton { Text = "Calibrate" };
            calibrate.Click += Calibrate;
            layout.Controls.Add(Row(xpState, calibrate));
            ShowXp();

            layout.Controls.Add(Ui.Caption("On top"));
            onTop.Selected = Math.Max(0, Array.IndexOf(OnTopValues, settings.OnTop));
            layout.Controls.Add(onTop);

            layout.Controls.Add(Ui.Caption("Voice"));
            voice.Selected = settings.Voice ? 0 : 1;
            layout.Controls.Add(voice);

            layout.Controls.Add(Ui.Caption("Buttons"));
            buttons.Selected = settings.Overlay ? 1 : 0;
            layout.Controls.Add(buttons);

            var save = new UiButton { Text = "Save", Primary = true, DialogResult = DialogResult.OK };
            save.Click += (s, e) =>
            {
                settings.DisplayName = name.Text.Trim();
                settings.ClientLogPath = log.Text.Trim();
                if (role.Selected == 0) settings.GroupCode = code.Text.Trim();
                settings.IsAdmin = role.Selected == 1;
                settings.Xp = xp;
                settings.OnTop = OnTopValues[onTop.Selected];
                settings.Voice = voice.Selected == 0;
                settings.Overlay = buttons.Selected == 1;
            };
            var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Width = FieldWidth, Height = 60, Margin = Padding.Empty, Padding = new Padding(0, 24, 0, 0), BackColor = Ui.Bg };
            footer.Controls.Add(save);
            layout.Controls.Add(footer);
            AcceptButton = save;
            Controls.Add(layout);
        }

        static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = Padding.Empty, BackColor = Ui.Bg };
            row.Controls.AddRange(controls);
            return row;
        }

        /// <summary>A runner pastes the admin's code; the admin sees their own to copy.</summary>
        void ShowRole()
        {
            var admin = role.Selected == 1;
            code.Box.ReadOnly = admin;
            code.Text = admin ? (hostedCode.Length > 0 ? hostedCode : "after restart") : settings.GroupCode;
            copy.Visible = admin && hostedCode.Length > 0;
        }

        void ShowXp()
        {
            xpState.Text = !xp.IsSet ? "Not set" : xp.Learned ? "Set" : "Set, learning";
            xpState.ForeColor = xp.IsSet ? Ui.Fg : Color.FromArgb(245, 165, 36);
        }

        void Calibrate(object sender, EventArgs e)
        {
            using (var form = new CalibrationForm(xp))
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    xp = form.Result;
                    ShowXp();
                }
        }

        void Browse(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Filter = "Client.txt|Client.txt" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    log.Text = dialog.FileName;
        }
    }
}
