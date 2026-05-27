using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using iDeviceInfo.Native;

namespace iDeviceInfo
{
    /// <summary>
    /// Reads device info from 3uTools every 2 seconds.
    ///
    /// 3uTools uses QtWebEngine (embedded Chromium) for its UI, so WM_GETTEXT and
    /// IAccessible return nothing.  Instead we read the LevelDB localStorage/
    /// sessionStorage files that QtWebEngine writes in real time — they contain a
    /// "BasicsData" JSON blob with every device field we need.
    ///
    /// To select the RIGHT blob, we subscribe to AMDeviceNotificationSubscribe so
    /// we always know the serial of the physically-connected device.  That serial is
    /// used to filter LevelDB blobs, preventing stale cached data from a previous
    /// device from winning the "latest checkDate" race.
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

        // AMD notification state:
        //   _amdAvailable = false  → MobileDevice.dll absent; pure LevelDB checkDate fallback.
        //   _amdAvailable = true:
        //     _amdSerial == null   → AMD says no device is physically connected.
        //     _amdSerial == ""     → Device connected but serial unreadable; checkDate fallback.
        //     _amdSerial == "XYZ"  → Device connected, serial known; filter blobs by serial.
        private bool             _amdAvailable;
        private volatile string? _amdSerial;       // written on UI thread, read on thread-pool
        private IntPtr           _amdSubscription = IntPtr.Zero;
        private AMD.DeviceNotificationCallback? _amdCallback; // keep delegate alive; GC must not collect it

        // ── Public API ────────────────────────────────────────────────────

        public void Start()
        {
            // Subscribe to AMDevice connect/disconnect events.
            // Must be called after Application.Run() has started the Win32 message pump
            // (AMDeviceNotificationSubscribe posts WM messages to deliver callbacks).
            _amdCallback = OnAmdNotification;
            try
            {
                int result = AMD.AMDeviceNotificationSubscribe(
                    _amdCallback, 0, 0, IntPtr.Zero, out _amdSubscription);
                _amdAvailable = (result == 0 && _amdSubscription != IntPtr.Zero);
            }
            catch
            {
                // MobileDevice.dll not installed (no iTunes / Apple Devices app).
                // Degrade gracefully: pure LevelDB checkDate heuristic.
                _amdAvailable    = false;
                _amdSubscription = IntPtr.Zero;
            }

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += async (s, e) => await ScrapeAsync();
            _timer.Start();
            _ = ScrapeAsync();
        }

        public void Restart() => _ = ScrapeAsync();

        // ── AMD notification callback ─────────────────────────────────────

        private void OnAmdNotification(ref AMD.DeviceCallbackInfo info, IntPtr cookie)
        {
            if (info.Message == AMD.MSG_CONNECTED)
            {
                _amdSerial = ReadSerialFromDevice(info.Device) ?? "";
                _ = ScrapeAsync();
            }
            else if (info.Message == AMD.MSG_DISCONNECTED)
            {
                _amdSerial = null;
                _ = ScrapeAsync();
            }
        }

        /// <summary>
        /// Reads SerialNumber directly from the iOS device via lockdown.
        /// Returns null if the device is locked, not yet trusted, or any error occurs.
        /// </summary>
        private static string? ReadSerialFromDevice(IntPtr device)
        {
            try
            {
                if (AMD.AMDeviceConnect(device) != 0) return null;

                // Try without a full lockdown session first (works if already trusted).
                IntPtr keyRef = CF.ToCFString("SerialNumber");
                IntPtr valRef = AMD.AMDeviceCopyValue(device, IntPtr.Zero, keyRef);
                CF.CFRelease(keyRef);

                if (valRef == IntPtr.Zero)
                {
                    // Need a full lockdown session.
                    if (AMD.AMDeviceValidatePairing(device) != 0 ||
                        AMD.AMDeviceStartSession(device)    != 0)
                    {
                        AMD.AMDeviceDisconnect(device);
                        return null;
                    }

                    keyRef = CF.ToCFString("SerialNumber");
                    valRef = AMD.AMDeviceCopyValue(device, IntPtr.Zero, keyRef);
                    CF.CFRelease(keyRef);
                    AMD.AMDeviceStopSession(device);
                }

                string? serial = null;
                if (valRef != IntPtr.Zero)
                {
                    serial = CF.CFValueToString(valRef);
                    CF.CFRelease(valRef);
                }

                AMD.AMDeviceDisconnect(device);
                return string.IsNullOrEmpty(serial) ? null : serial;
            }
            catch { return null; }
        }

        // ── Scraping ──────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task ScrapeAsync()
        {
            if (_scraping) return;
            _scraping = true;

            // Snapshot AMD state on the UI thread before jumping to the thread pool.
            bool    amdAvailable = _amdAvailable;
            string? amdSerial    = _amdSerial;

            DeviceInfo? info;
            try
            {
                info = await System.Threading.Tasks.Task.Run(
                    () => TryScrape3uTools(amdAvailable, amdSerial));
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

        private static DeviceInfo? TryScrape3uTools(bool amdAvailable, string? amdSerial)
        {
            Process[] procs = Process.GetProcessesByName("3uTools");
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (!running) return null;

            // If AMD is available and definitively says no device is connected,
            // don't let stale LevelDB data from a previous session report a ghost device.
            if (amdAvailable && amdSerial == null) return null;

            List<string> texts = CollectFromLocalStorage();
            return ParseDeviceInfo(texts, amdSerial);
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

        // ── JSON field regexes ────────────────────────────────────────────

        private static readonly Regex ReCheckDate   = new(@"""checkDate""\s*:\s*""(\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2})""", RegexOptions.Compiled);
        private static readonly Regex ReDeviceName  = new(@"""deviceName""\s*:\s*""([^""]{1,120})""",   RegexOptions.Compiled);
        private static readonly Regex ReImei        = new(@"""imei""\s*:\s*""(\d{14,16})""",             RegexOptions.Compiled);
        private static readonly Regex ReImei2       = new(@"""imei2""\s*:\s*""(\d{14,16})""",            RegexOptions.Compiled);
        private static readonly Regex ReSerial      = new(@"(?<![_a-z])""serial""\s*:\s*""([A-Z0-9]{6,20})""", RegexOptions.Compiled);
        private static readonly Regex ReBatLife     = new(@"""batLife""\s*:\s*""(\d{1,3})""",            RegexOptions.Compiled);
        private static readonly Regex ReProductType = new(@"""productType""\s*:\s*""([^""]{1,60})""",    RegexOptions.Compiled);
        private static readonly Regex ReBuildVer    = new(@"""buildver""\s*:\s*""([A-Z0-9]{3,10})""",    RegexOptions.Compiled);
        private static readonly Regex ReModelRead   = new(@"""key_model""\s*:\s*\{[^}]*?""read""\s*:\s*""([^""]{1,80})""", RegexOptions.Compiled);

        // ── JSON parsing ──────────────────────────────────────────────────

        /// <summary>
        /// Selects the correct device blob(s) from the LevelDB string pool and extracts
        /// device fields from them.
        ///
        /// Root cause of the "old device shown on new connection" bug:
        ///   3uTools refreshes cached scan records for ALL previously seen devices every
        ///   time any device is checked — not just the active one.  This means the old
        ///   device's LevelDB entry can receive a newer checkDate than the freshly
        ///   connected device's entry, making "latest checkDate wins" unreliable.
        ///
        /// Fix:
        ///   When we know the serial of the physically-connected device (via AMD), we
        ///   filter blobs by that serial and ignore every other device's data entirely.
        ///   We collect ALL blobs containing the serial, sort them newest-first, and
        ///   pass them all to ExtractFields so fields truncated in a newer WAL entry are
        ///   backfilled from an older complete blob.
        /// </summary>
        private static DeviceInfo? ParseDeviceInfo(List<string> texts, string? knownSerial = null)
        {
            if (!string.IsNullOrEmpty(knownSerial))
            {
                var blobsForSerial = texts
                    .Where(t => t.Contains(knownSerial, StringComparison.Ordinal))
                    .Select(t =>
                    {
                        var m  = ReCheckDate.Match(t);
                        var dt = DateTime.MinValue;
                        if (m.Success)
                            DateTime.TryParseExact(
                                m.Groups[1].Value, "MM/dd/yyyy HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out dt);
                        return (blob: t, date: dt);
                    })
                    .OrderByDescending(x => x.date)
                    .Select(x => x.blob)
                    .ToList();

                if (blobsForSerial.Count > 0)
                    return ExtractFields(blobsForSerial);

                // 3uTools hasn't scanned this device yet — return null so the timer retries.
                return null;
            }

            // AMD serial unavailable (dll missing or device locked/untrusted).
            // Fall back to the original "latest checkDate" heuristic.
            string?  latestBlob = null;
            DateTime latestDate = DateTime.MinValue;

            foreach (string t in texts)
            {
                var m = ReCheckDate.Match(t);
                if (!m.Success) continue;

                if (DateTime.TryParseExact(
                        m.Groups[1].Value, "MM/dd/yyyy HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime dt)
                    && dt > latestDate)
                {
                    latestDate = dt;
                    latestBlob = t;
                }
            }

            return ExtractFields(latestBlob != null
                ? new List<string> { latestBlob }
                : texts);
        }

        private static DeviceInfo? ExtractFields(List<string> texts)
        {
            var info = new DeviceInfo();

            foreach (string t in texts)
            {
                if (!t.Contains('"')) continue;
                Match m;

                if (info.DeviceName == "Unknown Device")
                { m = ReDeviceName.Match(t); if (m.Success) info.DeviceName = m.Groups[1].Value.Trim(); }

                if (info.IMEI == "N/A")
                { m = ReImei.Match(t); if (m.Success) info.IMEI = m.Groups[1].Value; }

                if (string.IsNullOrEmpty(info.IMEI2))
                { m = ReImei2.Match(t); if (m.Success) info.IMEI2 = m.Groups[1].Value; }

                if (string.IsNullOrEmpty(info.SerialNumber))
                { m = ReSerial.Match(t); if (m.Success) info.SerialNumber = m.Groups[1].Value; }

                if (info.BatteryHealth == "N/A")
                { m = ReBatLife.Match(t); if (m.Success) info.BatteryHealth = m.Groups[1].Value + "%"; }

                if (string.IsNullOrEmpty(info.ProductType))
                { m = ReProductType.Match(t); if (m.Success) info.ProductType = m.Groups[1].Value; }

                if (string.IsNullOrEmpty(info.iOSVersion))
                { m = ReBuildVer.Match(t); if (m.Success) info.iOSVersion = m.Groups[1].Value; }

                if (string.IsNullOrEmpty(info.ModelName))
                { m = ReModelRead.Match(t); if (m.Success) info.ModelName = m.Groups[1].Value; }
            }

            if (string.IsNullOrEmpty(info.ModelName) && info.DeviceName != "Unknown Device")
                info.ModelName = info.DeviceName;

            bool hasDevice = !string.IsNullOrEmpty(info.SerialNumber)
                          || (info.IMEI != "N/A" && !string.IsNullOrEmpty(info.IMEI));

            return hasDevice ? info : null;
        }

        // ── Debug dump ────────────────────────────────────────────────────

        /// <summary>Forwards the current AMD state into DumpToFile.</summary>
        public void DumpWithAmdState() => DumpToFile(_amdAvailable, _amdSerial);

        /// <summary>
        /// Writes a debug file to the Desktop showing LevelDB strings, blob selection,
        /// and the parsed result.  Pass amdSerial as reported by AMDevice for an
        /// accurate picture; leave defaults to simulate the pre-fix checkDate path.
        /// </summary>
        public static void DumpToFile(bool amdAvailable = false, string? amdSerial = null)
        {
            Process[] procs = Process.GetProcessesByName("3uTools");
            bool running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();

            var lines = new List<string>
            {
                $"iDeviceInfo Debug Dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"3uTools running: {running}",
                $"AMD available:   {amdAvailable}",
                $"AMD serial:      {amdSerial ?? "(none)"}",
                ""
            };

            var texts = CollectFromLocalStorage();
            lines.Add($"=== LevelDB strings ({texts.Count} unique) ===");
            lines.Add("");
            for (int i = 0; i < texts.Count; i++)
                lines.Add($"[{i,4}] {texts[i]}");

            lines.Add("");
            lines.Add($"=== Blob selection " +
                      $"(knownSerial={(!string.IsNullOrEmpty(amdSerial) ? amdSerial : "null → checkDate fallback")}) ===");

            if (!string.IsNullOrEmpty(amdSerial))
            {
                var matched = texts
                    .Where(t => t.Contains(amdSerial, StringComparison.Ordinal))
                    .Select(t =>
                    {
                        var m  = ReCheckDate.Match(t);
                        var dt = DateTime.MinValue;
                        if (m.Success)
                            DateTime.TryParseExact(m.Groups[1].Value, "MM/dd/yyyy HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out dt);
                        return (blob: t, date: dt);
                    })
                    .OrderByDescending(x => x.date)
                    .ToList();

                lines.Add($"Blobs matching serial '{amdSerial}': {matched.Count}");
                foreach (var (blob, date) in matched)
                    lines.Add($"  checkDate: {date:MM/dd/yyyy HH:mm:ss}  " +
                              $"(first 160 chars: {blob[..Math.Min(160, blob.Length)]})");
            }
            else
            {
                string?  latestBlob = null;
                DateTime latestDate = DateTime.MinValue;
                foreach (string t in texts)
                {
                    var m = ReCheckDate.Match(t);
                    if (m.Success && DateTime.TryParseExact(m.Groups[1].Value,
                        "MM/dd/yyyy HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime dt)
                        && dt > latestDate)
                    { latestDate = dt; latestBlob = t; }
                }
                lines.Add(latestBlob != null
                    ? $"Latest checkDate: {latestDate:MM/dd/yyyy HH:mm:ss}  " +
                      $"(first 200 chars: {latestBlob[..Math.Min(200, latestBlob.Length)]})"
                    : "(none found — will scan all strings)");
            }

            var parsed = ParseDeviceInfo(texts, amdSerial);
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
            if (_amdSubscription != IntPtr.Zero)
            {
                try { AMD.AMDeviceNotificationUnsubscribe(_amdSubscription); } catch { }
                _amdSubscription = IntPtr.Zero;
            }
        }
    }
}
