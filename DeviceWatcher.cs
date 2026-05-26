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
    /// Reads device info from 3uTools every 2 seconds.
    ///
    /// 3uTools uses QtWebEngine (embedded Chromium) for its UI, so WM_GETTEXT and
    /// IAccessible return nothing.  Instead we read the LevelDB localStorage/
    /// sessionStorage files that QtWebEngine writes in real time — they contain a
    /// "BasicsData" JSON blob with every device field we need.
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
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (!running) return null;

            List<string> texts = CollectFromLocalStorage();
            return ParseDeviceInfo(texts);
        }

        // ── LevelDB reading ───────────────────────────────────────────────

        private static readonly string StorageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "3uTools", "QtWebEngine", "Default");

        private static List<string> CollectFromLocalStorage()
        {
            var results = new List<string>();
            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] dirs =
            {
                Path.Combine(StorageRoot, "Session Storage"),
                Path.Combine(StorageRoot, "Local Storage", "leveldb"),
            };

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;

                var files = new List<string>();
                try { files.AddRange(Directory.GetFiles(dir, "*.log")); } catch { }
                try { files.AddRange(Directory.GetFiles(dir, "*.ldb")); } catch { }

                foreach (string file in files)
                {
                    try { if (new FileInfo(file).Length < 10) continue; } catch { continue; }

                    byte[]? data = TryReadShared(file);
                    if (data == null) continue;

                    foreach (string s in ExtractAsciiStrings(data, minLen: 5))
                        if (seen.Add(s)) results.Add(s);

                    foreach (string s in ExtractUtf16Strings(data, minLen: 5))
                        if (seen.Add(s)) results.Add(s);
                }
            }

            return results;
        }

        private static byte[]? TryReadShared(string path)
        {
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[fs.Length];
                _ = fs.Read(buf, 0, buf.Length);
                return buf;
            }
            catch { return null; }
        }

        private static IEnumerable<string> ExtractAsciiStrings(byte[] data, int minLen)
        {
            var sb = new StringBuilder();
            foreach (byte b in data)
            {
                if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
                else
                {
                    if (sb.Length >= minLen) yield return sb.ToString();
                    sb.Clear();
                }
            }
            if (sb.Length >= minLen) yield return sb.ToString();
        }

        private static IEnumerable<string> ExtractUtf16Strings(byte[] data, int minLen)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i + 1 < data.Length)
            {
                byte lo = data[i], hi = data[i + 1];
                if (lo >= 0x20 && lo < 0x7F && hi == 0x00) { sb.Append((char)lo); i += 2; }
                else
                {
                    if (sb.Length >= minLen) yield return sb.ToString();
                    sb.Clear(); i++;
                }
            }
            if (sb.Length >= minLen) yield return sb.ToString();
        }

        // ── JSON field extraction ─────────────────────────────────────────
        // 3uTools stores device data as a BasicsData JSON blob in localStorage.
        // The blob may be split across multiple extracted strings (LevelDB records
        // break at newlines), so we run the regexes against every string that
        // looks JSON-like and take the first match found for each field.

        // Each regex targets the *direct* JSON string value  "key":"value"
        // (the key must end with :" to avoid matching nested object keys).

        private static readonly Regex ReDeviceName  = new(@"""deviceName""\s*:\s*""([^""]{1,120})""",   RegexOptions.Compiled);
        private static readonly Regex ReImei        = new(@"""imei""\s*:\s*""(\d{14,16})""",             RegexOptions.Compiled);
        private static readonly Regex ReImei2       = new(@"""imei2""\s*:\s*""(\d{14,16})""",            RegexOptions.Compiled);
        // "serial":"CF9G2X7GK6"  — but NOT "key_serial":{...}  (the :" guard works)
        private static readonly Regex ReSerial      = new(@"(?<![_a-z])""serial""\s*:\s*""([A-Z0-9]{6,20})""", RegexOptions.Compiled);
        private static readonly Regex ReBatLife     = new(@"""batLife""\s*:\s*""(\d{1,3})""",            RegexOptions.Compiled);
        private static readonly Regex ReProductType = new(@"""productType""\s*:\s*""([^""]{1,60})""",    RegexOptions.Compiled);
        private static readonly Regex ReBuildVer    = new(@"""buildver""\s*:\s*""([A-Z0-9]{3,10})""",    RegexOptions.Compiled);
        // Model from key_model.read
        private static readonly Regex ReModelRead   = new(@"""key_model""\s*:\s*\{[^}]*?""read""\s*:\s*""([^""]{1,80})""", RegexOptions.Compiled);
        // Also accept deviceName as model name when no key_model present
        private static readonly Regex ReColor       = new(@"""color""\s*:\s*""([^""]{1,80})""",          RegexOptions.Compiled);

        private static DeviceInfo? ParseDeviceInfo(List<string> texts)
        {
            var info = new DeviceInfo();

            foreach (string t in texts)
            {
                // Only bother scanning strings that look like 3uTools JSON
                if (!t.Contains('"')) continue;

                Match m;

                if (info.DeviceName == "Unknown Device")
                {
                    m = ReDeviceName.Match(t);
                    if (m.Success) info.DeviceName = m.Groups[1].Value.Trim();
                }

                if (info.IMEI == "N/A")
                {
                    m = ReImei.Match(t);
                    if (m.Success) info.IMEI = m.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(info.IMEI2))
                {
                    m = ReImei2.Match(t);
                    if (m.Success) info.IMEI2 = m.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(info.SerialNumber))
                {
                    m = ReSerial.Match(t);
                    if (m.Success) info.SerialNumber = m.Groups[1].Value;
                }

                if (info.BatteryHealth == "N/A")
                {
                    m = ReBatLife.Match(t);
                    if (m.Success) info.BatteryHealth = m.Groups[1].Value + "%";
                }

                if (string.IsNullOrEmpty(info.ProductType))
                {
                    m = ReProductType.Match(t);
                    if (m.Success) info.ProductType = m.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(info.iOSVersion))
                {
                    m = ReBuildVer.Match(t);
                    if (m.Success) info.iOSVersion = m.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(info.ModelName))
                {
                    m = ReModelRead.Match(t);
                    if (m.Success) info.ModelName = m.Groups[1].Value;
                }

                // Colour stored in ProductType's sibling field — use as subtitle hint
                if (string.IsNullOrEmpty(info.ProductType))
                {
                    m = ReColor.Match(t);
                    if (m.Success && !m.Groups[1].Value.StartsWith("Front"))
                    {
                        // only for strings like "Natural Titanium", not "Front Black\nRear …"
                    }
                }
            }

            // Fall back: if ModelName empty, use DeviceName
            if (string.IsNullOrEmpty(info.ModelName) && info.DeviceName != "Unknown Device")
                info.ModelName = info.DeviceName;

            bool hasDevice = !string.IsNullOrEmpty(info.SerialNumber)
                          || (info.IMEI != "N/A" && !string.IsNullOrEmpty(info.IMEI));

            return hasDevice ? info : null;
        }

        // ── Debug dump ────────────────────────────────────────────────────

        public static void DumpToFile()
        {
            Process[] procs = Process.GetProcessesByName("3uTools");
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();

            var lines = new List<string>
            {
                $"iDeviceInfo Debug Dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"3uTools running: {running}",
                ""
            };

            var texts = CollectFromLocalStorage();
            lines.Add($"=== LevelDB strings ({texts.Count} unique) ===");
            lines.Add("");
            for (int i = 0; i < texts.Count; i++)
                lines.Add($"[{i,4}] {texts[i]}");

            var parsed = ParseDeviceInfo(texts);
            lines.Add("");
            lines.Add("=== Parser result ===");
            if (parsed == null)
            {
                lines.Add("No device detected.");
            }
            else
            {
                lines.Add($"DeviceName:    {parsed.DeviceName}");
                lines.Add($"ModelName:     {parsed.ModelName}");
                lines.Add($"iOSVersion:    {parsed.iOSVersion}");
                lines.Add($"SerialNumber:  {parsed.SerialNumber}");
                lines.Add($"IMEI:          {parsed.IMEI}");
                lines.Add($"IMEI2:         {parsed.IMEI2}");
                lines.Add($"BatteryHealth: {parsed.BatteryHealth}");
                lines.Add($"BatteryLevel:  {parsed.BatteryLevel}");
                lines.Add($"ProductType:   {parsed.ProductType}");
            }

            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "iDeviceInfo_debug.txt");
                File.WriteAllLines(path, lines, Encoding.UTF8);
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"")
                    { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save debug file:\n{ex.Message}",
                    "iDeviceInfo", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
