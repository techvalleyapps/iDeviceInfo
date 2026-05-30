using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace iDeviceInfo.Forms
{
    /// <summary>
    /// A small floating "Copy All" button that hovers above the system tray
    /// whenever an iOS device is connected. Clicking it copies all device
    /// info to the clipboard.
    ///
    /// Visibility is driven entirely by UpdateDevice() — no process polling.
    /// </summary>
    public sealed class FloatingCopyButton : Form
    {
        // ── Win32 ─────────────────────────────────────────────────────────

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE     = 0x0001;
        private const uint SWP_NOMOVE     = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int  WS_EX_NOACTIVATE = 0x08000000;
        private const int  WS_EX_TOOLWINDOW = 0x00000080;

        // ── State ─────────────────────────────────────────────────────────

        private readonly Label  _btn;
        private DeviceInfo?     _currentDevice;

        // Colors
        private static readonly Color ColNormal  = Color.FromArgb(10,  132, 255);
        private static readonly Color ColHover   = Color.FromArgb(40,  155, 255);
        private static readonly Color ColPressed = Color.FromArgb(0,   100, 220);
        private static readonly Color ColDisabled = Color.FromArgb(80, 80,  80);
        private static readonly Color ColText    = Color.White;

        // Dimensions
        private const int BtnW        = 110;
        private const int BtnH        = 32;
        private const int MarginRight  = 14;
        private const int MarginBottom = 150;  // clears Windows notification toasts + extra 50 px

        // ── Constructor ───────────────────────────────────────────────────

        public FloatingCopyButton()
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor       = ColDisabled;
            Size            = new Size(BtnW, BtnH);
            ShowInTaskbar   = false;
            TopMost         = true;
            StartPosition   = FormStartPosition.Manual;
            Opacity         = 0.93;
            Visible         = false;   // hidden until a device connects

            PositionAboveTray();
            SetStyle(ControlStyles.Selectable, false);

            _btn = new Label
            {
                Text      = "📋  Copy All",
                Font      = new Font("Segoe UI Semibold", 9f),
                ForeColor = ColText,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor    = Cursors.Hand,
                BackColor = Color.Transparent
            };
            Controls.Add(_btn);

            _btn.MouseEnter += (_, _) => BackColor = ColHover;
            _btn.MouseLeave += (_, _) => BackColor = _currentDevice != null ? ColNormal : ColDisabled;
            _btn.MouseDown  += (_, _) => BackColor = ColPressed;
            _btn.MouseUp    += (_, e) => { BackColor = ColHover; if (e.Button == MouseButtons.Left) DoCopyAll(); };
            MouseUp         += (_, e) => { if (e.Button == MouseButtons.Left) DoCopyAll(); };
        }

        // ── Device update (called by TrayApplicationContext) ──────────────

        public void UpdateDevice(DeviceInfo? info)
        {
            _currentDevice = info;

            if (info != null)
            {
                _btn.Text = "📋  Copy All";
                BackColor = ColNormal;
                _btn.Font = new Font("Segoe UI Semibold", 9f);

                if (!Visible)
                {
                    Show();
                    PositionAboveTray();
                    KeepTopmost();
                }
            }
            else
            {
                Hide();
            }
        }

        // ── Copy action ───────────────────────────────────────────────────

        private void DoCopyAll()
        {
            if (_currentDevice == null)
            {
                ShowFlash("No device!", Color.FromArgb(200, 60, 60));
                return;
            }
            try
            {
                Clipboard.SetText(_currentDevice.ToClipboardText());
                ShowFlash("Copied!", Color.FromArgb(52, 199, 89));
            }
            catch
            {
                ShowFlash("Error", Color.FromArgb(200, 60, 60));
            }
        }

        private async void ShowFlash(string message, Color color)
        {
            string origText  = _btn.Text;
            Color  origColor = BackColor;

            _btn.Text = message;
            BackColor = color;

            await System.Threading.Tasks.Task.Delay(1200);

            if (!IsDisposed)
            {
                _btn.Text = origText;
                BackColor = _currentDevice != null ? ColNormal : ColDisabled;
            }
        }

        // ── Positioning ───────────────────────────────────────────────────

        private void PositionAboveTray()
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Right - BtnW - MarginRight,
                                 wa.Bottom - BtnH - MarginBottom);
        }

        private void KeepTopmost()
            => SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // ── Focus / activation prevention ────────────────────────────────

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // ── Rounded corners ───────────────────────────────────────────────

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            var path = new GraphicsPath();
            int r = 8;
            path.AddArc(0,           0,            r * 2, r * 2, 180, 90);
            path.AddArc(Width - r*2, 0,            r * 2, r * 2, 270, 90);
            path.AddArc(Width - r*2, Height - r*2, r * 2, r * 2,   0, 90);
            path.AddArc(0,           Height - r*2, r * 2, r * 2,  90, 90);
            path.CloseFigure();
            Region = new Region(path);
        }

        // ── Cleanup ───────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            // Nothing extra to dispose — no timer, no polling
            base.Dispose(disposing);
        }
    }
}
