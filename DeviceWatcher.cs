using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace iDeviceInfo
{
    /// <summary>
    /// Reads device info by scraping the 3uTools window every 2 seconds.
    ///
    /// Strategy:
    ///   IAccessible (oleacc.dll) — walks Qt's logical accessibility tree.
    ///   Qt 5 on Windows exposes all label/value text through MSAA even though
    ///   it has no real Win32 child HWNDs (hence WM_GETTEXT returns nothing).
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
        private bool   _scraping;

        // ── IAccessible (MSAA) COM interface — InterfaceIsIDispatch lets us
        //   declare only the methods we need; Qt's IAccessible uses IDispatch. ──

        [ComImport]
        [Guid("618736e0-3c3d-11cf-810c-00aa00389b71")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IAccessible
        {
            [DispId(-5001)] int accChildCount { get; }

            [DispId(-5002)]
            [return: MarshalAs(UnmanagedType.Struct)]
            object? get_accChild([In, MarshalAs(UnmanagedType.Struct)] object varChild);

            [DispId(-5003)]
            [return: MarshalAs(UnmanagedType.BStr)]
            string? get_accName([In, MarshalAs(UnmanagedType.Struct)] object varChild);

            [DispId(-5004)]
            [return: MarshalAs(UnmanagedType.BStr)]
            string? get_accValue([In, MarshalAs(UnmanagedType.Struct)] object varChild);
        }

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            uint   dwObjectId,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

        private static readonly Guid IID_IAccessible =
            new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");

        private const uint OBJID_WINDOW = 0x00000000;
        private const int  CHILDID_SELF = 0;

        // ── Win32 — kept as extra fallback ────────────────────────────────

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

        public void Start()
        {
            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += async (s, e) => await ScrapeAsync();
            _timer.Start();
            _ = ScrapeAsync();
        }

        public void Restart() => _ = ScrapeAsync();

        // ── Scraping ──────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task ScrapeAsync()
        {
            if (_scraping) return;
            _scraping = true;

            DeviceInfo? info;
            try
            {
                info = await System.Threading.Tasks.Task.Run(() => TryScrape3uTools());
            }
            finally
            {
                _scraping = false;
            }

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

        private static DeviceInfo? TryScrape3uTools()
        {
            Process[] procs = Process.GetProcessesByName("3uTools");
            if (procs.Length == 0) return null;

            try
            {
                IntPtr hwnd = procs[0].MainWindowHandle;
                if (hwnd == IntPtr.Zero) return null;

                // Primary: IAccessible tree (reads Qt logical widget hierarchy)
                List<string> texts = CollectViaIAccessible(hwnd);

                // Fallback: WM_GETTEXT for any real child HWNDs
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

        // ── IAccessible collection ────────────────────────────────────────

        private static List<string> CollectViaIAccessible(IntPtr hwnd)
        {
            var results = new List<string>();
            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Guid iid = IID_IAccessible;
                if (AccessibleObjectFromWindow(hwnd, OBJID_WINDOW, ref iid, out object obj) != 0)
                    return results;

                if (obj is IAccessible root)
                    WalkAccessible(root, results, seen, depth: 0);
            }
            catch { }

            return results;
        }

        private static void WalkAccessible(
            IAccessible acc,
            List<string> results,
            HashSet<string> seen,
            int depth)
        {
            if (depth > 25 || results.Count >= 800) return;

            // Read Name and Value for this node (CHILDID_SELF = 0)
            try
            {
                string? name = acc.get_accName(CHILDID_SELF);
                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 512)
                {
                    string t = name.Trim();
                    if (seen.Add(t)) results.Add(t);
                }

                string? val = acc.get_accValue(CHILDID_SELF);
                if (!string.IsNullOrWhiteSpace(val) && val.Length <= 512)
                {
                    string t = val.Trim();
                    if (seen.Add(t)) results.Add(t);
                }
            }
            catch { }

            // Recurse into children
            int childCount;
            try { childCount = acc.accChildCount; }
            catch { return; }

            for (int i = 1; i <= childCount && results.Count < 800; i++)
            {
                try
                {
                    object? child = acc.get_accChild(i);
                    if (child is IAccessible childAcc)
                        WalkAccessible(childAcc, results, seen, depth + 1);
                    // child can also be an int (simple child ID within parent) — skip those
                }
                catch { }
            }
        }

        // ── WM_GETTEXT fallback ───────────────────────────────────────────

        private static List<string> CollectWindowText(IntPtr root)
        {
            var results = new List<string>();

            EnumChildWindows(root, (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                int len = GetWindowTextLength(hwnd);
                if (len <= 0 || len > 2048) return true;

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

            for (int i = 0; i < texts.Count; i++)
            {
                string raw   = texts[i];
                string lower = raw.ToLowerInvariant();

                int colon = raw.IndexOf(':');
                if (colon > 0 && colon < raw.Length - 1)
                {
                    ApplyLabel(info, raw[..colon].Trim().ToLowerInvariant(), raw[(colon + 1)..].Trim());
                    continue;
                }

                if (i + 1 < texts.Count)
                    ApplyLabel(info, lower, texts[i + 1].Trim());
            }

            foreach (string t in texts)
            {
                if (info.IMEI == "N/A" && Regex.IsMatch(t, @"^\d{15}$"))
                    info.IMEI = t;

                if (string.IsNullOrEmpty(info.SerialNumber)
                    && Regex.IsMatch(t, @"^[A-Z0-9]{10,15}$")
                    && t != info.IMEI)
                    info.SerialNumber = t;

                if (string.IsNullOrEmpty(info.iOSVersion)
                    && Regex.IsMatch(t, @"^\d{1,2}\.\d{1,2}(\.\d{1,2})?$"))
                    info.iOSVersion = t;

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
            else if (label.Contains("imei2") || label.Contains("imei 2") || label.Contains("imei_2"))
                info.IMEI2 = value;
            else if (label.Contains("imei"))
                info.IMEI = value;
            else if (label.Contains("battery life") || label.Contains("battery health")
                  || label.Contains("maximum capacity") || label.Contains("max capacity"))
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

        // ── Debug dump ────────────────────────────────────────────────────

        public static void DumpToFile()
        {
            Process[] procs = Process.GetProcessesByName("3uTools");
            if (procs.Length == 0)
            {
                MessageBox.Show(
                    "3uTools is not running.\n\nOpen 3uTools with an iPhone connected, then try again.",
                    "iDeviceInfo — Debug", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

                // IAccessible
                lines.Add("=== IAccessible (MSAA) ===");
                var accTexts = CollectViaIAccessible(hwnd);
                lines.Add($"Items: {accTexts.Count}");
                lines.Add("");
                for (int i = 0; i < accTexts.Count; i++)
                    lines.Add($"[{i,3}] {accTexts[i]}");

                // WM_GETTEXT
                lines.Add("");
                lines.Add("=== WM_GETTEXT Win32 ===");
                var wmTexts = CollectWindowText(hwnd);
                lines.Add($"Items: {wmTexts.Count}");
                lines.Add("");
                for (int i = 0; i < wmTexts.Count; i++)
                    lines.Add($"[{i,3}] {wmTexts[i]}");

                // AppData search
                lines.Add("");
                lines.Add("=== 3uTools AppData files ===");
                foreach (var dir in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),      "3uTools"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3uTools"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),      "3uTools9"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3uTools9"),
                    @"C:\Program Files\3uTools9\Data",
                    @"C:\Program Files\3uTools9\Resources",
                })
                {
                    if (!Directory.Exists(dir)) continue;
                    lines.Add($"DIR: {dir}");
                    try
                    {
                        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                            lines.Add($"  {f}  ({new FileInfo(f).Length} bytes, modified {File.GetLastWriteTime(f):yyyy-MM-dd HH:mm})");
                    }
                    catch (Exception ex) { lines.Add($"  (error: {ex.Message})"); }
                }

                // Parser result
                var combined = new List<string>(accTexts);
                foreach (var t in wmTexts) if (!combined.Contains(t)) combined.Add(t);
                var parsed = ParseDeviceInfo(combined);

                lines.Add("");
                lines.Add("=== Parser result ===");
                if (parsed == null)
                {
                    lines.Add("No device detected — Serial/IMEI not found.");
                    lines.Add("Share this file to fix the parser.");
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
