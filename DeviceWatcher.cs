using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using iDeviceInfo.Native;

namespace iDeviceInfo
{
    /// <summary>
    /// Detects and reads iOS device information directly via Apple's MobileDevice.dll.
    ///
    /// Detection strategy (Apple Devices app v1818+ compatible):
    ///   Primary  — AMDCreateDeviceList() polled every 2 s from the WinForms timer.
    ///              Reliable on all DLL versions; connects/disconnects detected within ~2 s.
    ///   Bonus    — AMDeviceNotificationSubscribe() for instant response on older iTunes DLL.
    ///              Silently ignored if the DLL doesn't deliver callbacks (v1818 behaviour).
    ///
    /// Events always fire on the WinForms UI thread (safe to touch controls directly).
    /// Does NOT depend on 3uTools or any LevelDB scraping.
    /// </summary>
    public sealed class DeviceWatcher : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────

        /// <summary>Fired when a device connects and all its fields have been read.</summary>
        public event EventHandler<DeviceInfo>? DeviceConnected;

        /// <summary>Fired when a device disconnects. Arg = SerialNumber.</summary>
        public event EventHandler<string>? DeviceDisconnected;

        // ── State ─────────────────────────────────────────────────────────

        private bool _disposed;

        // Polling timer — primary detection path
        private System.Windows.Forms.Timer? _pollTimer;

        // serial → cached DeviceInfo for connected devices
        private readonly Dictionary<string, DeviceInfo> _connected =
            new(StringComparer.OrdinalIgnoreCase);

        // AMD notification subscription — bonus path (may never fire on v1818)
        private IntPtr                          _amdSubscription = IntPtr.Zero;
        private AMD.DeviceNotificationCallback? _amdCallback;

        // ── Diagnostics ───────────────────────────────────────────────────
        private int     _pollCount;
        private int     _callbackFireCount;
        private string? _lastPollException;
        private string? _lastReadException;

        // ── Lifecycle ─────────────────────────────────────────────────────

        /// <summary>
        /// Start polling and (optionally) subscribe to AMD notifications.
        /// Must be called after Application.Run() has started the message pump.
        /// </summary>
        public void Start()
        {
            // ── Bonus: AMD notification callback (instant on old iTunes DLL) ──
            _amdCallback = OnAmdNotification;
            try
            {
                int r = AMD.AMDeviceNotificationSubscribe(
                    _amdCallback, 0, 0, IntPtr.Zero, out _amdSubscription);
                if (r != 0) _amdSubscription = IntPtr.Zero;
            }
            catch { _amdSubscription = IntPtr.Zero; }

            // ── Primary: polling via AMDCreateDeviceList ───────────────────
            _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _pollTimer.Tick += (_, _) => Poll();
            _pollTimer.Start();
            Poll(); // immediate first check
        }

        /// <summary>Force an immediate re-poll (tray menu Refresh).</summary>
        public void Restart() => Poll();

        // ── Polling ───────────────────────────────────────────────────────

        private void Poll()
        {
            _pollCount++;
            try
            {
                // Get UDIDs of all currently connected devices
                var nowUdids = GetConnectedUdids(); // udid → device handle

                // Detect newly connected devices
                foreach (var (udid, handle) in nowUdids)
                {
                    // udid is our cheap identity key; serial is what we expose
                    // Check by udid first — if we already know this device by any serial, skip
                    bool alreadyTracked = _connected.Values.Any(
                        d => string.Equals(d.UDID, udid, StringComparison.OrdinalIgnoreCase));

                    if (!alreadyTracked)
                    {
                        DeviceInfo? di = ReadAllDeviceFields(handle);
                        if (di != null && !string.IsNullOrEmpty(di.SerialNumber))
                        {
                            _connected[di.SerialNumber] = di;
                            DeviceConnected?.Invoke(this, di);
                        }
                    }
                }

                // Detect disconnected devices
                var nowUdidSet = new HashSet<string>(
                    nowUdids.Keys, StringComparer.OrdinalIgnoreCase);

                foreach (var serial in _connected.Keys.ToList())
                {
                    DeviceInfo info = _connected[serial];
                    bool stillConnected = !string.IsNullOrEmpty(info.UDID)
                        ? nowUdidSet.Contains(info.UDID)
                        : nowUdids.Count > 0; // fallback if UDID wasn't read

                    if (!stillConnected)
                    {
                        _connected.Remove(serial);
                        DeviceDisconnected?.Invoke(this, serial);
                    }
                }
            }
            catch (Exception ex)
            {
                _lastPollException = ex.Message;
            }
        }

        /// <summary>
        /// Calls AMDCreateDeviceList() and returns UDID → device handle for every
        /// device the Apple Mobile Device Service currently knows about.
        /// </summary>
        private static Dictionary<string, IntPtr> GetConnectedUdids()
        {
            var result = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            IntPtr list = IntPtr.Zero;
            try
            {
                list = AMD.AMDCreateDeviceList();
                if (list == IntPtr.Zero) return result;

                long count = CF.CFArrayGetCount(list);
                for (long i = 0; i < count; i++)
                {
                    IntPtr device = CF.CFArrayGetValueAtIndex(list, i);
                    if (device == IntPtr.Zero) continue;

                    // AMDeviceCopyDeviceIdentifier is cheap — no connect/session needed
                    IntPtr udidRef = AMD.AMDeviceCopyDeviceIdentifier(device);
                    if (udidRef == IntPtr.Zero) continue;
                    string? udid = CF.CFValueToString(udidRef);
                    CF.CFRelease(udidRef);

                    if (!string.IsNullOrEmpty(udid))
                        result[udid] = device;
                }
            }
            finally
            {
                if (list != IntPtr.Zero) CF.CFRelease(list);
            }
            return result;
        }

        // ── AMD notification callback (bonus path — may never fire on v1818) ──

        private void OnAmdNotification(ref AMD.DeviceCallbackInfo info, IntPtr cookie)
        {
            _callbackFireCount++;
            // If the callback fires (iTunes-era DLL), trigger an immediate poll
            // so the UI updates instantly rather than waiting up to 2 s.
            Poll();
        }

        // ── Device field reading ──────────────────────────────────────────

        private DeviceInfo? ReadAllDeviceFields(IntPtr device)
        {
            try
            {
                if (AMD.AMDeviceConnect(device) != 0) return null;

                var info = new DeviceInfo();

                // Basic fields — often available without a full lockdown session
                info.DeviceName   = ReadKey(device, null, "DeviceName")     ?? "Unknown Device";
                info.SerialNumber = ReadKey(device, null, "SerialNumber")   ?? "";
                info.UDID         = ReadKey(device, null, "UniqueDeviceID") ?? "";
                info.ProductType  = ReadKey(device, null, "ProductType")    ?? "";
                info.iOSVersion   = ReadKey(device, null, "ProductVersion") ?? "";
                info.ModelName    = LookupModelName(info.ProductType);

                // Privileged fields — require a paired lockdown session
                bool sessionOk = AMD.AMDeviceValidatePairing(device) == 0 &&
                                 AMD.AMDeviceStartSession(device)    == 0;
                if (sessionOk)
                {
                    // Retry basic fields that need a session on some devices/iOS versions
                    if (string.IsNullOrEmpty(info.SerialNumber))
                        info.SerialNumber = ReadKey(device, null, "SerialNumber") ?? "";
                    if (string.IsNullOrEmpty(info.UDID))
                        info.UDID = ReadKey(device, null, "UniqueDeviceID") ?? "";
                    if (string.IsNullOrEmpty(info.ProductType))
                        info.ProductType = ReadKey(device, null, "ProductType") ?? "";
                    if (string.IsNullOrEmpty(info.iOSVersion))
                        info.iOSVersion = ReadKey(device, null, "ProductVersion") ?? "";
                    if (info.DeviceName == "Unknown Device")
                        info.DeviceName = ReadKey(device, null, "DeviceName") ?? "Unknown Device";
                    if (string.IsNullOrEmpty(info.ModelName))
                        info.ModelName = LookupModelName(info.ProductType);

                    string? imei = ReadKey(device, null,
                        "InternationalMobileEquipmentIdentity");
                    if (!string.IsNullOrEmpty(imei)) info.IMEI = imei;

                    string? imei2 = ReadKey(device, null,
                        "InternationalMobileEquipmentIdentity2");
                    if (!string.IsNullOrEmpty(imei2)) info.IMEI2 = imei2;

                    string? batt = ReadKey(device,
                        "com.apple.mobile.battery", "BatteryCurrentCapacity");
                    if (!string.IsNullOrEmpty(batt))
                        info.BatteryLevel = batt + "%";

                    string? charging = ReadKey(device,
                        "com.apple.mobile.battery", "BatteryIsCharging");
                    info.IsCharging = charging == "true" || charging == "1";

                    AMD.AMDeviceStopSession(device);
                }

                AMD.AMDeviceDisconnect(device);

                // Need at least a serial or UDID to be a useful result
                return (!string.IsNullOrEmpty(info.SerialNumber) ||
                        !string.IsNullOrEmpty(info.UDID)) ? info : null;
            }
            catch (Exception ex)
            {
                _lastReadException = ex.Message;
                try { AMD.AMDeviceDisconnect(device); } catch { /* ignore */ }
                return null;
            }
        }

        private static string? ReadKey(IntPtr device, string? domain, string key)
        {
            IntPtr domainRef = domain != null ? CF.ToCFString(domain) : IntPtr.Zero;
            IntPtr keyRef    = CF.ToCFString(key);
            IntPtr valRef    = AMD.AMDeviceCopyValue(device, domainRef, keyRef);
            CF.CFRelease(keyRef);
            if (domainRef != IntPtr.Zero) CF.CFRelease(domainRef);
            if (valRef == IntPtr.Zero) return null;
            string? result = CF.CFValueToString(valRef);
            CF.CFRelease(valRef);
            return result;
        }

        // ── Model name lookup ─────────────────────────────────────────────

        private static string LookupModelName(string productType)
            => ModelNames.TryGetValue(productType, out string? name) ? name : productType;

        private static readonly Dictionary<string, string> ModelNames =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // ── iPhone 16 (2024) ──────────────────────────────────────────
            ["iPhone17,1"] = "iPhone 16 Pro",
            ["iPhone17,2"] = "iPhone 16 Pro Max",
            ["iPhone17,3"] = "iPhone 16",
            ["iPhone17,4"] = "iPhone 16 Plus",
            // ── iPhone 15 (2023) ──────────────────────────────────────────
            ["iPhone16,1"] = "iPhone 15 Pro",
            ["iPhone16,2"] = "iPhone 15 Pro Max",
            ["iPhone15,4"] = "iPhone 15",
            ["iPhone15,5"] = "iPhone 15 Plus",
            // ── iPhone 14 (2022) ──────────────────────────────────────────
            ["iPhone15,2"] = "iPhone 14 Pro",
            ["iPhone15,3"] = "iPhone 14 Pro Max",
            ["iPhone14,7"] = "iPhone 14",
            ["iPhone14,8"] = "iPhone 14 Plus",
            // ── iPhone SE 3rd gen (2022) ──────────────────────────────────
            ["iPhone14,6"] = "iPhone SE (3rd generation)",
            // ── iPhone 13 (2021) ──────────────────────────────────────────
            ["iPhone14,2"] = "iPhone 13 Pro",
            ["iPhone14,3"] = "iPhone 13 Pro Max",
            ["iPhone14,4"] = "iPhone 13 mini",
            ["iPhone14,5"] = "iPhone 13",
            // ── iPhone 12 (2020) ──────────────────────────────────────────
            ["iPhone13,1"] = "iPhone 12 mini",
            ["iPhone13,2"] = "iPhone 12",
            ["iPhone13,3"] = "iPhone 12 Pro",
            ["iPhone13,4"] = "iPhone 12 Pro Max",
            // ── iPhone SE 2nd gen (2020) ──────────────────────────────────
            ["iPhone12,8"] = "iPhone SE (2nd generation)",
            // ── iPhone 11 (2019) ──────────────────────────────────────────
            ["iPhone12,1"] = "iPhone 11",
            ["iPhone12,3"] = "iPhone 11 Pro",
            ["iPhone12,5"] = "iPhone 11 Pro Max",
            // ── iPhone XS / XR (2018) ─────────────────────────────────────
            ["iPhone11,2"] = "iPhone XS",
            ["iPhone11,4"] = "iPhone XS Max",
            ["iPhone11,6"] = "iPhone XS Max",
            ["iPhone11,8"] = "iPhone XR",
            // ── iPhone X (2017) ───────────────────────────────────────────
            ["iPhone10,3"] = "iPhone X",
            ["iPhone10,6"] = "iPhone X",
            // ── iPhone 8 (2017) ───────────────────────────────────────────
            ["iPhone10,1"] = "iPhone 8",
            ["iPhone10,4"] = "iPhone 8",
            ["iPhone10,2"] = "iPhone 8 Plus",
            ["iPhone10,5"] = "iPhone 8 Plus",
            // ── iPhone 7 (2016) ───────────────────────────────────────────
            ["iPhone9,1"]  = "iPhone 7",
            ["iPhone9,3"]  = "iPhone 7",
            ["iPhone9,2"]  = "iPhone 7 Plus",
            ["iPhone9,4"]  = "iPhone 7 Plus",
            // ── iPhone SE 1st gen (2016) ──────────────────────────────────
            ["iPhone8,4"]  = "iPhone SE (1st generation)",
            // ── iPhone 6s (2015) ──────────────────────────────────────────
            ["iPhone8,1"]  = "iPhone 6s",
            ["iPhone8,2"]  = "iPhone 6s Plus",
            // ── iPhone 6 (2014) ───────────────────────────────────────────
            ["iPhone7,2"]  = "iPhone 6",
            ["iPhone7,1"]  = "iPhone 6 Plus",
            // ── iPhone 5s (2013) ──────────────────────────────────────────
            ["iPhone6,1"]  = "iPhone 5s",
            ["iPhone6,2"]  = "iPhone 5s",
            // ── iPhone 5c (2013) ──────────────────────────────────────────
            ["iPhone5,3"]  = "iPhone 5c",
            ["iPhone5,4"]  = "iPhone 5c",
            // ── iPhone 5 (2012) ───────────────────────────────────────────
            ["iPhone5,1"]  = "iPhone 5",
            ["iPhone5,2"]  = "iPhone 5",

            // ── iPad Pro M4 (2024) ────────────────────────────────────────
            ["iPad16,3"]   = "iPad Pro 11-inch (M4)",
            ["iPad16,4"]   = "iPad Pro 11-inch (M4)",
            ["iPad16,5"]   = "iPad Pro 13-inch (M4)",
            ["iPad16,6"]   = "iPad Pro 13-inch (M4)",
            // ── iPad Air M2 (2024) ────────────────────────────────────────
            ["iPad14,8"]   = "iPad Air 13-inch (M2)",
            ["iPad14,9"]   = "iPad Air 13-inch (M2)",
            ["iPad14,10"]  = "iPad Air 11-inch (M2)",
            ["iPad14,11"]  = "iPad Air 11-inch (M2)",
            // ── iPad mini 7 (2024) ────────────────────────────────────────
            ["iPad16,1"]   = "iPad mini (7th generation)",
            ["iPad16,2"]   = "iPad mini (7th generation)",
            // ── iPad Pro M2 (2022) ────────────────────────────────────────
            ["iPad14,3"]   = "iPad Pro 11-inch (4th generation)",
            ["iPad14,4"]   = "iPad Pro 11-inch (4th generation)",
            ["iPad14,5"]   = "iPad Pro 12.9-inch (6th generation)",
            ["iPad14,6"]   = "iPad Pro 12.9-inch (6th generation)",
            // ── iPad 10th gen (2022) ──────────────────────────────────────
            ["iPad13,18"]  = "iPad (10th generation)",
            ["iPad13,19"]  = "iPad (10th generation)",
            // ── iPad Air M1 (2022) ────────────────────────────────────────
            ["iPad13,16"]  = "iPad Air (5th generation)",
            ["iPad13,17"]  = "iPad Air (5th generation)",
            // ── iPad mini 6 (2021) ────────────────────────────────────────
            ["iPad14,1"]   = "iPad mini (6th generation)",
            ["iPad14,2"]   = "iPad mini (6th generation)",
            // ── iPad Pro M1 (2021) ────────────────────────────────────────
            ["iPad13,4"]   = "iPad Pro 11-inch (3rd generation)",
            ["iPad13,5"]   = "iPad Pro 11-inch (3rd generation)",
            ["iPad13,6"]   = "iPad Pro 11-inch (3rd generation)",
            ["iPad13,7"]   = "iPad Pro 11-inch (3rd generation)",
            ["iPad13,8"]   = "iPad Pro 12.9-inch (5th generation)",
            ["iPad13,9"]   = "iPad Pro 12.9-inch (5th generation)",
            ["iPad13,10"]  = "iPad Pro 12.9-inch (5th generation)",
            ["iPad13,11"]  = "iPad Pro 12.9-inch (5th generation)",
            // ── iPad 9th gen (2021) ───────────────────────────────────────
            ["iPad12,1"]   = "iPad (9th generation)",
            ["iPad12,2"]   = "iPad (9th generation)",
            // ── iPad Air 4th gen (2020) ───────────────────────────────────
            ["iPad13,1"]   = "iPad Air (4th generation)",
            ["iPad13,2"]   = "iPad Air (4th generation)",
            // ── iPad Pro 2020 ─────────────────────────────────────────────
            ["iPad8,9"]    = "iPad Pro 11-inch (2nd generation)",
            ["iPad8,10"]   = "iPad Pro 11-inch (2nd generation)",
            ["iPad8,11"]   = "iPad Pro 12.9-inch (4th generation)",
            ["iPad8,12"]   = "iPad Pro 12.9-inch (4th generation)",
            // ── iPad 8th gen (2020) ───────────────────────────────────────
            ["iPad11,6"]   = "iPad (8th generation)",
            ["iPad11,7"]   = "iPad (8th generation)",
            // ── iPad mini 5 / iPad Air 3 (2019) ──────────────────────────
            ["iPad11,1"]   = "iPad mini (5th generation)",
            ["iPad11,2"]   = "iPad mini (5th generation)",
            ["iPad11,3"]   = "iPad Air (3rd generation)",
            ["iPad11,4"]   = "iPad Air (3rd generation)",
            // ── iPad Pro 2018 ─────────────────────────────────────────────
            ["iPad8,1"]    = "iPad Pro 11-inch (1st generation)",
            ["iPad8,2"]    = "iPad Pro 11-inch (1st generation)",
            ["iPad8,3"]    = "iPad Pro 11-inch (1st generation)",
            ["iPad8,4"]    = "iPad Pro 11-inch (1st generation)",
            ["iPad8,5"]    = "iPad Pro 12.9-inch (3rd generation)",
            ["iPad8,6"]    = "iPad Pro 12.9-inch (3rd generation)",
            ["iPad8,7"]    = "iPad Pro 12.9-inch (3rd generation)",
            ["iPad8,8"]    = "iPad Pro 12.9-inch (3rd generation)",
            // ── iPad 7th gen (2019) ───────────────────────────────────────
            ["iPad7,11"]   = "iPad (7th generation)",
            ["iPad7,12"]   = "iPad (7th generation)",
            // ── iPad 6th gen (2018) ───────────────────────────────────────
            ["iPad7,5"]    = "iPad (6th generation)",
            ["iPad7,6"]    = "iPad (6th generation)",
            // ── iPod touch 7th gen (2019) ─────────────────────────────────
            ["iPod9,1"]    = "iPod touch (7th generation)",
        };

        // ── Debug dump ────────────────────────────────────────────────────

        public void DumpWithAmdState()
        {
            var nowUdids = new Dictionary<string, IntPtr>();
            string listError = "(none)";
            try { nowUdids = GetConnectedUdids(); }
            catch (Exception ex) { listError = ex.Message; }

            var lines = new List<string>
            {
                $"iDeviceInfo Debug Dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"AMD subscription active:    {_amdSubscription != IntPtr.Zero}",
                $"Notification callback fires: {_callbackFireCount} times",
                $"Poll count:                 {_pollCount}",
                $"Tracked connected devices:  {_connected.Count}",
                $"AMDCreateDeviceList count:  {nowUdids.Count}",
                $"Last poll exception:        {_lastPollException ?? "(none)"}",
                $"Last read exception:        {_lastReadException ?? "(none)"}",
                $"AMDCreateDeviceList error:  {listError}",
                ""
            };

            // Show what AMDCreateDeviceList currently returns
            if (nowUdids.Count == 0)
            {
                lines.Add("AMDCreateDeviceList returned 0 devices.");
                lines.Add("(Is a device plugged in and trusted on this PC?)");
            }
            else
            {
                lines.Add($"=== Live devices from AMDCreateDeviceList ({nowUdids.Count}) ===");
                lines.Add("");
                int i = 1;
                foreach (var (udid, handle) in nowUdids)
                {
                    lines.Add($"── Device {i++} ───────────────────────────────────────────────");
                    lines.Add($"   UDID (fast):   {udid}");
                    DeviceInfo? di = ReadAllDeviceFields(handle);
                    if (di != null)
                    {
                        lines.Add($"   DeviceName:    {di.DeviceName}");
                        lines.Add($"   ModelName:     {di.ModelName}");
                        lines.Add($"   ProductType:   {di.ProductType}");
                        lines.Add($"   iOSVersion:    {di.iOSVersion}");
                        lines.Add($"   SerialNumber:  {di.SerialNumber}");
                        lines.Add($"   IMEI:          {di.IMEI}");
                        lines.Add($"   BatteryLevel:  {di.BatteryLevel}");
                        lines.Add($"   IsCharging:    {di.IsCharging}");
                        lines.Add($"   UDID:          {di.UDID}");
                    }
                    else
                    {
                        lines.Add($"   ReadAllDeviceFields returned null");
                        lines.Add($"   Last read exception: {_lastReadException ?? "(none)"}");
                    }
                    lines.Add("");
                }
            }

            // Also show what the poll loop has currently tracked
            lines.Add($"=== Tracked by poll loop ({_connected.Count}) ===");
            lines.Add("");
            foreach (var (serial, info) in _connected)
            {
                lines.Add($"   Serial: {serial}  Name: {info.DeviceName}  UDID: {info.UDID}");
            }
            if (_connected.Count == 0) lines.Add("   (none)");

            try
            {
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "iDeviceInfo_debug.txt");
                System.IO.File.WriteAllLines(path, lines, Encoding.UTF8);
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"")
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
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            if (_amdSubscription != IntPtr.Zero)
            {
                try { AMD.AMDeviceNotificationUnsubscribe(_amdSubscription); } catch { /* ignore */ }
                _amdSubscription = IntPtr.Zero;
            }
        }
    }
}
