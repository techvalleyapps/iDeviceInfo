using System;
using System.Drawing;
using System.Windows.Forms;

namespace iDeviceInfo.Forms
{
    /// <summary>
    /// Shown when Apple's "Mobile Device Support" component (MobileDevice.dll /
    /// CoreFoundation.dll) isn't installed. "Install" opens the Apple Devices app
    /// listing in the Microsoft Store; "Later" just dismisses the popup.
    /// </summary>
    public sealed class MissingDependencyForm : Form
    {
        public const string AppleDevicesStoreUrl =
            "https://apps.microsoft.com/detail/9NP83LWLPZ9K";

        private static readonly Color Bg       = Color.FromArgb(30, 30, 30);
        private static readonly Color HeaderBg = Color.FromArgb(20, 20, 20);
        private static readonly Color Accent   = Color.FromArgb(10, 132, 255);
        private static readonly Color TextFg   = Color.White;
        private static readonly Color SubFg    = Color.FromArgb(160, 160, 160);
        private static readonly Color BtnBg    = Color.FromArgb(55, 55, 55);
        private static readonly Color BtnHover = Color.FromArgb(75, 75, 75);

        public MissingDependencyForm()
        {
            Text            = "iDeviceInfo";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ShowInTaskbar   = true;
            StartPosition   = FormStartPosition.CenterScreen;
            ClientSize      = new Size(380, 190);
            BackColor       = Bg;
            TopMost         = true;
            Icon            = MainForm.LoadAppIcon();

            var header = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = HeaderBg };
            var title = new Label
            {
                Text      = "⚠  Apple Support Required",
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
                Text = "iDeviceInfo needs the \"Apple Devices\" app (or iTunes) installed " +
                       "to read connected iPhones — it provides the USB driver Windows uses " +
                       "to talk to the device.",
                Font      = new Font("Segoe UI", 9f),
                ForeColor = SubFg,
                AutoSize  = false,
                Bounds    = new Rectangle(16, 56, 348, 70),
                TextAlign = ContentAlignment.TopLeft
            };

            var laterBtn   = MakeButton("Later", new Point(174, 140), 90);
            var installBtn = MakeButton("Install", new Point(274, 140), 90);
            installBtn.BackColor  = Accent;
            installBtn.MouseEnter += (_, _) => installBtn.BackColor = Color.FromArgb(40, 155, 255);
            installBtn.MouseLeave += (_, _) => installBtn.BackColor = Accent;

            laterBtn.Click   += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            installBtn.Click += (_, _) => { DialogResult = DialogResult.OK;     Close(); };

            Controls.Add(msg);
            Controls.Add(laterBtn);
            Controls.Add(installBtn);
            Controls.Add(header);

            AcceptButton = installBtn;
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
