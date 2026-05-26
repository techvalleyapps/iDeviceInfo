using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace iDeviceInfo
{
    /// <summary>
    /// Reads device info by scraping the 3uTools window every 2 seconds.
    /// Uses Win32 EnumChildWindows + GetWindowText to collect all visible
    /// text from 3uTools, then parses out Serial, IMEI, Battery etc.
    /// No MobileDevice.dll required — zero conflict with 3uTools.
    /// </summary>
    public sealed class DeviceWatcher : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────

        public event EventHandler<DeviceInfo>? DeviceConnected;
        public event EventHandler?             DeviceDisconnected;

        // ── State ─────────────────────────────────────────────────────────

        private System.Windows.Forms.Timer? _timer;
        private bool   _disposed;
        private bool   _devicePresent;
        private string _lastSerial = "";

        // ── Win32 ─────────────────────────────────────────────────────────

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(
            IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>Starts the 2-second scrape loop. Call from the UI thread.</summary>
        public void Start()
        {
            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += (s, e) => Scrape();
            _timer.Start();
            Scrape(); // immediate first check
        }

        /// <summary>Force an immediate re-scrape (e.g. from the Refresh menu item).</summary>
        public void Restart() => Scrape();

        // ── Scraping ──────────────────────────────────────────────────────

        private void Scrape()
        {
            var info = TryScrape3uTools();
            bool hasDevice = info != null;

            if (hasDevice && (!_devicePresent || info!.SerialNumber != _lastSerial))
            {
                // New device or changed device
                _devicePresent = true;
                _lastSerial    = info!.SerialNumber;
                DeviceConnected?.Invoke(this, info);
            }
            else if (!hasDevice && _devicePresent)
            {
                // Device gone (or 3uTools closed)
                _devicePresent = false;
                _lastSerial    = "";
                DeviceDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Returns a populated DeviceInfo if 3uTools is running AND showing a device,
        /// or null if no device is visible in 3uTools right now.
        /// </summary>
        private static DeviceInfo? TryScrape3uTools()
        {
            Process[] procs = Process.GetProcessesByName("3uTools");
            if (procs.Length == 0) return null;

            try
            {
                IntPtr hwnd = procs[0].MainWindowHandle;
                if (hwnd == IntPtr.Zero) return null;

                List<string> texts = CollectWindowText(hwnd);
                return ParseDeviceInfo(texts);
            }
            catch
            {
                return null;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        // ── Text collection ───────────────────────────────────────────────

        /// <summary>
        /// Walks every visible child window of the given HWND and collects
        /// their text via WM_GETTEXT. Qt widgets respond to this message.
        /// </summary>
        private static List<string> CollectWindowText(IntPtr root)
        {
            var results = new List<string>();

            EnumChildWindows(root, (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                int len = GetWindowTextLength(hwnd);
                if (len <= 0 || len > 1024) return true;

                var sb = new StringBuilder(len + 2);
                GetWindowText(hwnd, sb, sb.Capacity);
                string text = sb.ToString().Trim();

                if (!string.IsNullOrWhiteSpace(text))
                    results.Add(text);

                return true; // continue enumeration
            }, IntPtr.Zero);

            return results;
        }

        // ── Parsing ───────────────────────────────────────────────────────

        /// <summary>
        /// Parses a flat list of text strings (in UI order) from 3uTools
        /// into a DeviceInfo. Returns null if no device appears to be connected.
        /// </summary>
        private static DeviceInfo? ParseDeviceInfo(List<string> texts)
        {
            var info = new DeviceInfo();

            // ── Pass 1: label → next-value pairing ────────────────────────
            // 3uTools shows "Serial Number" then the value as the next text node,
            // or sometimes "Serial Number: XXXX" in one string.
            for (int i = 0; i < texts.Count; i++)
            {
                string raw   = texts[i];
                string lower = raw.ToLowerInvariant();

                // Handle "Label: Value" in a single string
                int colon = raw.IndexOf(':');
                if (colon > 0 && colon < raw.Length - 1)
                {
                    string lbl = raw[..colon].Trim().ToLowerInvariant();
                    string val = raw[(colon + 1)..].Trim();
                    ApplyLabel(info, lbl, val);
                    continue;
                }

                // Handle label on its own line, value on next line
                if (i + 1 < texts.Count)
                {
                    string nextVal = texts[i + 1].Trim();
                    ApplyLabel(info, lower, nextVal);
                }
            }

            // ── Pass 2: regex fallback on all strings ─────────────────────
            foreach (string t in texts)
            {
                // IMEI: exactly 15 digits
                if (info.IMEI == "N/A" && Regex.IsMatch(t, @"^\d{15}$"))
                    info.IMEI = t;

                // Serial: 10–15 uppercase alphanumeric chars (no spaces)
                if (string.IsNullOrEmpty(info.SerialNumber)
                    && Regex.IsMatch(t, @"^[A-Z0-9]{10,15}$")
                    && t != info.IMEI)
                    info.SerialNumber = t;

                // iOS version: e.g. "18.3.1"
                if (string.IsNullOrEmpty(info.iOSVersion)
                    && Regex.IsMatch(t, @"^\d{1,2}\.\d{1,2}(\.\d{1,2})?$"))
                    info.iOSVersion = t;

                // Battery percentage e.g. "84%"
                if (info.BatteryLevel == "N/A"
                    && Regex.IsMatch(t, @"^\d{1,3}%$"))
                    info.BatteryLevel = t;
            }

            // Only return an info object if we found at minimum a serial number
            // or an IMEI — otherwise 3uTools has no device connected
            bool hasDevice = !string.IsNullOrEmpty(info.SerialNumber)
                          || (info.IMEI != "N/A" && !string.IsNullOrEmpty(info.IMEI));

            return hasDevice ? info : null;
        }

        private static void ApplyLabel(DeviceInfo info, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            if (label.Contains("serial"))
                info.SerialNumber = value;
            else if (label.Contains("imei2") || label.Contains("imei 2"))
                info.IMEI2 = value;
            else if (label.Contains("imei"))
                info.IMEI = value;
            else if (label.Contains("battery life") || label.Contains("battery health")
                  || label.Contains("maximum capacity"))
                info.BatteryHealth = value;
            else if (label.Contains("battery"))
                info.BatteryLevel = value;
            else if (label.Contains("device name") || label.Contains("iphone name")
                  || label.Contains("ipad name")   || label.Contains("item title")
                  || label.Contains("phone name"))
                info.DeviceName = value;
            else if (label.Contains("ios version") || label.Contains("system version")
                  || label.Contains("software version"))
                info.iOSVersion = value;
            else if (label.Contains("model name") || label.Contains("device model"))
                info.ModelName = value;
        }

        // ── IDisposable ───────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}
