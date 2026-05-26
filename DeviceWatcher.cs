using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Forms;

namespace iDeviceInfo
{
    /// <summary>
    /// Reads device info by scraping the 3uTools window every 2 seconds.
    ///
    /// Strategy (in order):
    ///   1. UIAutomation  — reads Qt accessibility tree (Name + ValuePattern)
    ///   2. WM_GETTEXT    — fallback for any plain Win32 child windows
    ///
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
        private bool   _scraping;          // re-entrancy guard

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
            _timer.Tick += async (s, e) => await ScrapeAsync();
            _timer.Start();
            _ = ScrapeAsync(); // immediate first check
        }

        /// <summary>Force an immediate re-scrape (e.g. from the Refresh menu item).</summary>
        public void Restart() => _ = ScrapeAsync();

        // ── Scraping ──────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task ScrapeAsync()
        {
            if (_scraping) return;
            _scraping = true;

            DeviceInfo? info;
            try
            {
                // UIAutomation can be slow — run off the UI thread
                info = await System.Threading.Tasks.Task.Run(() => TryScrape3uTools());
            }
            finally
            {
                _scraping = false;
            }

            // Back on UI thread (WinForms SynchronizationContext)
            bool hasDevice = info != null;

            if (hasDevice && (!_devicePresent || info!.SerialNumber != _lastSerial))
            {
                _devicePresent = true;
                _lastSerial    = info!.SerialNumber;
                DeviceConnected?.Invoke(this, info);
            }
            else if (!hasDevice && _devicePresent)
            {
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

                // Primary: UIAutomation (reads Qt accessibility layer)
                List<string> texts = CollectViaUIAutomation(hwnd);

                // Fallback: WM_GETTEXT — merge in anything UIAutomation missed
                foreach (var t in CollectWindowText(hwnd))
                    if (!texts.Contains(t, StringComparer.Ordinal))
                        texts.Add(t);

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

        // ── UIAutomation collection ────────────────────────────────────────

        /// <summary>
        /// Walks the UIAutomation accessibility tree of the given window handle.
        /// Qt 5 exposes all label/value text through IAccessible / UIA Name + ValuePattern.
        /// </summary>
        private static List<string> CollectViaUIAutomation(IntPtr hwnd)
        {
            var results = new List<string>();
            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                WalkTree(root, results, seen, depth: 0, maxDepth: 12, maxItems: 600);
            }
            catch { /* UIAutomation unavailable or window inaccessible */ }

            return results;
        }

        private static void WalkTree(
            AutomationElement el,
            List<string> results,
            HashSet<string> seen,
            int depth, int maxDepth, int maxItems)
        {
            if (depth > maxDepth || results.Count >= maxItems) return;

            try
            {
                // Name property (label text, button caption, etc.)
                string name = el.Current.Name;
                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 512)
                {
                    string trimmed = name.Trim();
                    if (seen.Add(trimmed)) results.Add(trimmed);
                }

                // ValuePattern (text-box / read-only field content)
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object vp))
                {
                    string val = ((ValuePattern)vp).Current.Value;
                    if (!string.IsNullOrWhiteSpace(val) && val.Length <= 512)
                    {
                        string trimmed = val.Trim();
                        if (seen.Add(trimmed)) results.Add(trimmed);
                    }
                }
            }
            catch { }

            // Recurse into children
            try
            {
                var walker = TreeWalker.RawViewWalker;
                var child  = walker.GetFirstChild(el);
                while (child != null && results.Count < maxItems)
                {
                    WalkTree(child, results, seen, depth + 1, maxDepth, maxItems);
                    child = walker.GetNextSibling(child);
                }
            }
            catch { }
        }

        // ── WM_GETTEXT fallback ────────────────────────────────────────────

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

                return true;
            }, IntPtr.Zero);

            return results;
        }

        // ── Parsing ───────────────────────────────────────────────────────

        private static DeviceInfo? ParseDeviceInfo(List<string> texts)
        {
            var info = new DeviceInfo();

            // ── Pass 1: label → next-value pairing ────────────────────────
            for (int i = 0; i < texts.Count; i++)
            {
                string raw   = texts[i];
                string lower = raw.ToLowerInvariant();

                // "Label: Value" in a single string
                int colon = raw.IndexOf(':');
                if (colon > 0 && colon < raw.Length - 1)
                {
                    string lbl = raw[..colon].Trim().ToLowerInvariant();
                    string val = raw[(colon + 1)..].Trim();
                    ApplyLabel(info, lbl, val);
                    continue;
                }

                // Label on its own, value on the next line
                if (i + 1 < texts.Count)
                    ApplyLabel(info, lower, texts[i + 1].Trim());
            }

            // ── Pass 2: regex fallback ─────────────────────────────────────
            foreach (string t in texts)
            {
                // IMEI: exactly 15 digits
                if (info.IMEI == "N/A" && Regex.IsMatch(t, @"^\d{15}$"))
                    info.IMEI = t;

                // Serial: 10–15 uppercase alphanumeric
                if (string.IsNullOrEmpty(info.SerialNumber)
                    && Regex.IsMatch(t, @"^[A-Z0-9]{10,15}$")
                    && t != info.IMEI)
                    info.SerialNumber = t;

                // iOS version: "18.3.1" etc.
                if (string.IsNullOrEmpty(info.iOSVersion)
                    && Regex.IsMatch(t, @"^\d{1,2}\.\d{1,2}(\.\d{1,2})?$"))
                    info.iOSVersion = t;

                // Battery percentage: "84%"
                if (info.BatteryLevel == "N/A" && Regex.IsMatch(t, @"^\d{1,3}%$"))
                    info.BatteryLevel = t;
            }

            bool hasDevice = !string.IsNullOrEmpty(info.SerialNumber)
                          || (info.IMEI != "N/A" && !string.IsNullOrEmpty(info.IMEI));

            return hasDevice ? info : null;
        }

        private static void ApplyLabel(DeviceInfo info, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            if (label.Contains("serial") || label is "s/n" or "sn")
                info.SerialNumber = value;
            else if (label.Contains("imei2") || label.Contains("imei 2")
                  || label.Contains("imei_2"))
                info.IMEI2 = value;
            else if (label.Contains("imei"))
                info.IMEI = value;
            else if (label.Contains("battery life") || label.Contains("battery health")
                  || label.Contains("maximum capacity") || label.Contains("max capacity")
                  || label.Contains("batterycapacity"))
                info.BatteryHealth = value;
            else if (label.Contains("battery"))
                info.BatteryLevel = value;
            else if (label.Contains("device name") || label.Contains("iphone name")
                  || label.Contains("ipad name")   || label.Contains("item title")
                  || label.Contains("phone name")  || label.Contains("devicename"))
                info.DeviceName = value;
            else if (label.Contains("ios version") || label.Contains("system version")
                  || label.Contains("software version") || label.Contains("iosversion"))
                info.iOSVersion = value;
            else if (label.Contains("model name") || label.Contains("device model")
                  || label.Contains("modelname"))
                info.ModelName = value;
        }

        // ── Debug dump ────────────────────────────────────────────────────

        /// <summary>
        /// Collects all text from 3uTools (UIAutomation + WM_GETTEXT),
        /// saves to iDeviceInfo_debug.txt on the Desktop, and opens it in Notepad.
        /// Use this to diagnose "no device" problems.
        /// </summary>
        public static void DumpToFile()
        {
            Process[] procs = Process.GetProcessesByName("3uTools");
            if (procs.Length == 0)
            {
                MessageBox.Show(
                    "3uTools is not running.\n\nOpen 3uTools with an iPhone connected, then try again.",
                    "iDeviceInfo — Debug",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                IntPtr hwnd = procs[0].MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                {
                    MessageBox.Show("3uTools window handle is zero.",
                        "iDeviceInfo — Debug", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var lines = new List<string>
                {
                    $"iDeviceInfo Debug Dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"3uTools HWND: 0x{hwnd:X}",
                    ""
                };

                lines.Add("=== UIAutomation (primary) ===");
                var uia = CollectViaUIAutomation(hwnd);
                lines.Add($"Items: {uia.Count}");
                lines.Add("");
                lines.AddRange(uia);

                lines.Add("");
                lines.Add("=== WM_GETTEXT Win32 (fallback) ===");
                var wm = CollectWindowText(hwnd);
                lines.Add($"Items: {wm.Count}");
                lines.Add("");
                lines.AddRange(wm);

                // Run parser and show what it found
                var combined = new List<string>(uia);
                foreach (var t in wm)
                    if (!combined.Contains(t)) combined.Add(t);
                var parsed = ParseDeviceInfo(combined);

                lines.Add("");
                lines.Add("=== Parser result ===");
                if (parsed == null)
                {
                    lines.Add("No device detected — Serial/IMEI not found in collected text.");
                    lines.Add("Share this file so we can fix the parser.");
                }
                else
                {
                    lines.Add($"DeviceName:    {parsed.DeviceName}");
                    lines.Add($"ModelName:     {parsed.ModelName}");
                    lines.Add($"iOSVersion:    {parsed.iOSVersion}");
                    lines.Add($"SerialNumber:  {parsed.SerialNumber}");
                    lines.Add($"IMEI:          {parsed.IMEI}");
                    lines.Add($"IMEI2:         {parsed.IMEI2}");
                    lines.Add($"BatteryLevel:  {parsed.BatteryLevel}");
                    lines.Add($"BatteryHealth: {parsed.BatteryHealth}");
                }

                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "iDeviceInfo_debug.txt");
                File.WriteAllLines(path, lines, Encoding.UTF8);

                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Debug dump failed:\n\n{ex.Message}",
                    "iDeviceInfo — Debug", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
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
