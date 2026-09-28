using System;
using System.Drawing;
using System.Windows.Forms;

namespace iDeviceInfo.Forms
{
    /// <summary>
    /// Small popup shown when a newer release is found. "Update Now" triggers a silent
    /// download + install (handled by the caller); "Later" just dismisses the popup.
    /// </summary>
    public sealed class UpdateAvailableForm : Form
    {
        private static readonly Color Bg      = Color.FromArgb(30, 30, 30);
        private static readonly Color HeaderBg = Color.FromArgb(20, 20, 20);
        private static readonly Color Accent  = Color.FromArgb(10, 132, 255);
        private static readonly Color TextFg  = Color.White;
        private static readonly Color SubFg   = Color.FromArgb(160, 160, 160);
        private static readonly Color BtnBg   = Color.FromArgb(55, 55, 55);
        private static readonly Color BtnHover = Color.FromArgb(75, 75, 75);

        public UpdateAvailableForm(string tagName)
        {
            Text            = "iDeviceInfo Update";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = true;
            StartPosition   = FormStartPosition.CenterScreen;
            ClientSize      = new Size(360, 170);
            BackColor       = Bg;
            TopMost         = true;
            Icon            = MainForm.LoadAppIcon();

            var header = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = HeaderBg };
            var title = new Label
            {
                Text      = "⬆  Update Available",
                Font      = new Font("Segoe UI Semibold", 11f),
                ForeColor = TextFg,
                AutoSize  = false,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(16, 0, 0, 0)
            };
            header.Controls.Add(title);

            var msg = new Label
            {
                Text      = $"iDeviceInfo {tagName} is available.\nUpdate now to install it — this only takes a moment.",
                Font      = new Font("Segoe UI", 9f),
                ForeColor = SubFg,
                AutoSize  = false,
                Bounds    = new Rectangle(16, 56, 328, 50),
                TextAlign = ContentAlignment.TopLeft
            };

            var laterBtn  = MakeButton("Later", new Point(154, 120), 90);
            var updateBtn = MakeButton("Update Now", new Point(254, 120), 106);
            updateBtn.BackColor = Accent;
            updateBtn.MouseEnter += (_, _) => updateBtn.BackColor = Color.FromArgb(40, 155, 255);
            updateBtn.MouseLeave += (_, _) => updateBtn.BackColor = Accent;

            laterBtn.Click  += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            updateBtn.Click += (_, _) => { DialogResult = DialogResult.OK;     Close(); };

            Controls.Add(msg);
            Controls.Add(laterBtn);
            Controls.Add(updateBtn);
            Controls.Add(header);

            AcceptButton = updateBtn;
            CancelButton = laterBtn;
        }

        private static Button MakeButton(string text, Point location, int width)
        {
            var b = new Button
            {
                Text      = text,
                Location  = location,
                Size      = new Size(width, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = BtnBg,
                Font      = new Font("Segoe UI", 9f),
                Cursor    = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = BtnBg;
            b.MouseEnter += (_, _) => { if (b.BackColor != Accent) b.BackColor = BtnHover; };
            b.MouseLeave += (_, _) => { if (b.BackColor != Accent) b.BackColor = BtnBg; };
            return b;
        }
    }
}
