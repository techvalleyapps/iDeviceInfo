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
    /// Reads device info by scraping 3uTools every 2 seconds.
    ///
    /// 3uTools renders its UI inside QtWebEngine (embedded Chromium), so neither
    /// WM_GETTEXT nor IAccessible expose any text.  Instead we read the LevelDB
    /// files that QtWebEngine writes to LocalAppData — device info flows through
    /// localStorage/sessionStorage in real time.
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
            // 3uTools must be running
            Process[] procs = Process.GetProcessesByName("3uTools");
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (!running) return null;

            // Read device info from QtWebEngine LevelDB storage files
            List<string> texts = CollectFromLocalStorage();
            return ParseDeviceInfo(texts);
        }

        // ── LevelDB string extraction ─────────────────────────────────────

        private static readonly string s_storageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "3uTools", "QtWebEngine", "Default");

        /// <summary>
        /// Reads all LevelDB .log and .ldb files from 3uTools' QtWebEngine storage
        /// and extracts printable strings (both ASCII and UTF-16 LE).
        /// </summary>
        private static List<string> CollectFromLocalStorage()
        {
            var results = new List<string>();
            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] dirs =
            {
                Path.Combine(s_storageRoot, "Session Storage"),
                Path.Combine(s_storageRoot, "Local Storage", "leveldb"),
            };

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;

                var files = new List<string>();
                try { files.AddRange(Directory.GetFiles(dir, "*.log")); } catch { }
                try { files.AddRange(Directory.GetFiles(dir, "*.ldb")); } catch { }

                foreach (string file in files)
                {
                    // Skip tiny/empty bookkeeping files
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

        /// <summary>
        /// Reads a file that may be open/locked by 3uTools using FileShare.ReadWrite.
        /// Returns null if the file cannot be read.
        /// </summary>
        private static byte[]? TryReadShared(string path)
        {
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buf = new byte[fs.Length];
                fs.Read(buf, 0, buf.Length);
                return buf;
            }
            catch { return null; }
        }

        /// <summary>Extracts contiguous printable ASCII runs of at least minLen chars.</summary>
        private static IEnumerable<string> ExtractAsciiStrings(byte[] data, int minLen)
        {
            var sb = new StringBuilder();
            foreach (byte b in data)
            {
                if (b >= 0x20 && b < 0x7F)
                    sb.Append((char)b);
                else
                {
                    if (sb.Length >= minLen) yield return sb.ToString();
                    sb.Clear();
                }
            }
            if (sb.Length >= minLen) yield return sb.ToString();
        }

        /// <summary>
        /// Extracts UTF-16 LE strings (each char = lo byte printable ASCII + hi byte 0x00).
        /// Chromium's V8 stores many JS strings in UTF-16 LE inside LevelDB.
        /// </summary>
        private static IEnumerable<string> ExtractUtf16Strings(byte[] data, int minLen)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i + 1 < data.Length)
            {
                byte lo = data[i];
                byte hi = data[i + 1];
                if (lo >= 0x20 && lo < 0x7F && hi == 0x00)
                {
                    sb.Append((char)lo);
                    i += 2;
                }
                else
                {
                    if (sb.Length >= minLen) yield return sb.ToString();
                    sb.Clear();
                    i++;
                }
            }
            if (sb.Length >= minLen) yield return sb.ToString();
        }

        // ── Parsing ───────────────────────────────────────────────────────

        private static DeviceInfo? ParseDeviceInfo(List<string> texts)
        {
            var info = new DeviceInfo();

            // Pass 1: label → value pairs
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

            // Pass 2: regex fallback
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
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();

            var lines = new List<string>
            {
                $"iDeviceInfo Debug Dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"3uTools running: {running}",
                ""
            };

            // LevelDB strings
            lines.Add("=== Strings extracted from QtWebEngine LevelDB ===");
            var lsTexts = CollectFromLocalStorage();
            lines.Add($"Total unique strings: {lsTexts.Count}");
            lines.Add("");
            for (int i = 0; i < lsTexts.Count; i++)
                lines.Add($"[{i,4}] {lsTexts[i]}");

            // Parser result
            var parsed = ParseDeviceInfo(lsTexts);
            lines.Add("");
            lines.Add("=== Parser result ===");
            if (parsed == null)
            {
                lines.Add("No device detected — Serial/IMEI not found in LevelDB strings.");
                lines.Add("Share this file to tune the parser.");
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

            // File inventory
            lines.Add("");
            lines.Add("=== LevelDB file inventory ===");
            string[] dirs =
            {
                Path.Combine(s_storageRoot, "Session Storage"),
                Path.Combine(s_storageRoot, "Local Storage", "leveldb"),
            };
            foreach (string dir in dirs)
            {
                lines.Add($"DIR: {dir}");
                if (!Directory.Exists(dir)) { lines.Add("  (not found)"); continue; }
                try
                {
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        var fi = new FileInfo(f);
                        lines.Add($"  {fi.Name,-40} {fi.Length,8} bytes  {fi.LastWriteTime:HH:mm:ss}");
                    }
                }
                catch (Exception ex) { lines.Add($"  error: {ex.Message}"); }
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
