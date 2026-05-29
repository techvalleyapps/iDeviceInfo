using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using iDeviceInfo.Native;

namespace iDeviceInfo
{
    /// <summary>
    /// Detects and reads iOS device information directly via Apple's MobileDevice.dll.
    ///
    /// Apple Devices app (v1818+) delivers AMD notification callbacks via CF RunLoop,
    /// NOT via raw Win32 messages.  We therefore:
    ///   1. Call CFRunLoopGetCurrent() on the AMD thread BEFORE subscribing, which
    ///      creates and registers a CF RunLoop for that thread.
    ///   2. Call AMDeviceNotificationSubscribe — AMD now has a valid RunLoop to
    ///      schedule events on.
    ///   3. Call CFRunLoopRun() which blocks the thread and dispatches CF events
    ///      (including AMD callbacks) as they arrive.
    ///
    /// If CFRunLoopRun / CFRunLoopGetCurrent are not exported by the installed
    /// CoreFoundation.dll, we fall back to a Win32 GetMessage loop (works with the
    /// older iTunes-based DLL).
    ///
    /// Device info is read synchronously on the AMD thread while the device handle
    /// is guaranteed valid, then the plain C# result is marshalled to the UI thread.
    ///
    /// Events always fire on the WinForms UI thread.
    /// Does NOT depend on 3uTools or any LevelDB scraping.
    /// </summary>
    public sealed class DeviceWatcher : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────

        /// <summary>Fired when a device connects and all its fields have been read.</summary>
        public event EventHandler<DeviceInfo>? DeviceConnected;

        /// <summary>Fired when a device disconnects. Arg = SerialNumber.</summary>
        public event EventHandler<string>? DeviceDisconnected;

        // ── Win32 P/Invokes (AMD thread fallback) ─────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd, wParam, lParam;
            public uint   message, time;
            public int    ptX, ptY;
        }

        [DllImport("user32.dll")] private static extern int    GetMessage(out MSG m, IntPtr h, uint f, uint l);
        [DllImport("user32.dll")] private static extern bool   TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern bool   PostThreadMessage(uint id, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const uint WM_QUIT = 0x0012;

        // ── State ─────────────────────────────────────────────────────────

        private bool _disposed;

        // UI-thread sync context (captured in Start)
        private SynchronizationContext? _uiCtx;

        // Background AMD thread
        private Thread? _amdThread;
        private uint    _amdThreadId;
        private readonly ManualResetEventSlim _amdReady = new(false);

        // CF RunLoop handle saved from the AMD thread so Dispose can stop it
        private IntPtr _amdRunLoop = IntPtr.Zero;

        // AMD subscription
        private IntPtr                          _amdSubscription = IntPtr.Zero;
        private AMD.DeviceNotificationCallback? _amdCallback;     // keep-alive ref

        // Per-device state — only touched on the UI thread
        private readonly Dictionary<string, DeviceInfo> _connected      = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<IntPtr, string>     _handleToSerial = new();

        // ── Diagnostics ───────────────────────────────────────────────────

        private int     _callbackFireCount;
        private uint    _lastCallbackMsg;
        private string? _lastReadException;
        private bool    _usingCFRunLoop;

        // ── Lifecycle ─────────────────────────────────────────────────────

        public void Start()
        {
            _uiCtx = SynchronizationContext.Current ?? new SynchronizationContext();

            _amdThread = new Thread(AmdThreadEntry)
            {
                IsBackground = true,
                Name         = "iDeviceInfo-AMD"
                // MTA (default) is correct — STA is NOT needed for AMD APIs
            };
            _amdThread.Start();

            // Wait up to 5 s for subscribe + RunLoop start
            _amdReady.Wait(5000);
        }

        /// <summary>Re-fires DeviceConnected for every currently tracked device (Refresh menu).</summary>
        public void Restart()
        {
            foreach (var di in _connected.Values.ToList())
                DeviceConnected?.Invoke(this, di);
        }

        // ── AMD thread entry ──────────────────────────────────────────────

        private void AmdThreadEntry()
        {
            _amdThreadId = GetCurrentThreadId();
            _amdCallback = OnAmdNotification;

            // ── Step 1: initialise CF RunLoop for this thread ────────────────
            //
            // AMDeviceNotificationSubscribe in Apple Devices app v1818+ schedules
            // its callbacks on the calling thread's CF RunLoop.  If no RunLoop exists
            // when Subscribe is called, the native code calls abort() and the process
            // dies instantly — no managed exception, no MessageBox.
            //
            // CFRunLoopGetCurrent() creates the RunLoop lazily if needed.
            // The returned pointer is borrowed (do NOT CFRelease it).

            bool useCFRunLoop = false;
            try
            {
                _amdRunLoop  = CF.CFRunLoopGetCurrent();
                useCFRunLoop = _amdRunLoop != IntPtr.Zero;
            }
            catch
            {
                // EntryPointNotFoundException → CFRunLoopGetCurrent not exported
                // (older iTunes DLL).  Fall back to Win32 message loop below.
            }

            // ── Step 2: subscribe ────────────────────────────────────────────

            try
            {
                int r = AMD.AMDeviceNotificationSubscribe(
                    _amdCallback, 0, 0, IntPtr.Zero, out _amdSubscription);
                if (r != 0)
                    _amdSubscription = IntPtr.Zero;
            }
            catch
            {
                _amdSubscription = IntPtr.Zero;
            }

            _usingCFRunLoop = useCFRunLoop;
            _amdReady.Set(); // unblock Start()

            // ── Step 3: pump events ──────────────────────────────────────────

            if (useCFRunLoop)
            {
                try
                {
                    CF.CFRunLoopRun(); // blocks until CFRunLoopStop() is called
                    return;
                }
                catch
                {
                    // CFRunLoopRun not exported — fall through to Win32 loop
                    _usingCFRunLoop = false;
                }
            }

            // Win32 fallback (iTunes / legacy DLL)
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        // ── AMD notification callback (runs on AMD thread) ────────────────

        private void OnAmdNotification(ref AMD.DeviceCallbackInfo info, IntPtr cookie)
        {
            _callbackFireCount++;
            _lastCallbackMsg = info.Message;

            IntPtr device = info.Device;
            uint   msg    = info.Message;

            // Ignore WiFi-connected devices — USB only
            try
            {
                if (AMD.AMDeviceGetInterfaceType(device) != AMD.INTERFACE_USB) return;
            }
            catch { /* if the call fails, allow through */ }

            if (msg == AMD.MSG_CONNECTED || msg == AMD.MSG_PAIRED)
            {
                // MSG_CONNECTED  — device just plugged in (may be untrusted; session fields
                //                  will be empty if pairing is not yet granted).
                // MSG_PAIRED     — user just tapped "Trust" on the device; re-read everything
                //                  now that a full lockdown session is possible.
                //
                // *** Read device info HERE, on the AMD thread, while the
                // device handle is guaranteed valid.  Never post a raw IntPtr
                // to the UI thread — it may be invalid by the time it runs. ***
                DeviceInfo? di = ReadAllDeviceFields(device);
                if (di == null || string.IsNullOrEmpty(di.SerialNumber)) return;

                // Marshal the plain C# result to the UI thread
                _uiCtx!.Post(_ =>
                {
                    _handleToSerial[device]     = di.SerialNumber;
                    _connected[di.SerialNumber] = di;
                    DeviceConnected?.Invoke(this, di);
                }, null);
            }
            else if (msg == AMD.MSG_DISCONNECTED)
            {
                _uiCtx!.Post(_ => HandleDisconnected(device), null);
            }
        }

        // ── Disconnect handler (UI thread) ────────────────────────────────

        private void HandleDisconnected(IntPtr device)
        {
            if (!_handleToSerial.TryGetValue(device, out string? serial)) return;
            _handleToSerial.Remove(device);
            _connected.Remove(serial);
            DeviceDisconnected?.Invoke(this, serial);
        }

        // ── Device field reading (AMD thread) ─────────────────────────────

        private DeviceInfo? ReadAllDeviceFields(IntPtr device)
        {
            try
            {
                if (AMD.AMDeviceConnect(device) != 0) return null;

                var info = new DeviceInfo();

                // Basic fields — readable before a full lockdown session on most devices
                info.DeviceName   = ReadKey(device, null, "DeviceName")     ?? "Unknown Device";
                info.SerialNumber = ReadKey(device, null, "SerialNumber")   ?? "";
                info.UDID         = ReadKey(device, null, "UniqueDeviceID") ?? "";
                info.ProductType  = ReadKey(device, null, "ProductType")    ?? "";
                info.iOSVersion   = ReadKey(device, null, "ProductVersion") ?? "";
                info.ModelName    = LookupModelName(info.ProductType);

                // Privileged fields — need a paired, trusted lockdown session
                bool sessionOk = AMD.AMDeviceValidatePairing(device) == 0 &&
                                 AMD.AMDeviceStartSession(device)    == 0;
                if (sessionOk)
                {
                    // Retry basics — some fields only come back after a session opens
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

                    // Battery health varies by iOS version; try diagnostics first.
                    string? health = DiagnosticsRelayClient.ReadBatteryHealth(device);
                    if (string.IsNullOrEmpty(health))
                    {
                        string? lockdownHealth =
                            ReadKey(device, "com.apple.mobile.battery", "BatteryMaximumCapacity") ??
                            ReadKey(device, "com.apple.mobile.battery", "MaximumCapacityPercent")  ??
                            ReadKey(device, "com.apple.mobile.battery", "BatteryHealthPercent")    ??
                            ReadKey(device, null,                        "BatteryMaximumCapacity");

                        if (!string.IsNullOrEmpty(lockdownHealth))
                            health = lockdownHealth + "%";
                    }
                    if (!string.IsNullOrEmpty(health))
                        info.BatteryHealth = health;

                    string? charging = ReadKey(device,
                        "com.apple.mobile.battery", "BatteryIsCharging");
                    info.IsCharging = charging == "true" || charging == "1";

                    // Storage — read raw byte count and snap to nearest standard size
                    string? diskBytes = ReadKey(device, "com.apple.disk_usage", "TotalDiskCapacity")
                                     ?? ReadKey(device, null, "TotalDiskCapacity");
                    if (long.TryParse(diskBytes, out long rawBytes) && rawBytes > 0)
                        info.StorageGB = NormalizeStorageGB(rawBytes);

                    // Color — DeviceColor is a hex string like "#1b1b1b"
                    string? colorHex = ReadKey(device, null, "DeviceColor")
                                    ?? ReadKey(device, null, "DeviceEnclosureColor");
                    if (!string.IsNullOrEmpty(colorHex))
                        info.Color = HexToColorName(info.ProductType, colorHex);

                    AMD.AMDeviceStopSession(device);
                }

                AMD.AMDeviceDisconnect(device);

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

        // ── Key reading ───────────────────────────────────────────────────

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

        // ── Storage normalization ─────────────────────────────────────────

        private static int NormalizeStorageGB(long bytes)
        {
            // Apple uses marketing gigabytes (1 GB = 1,000,000,000 bytes)
            double gb = bytes / 1_000_000_000.0;
            int[] sizes = { 4, 8, 16, 32, 64, 128, 256, 512, 1024 };
            int best = sizes[0];
            double minDiff = double.MaxValue;
            foreach (int s in sizes)
            {
                double diff = Math.Abs(gb - s);
                if (diff < minDiff) { minDiff = diff; best = s; }
            }
            return best;
        }

        // ── Color name lookup ─────────────────────────────────────────────

        /// <summary>
        /// Maps a DeviceColor hex string (e.g. "#1b1b1b") to a human-readable
        /// color name.  Uses an exact lookup table first; falls back to an HSL
        /// heuristic for unknown codes.  Adjusts some names by device generation
        /// (e.g. Silver→Starlight on iPhone 13+, Space Gray→Midnight on iPhone 13+).
        /// </summary>
        private static string HexToColorName(string productType, string hex)
        {
            hex = hex.Trim().ToUpperInvariant();
            if (!hex.StartsWith("#")) hex = "#" + hex;

            // ── Exact lookup table ────────────────────────────────────────
            // Values sourced from Apple lockdown observations across device generations.
            if (_colorMap.TryGetValue(hex, out string? exact))
            {
                // iPhone 13 / 14 / 15 / 16 rename some classic colors
                bool is13Plus = IsIPhone13OrLater(productType);
                if (is13Plus)
                {
                    if (exact == "Silver")     exact = "Starlight";
                    if (exact == "Space Gray") exact = "Midnight";
                }
                return exact;
            }

            // ── Fallback: HSL heuristic ───────────────────────────────────
            if (!TryParseHex(hex, out int r, out int g, out int b)) return "";

            float rf = r / 255f, gf = g / 255f, bf2 = b / 255f;
            float max = Math.Max(rf, Math.Max(gf, bf2));
            float min = Math.Min(rf, Math.Min(gf, bf2));
            float l   = (max + min) / 2f;
            float s   = max == min ? 0 : (l < 0.5f
                ? (max - min) / (max + min)
                : (max - min) / (2f - max - min));

            // Near-achromatic
            if (s < 0.12f)
            {
                if (l > 0.75f) return IsIPhone13OrLater(productType) ? "Starlight" : "Silver";
                if (l < 0.25f) return IsIPhone13OrLater(productType) ? "Midnight"  : "Space Gray";
                return "Gray";
            }

            // Chromatic — compute hue
            float h;
            if (max == rf)      h = (gf - bf2) / (max - min);
            else if (max == gf) h = 2f + (bf2 - rf) / (max - min);
            else                h = 4f + (rf - gf) / (max - min);
            h *= 60f;
            if (h < 0) h += 360f;

            if (h < 20 || h >= 340)
                return s > 0.6f ? "(PRODUCT)RED" : "Pink";
            if (h < 45)  return l < 0.5f ? "Gold"   : "Yellow";
            if (h < 80)  return "Yellow";
            if (h < 160) return "Green";
            if (h < 200) return "Teal";
            if (h < 260) return "Blue";
            if (h < 300) return l < 0.4f ? "Deep Purple" : "Purple";
            return "Pink";
        }

        private static bool IsIPhone13OrLater(string productType)
        {
            // iPhone14,x = iPhone 13 series; iPhone15,x = 14 series; etc.
            if (!productType.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)) return false;
            string digits = productType.Substring(6).Split(',')[0];
            return int.TryParse(digits, out int n) && n >= 14;
        }

        private static bool TryParseHex(string hex, out int r, out int g, out int b)
        {
            r = g = b = 0;
            string h = hex.TrimStart('#');
            if (h.Length != 6) return false;
            try
            {
                r = Convert.ToInt32(h.Substring(0, 2), 16);
                g = Convert.ToInt32(h.Substring(2, 2), 16);
                b = Convert.ToInt32(h.Substring(4, 2), 16);
                return true;
            }
            catch { return false; }
        }

        // Known exact DeviceColor hex → base color name
        // (generation-specific renames applied in HexToColorName)
        private static readonly Dictionary<string, string> _colorMap =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Whites / Silvers / Starlights ─────────────────────────────
            ["#E4E4E4"] = "Silver",
            ["#F5F5F7"] = "Silver",
            ["#E1E4E3"] = "Silver",
            ["#F0EEEC"] = "Silver",
            ["#F2EFE6"] = "Silver",   // Starlight (raw)
            ["#FAF6F2"] = "Silver",
            ["#F5F0E8"] = "Silver",
            ["#F9F4EE"] = "Silver",
            // ── Space Grays / Midnights / Blacks ──────────────────────────
            ["#1B1B1B"] = "Space Gray",
            ["#2C2C2C"] = "Space Gray",
            ["#3C3C3C"] = "Space Gray",
            ["#1A1A2E"] = "Space Gray",   // Midnight (raw)
            ["#242526"] = "Space Gray",
            ["#1C1B21"] = "Space Gray",   // Deep Purple (very dark)
            ["#2D2640"] = "Deep Purple",
            // ── Golds ─────────────────────────────────────────────────────
            ["#F7E8D3"] = "Gold",
            ["#D4AF8E"] = "Gold",
            ["#F5E6D3"] = "Gold",
            ["#FAE7C9"] = "Gold",
            ["#F0DFC0"] = "Gold",
            // ── Rose Golds / Pinks ────────────────────────────────────────
            ["#F2C2B2"] = "Rose Gold",
            ["#E8C8BC"] = "Rose Gold",
            ["#FCE8E3"] = "Pink",
            ["#F9D2CA"] = "Pink",
            ["#FADADD"] = "Pink",
            ["#F4C8BE"] = "Pink",
            // ── (PRODUCT)RED ──────────────────────────────────────────────
            ["#D32A2F"] = "(PRODUCT)RED",
            ["#BF2026"] = "(PRODUCT)RED",
            ["#C00017"] = "(PRODUCT)RED",
            ["#CE0800"] = "(PRODUCT)RED",
            ["#C8001A"] = "(PRODUCT)RED",
            // ── Blues ─────────────────────────────────────────────────────
            ["#215CCA"] = "Blue",
            ["#225DC8"] = "Blue",
            ["#2A4D8E"] = "Blue",
            ["#4A89DC"] = "Blue",
            ["#5BA4DC"] = "Sierra Blue",
            ["#4680BF"] = "Blue",
            ["#226DC8"] = "Blue",
            // ── Greens ────────────────────────────────────────────────────
            ["#5B8A58"] = "Green",
            ["#4C9A6E"] = "Green",
            ["#A8E0A0"] = "Green",
            ["#4E5851"] = "Midnight Green",
            ["#394A42"] = "Alpine Green",
            ["#4A6741"] = "Green",
            ["#3D6B45"] = "Green",
            // ── Purples ───────────────────────────────────────────────────
            ["#8E7EB0"] = "Purple",
            ["#B4B0C8"] = "Purple",
            ["#8979B4"] = "Purple",
            ["#7B6FA0"] = "Purple",
            // ── Yellows ───────────────────────────────────────────────────
            ["#FDE68A"] = "Yellow",
            ["#F5D470"] = "Yellow",
            ["#FDD460"] = "Yellow",
            ["#F4D03F"] = "Yellow",
            // ── Orange ────────────────────────────────────────────────────
            ["#F8954F"] = "Orange",
            ["#E8732A"] = "Orange",
        };

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
            string lastMsgName = _lastCallbackMsg switch
            {
                AMD.MSG_CONNECTED    => "MSG_CONNECTED",
                AMD.MSG_DISCONNECTED => "MSG_DISCONNECTED",
                AMD.MSG_PAIRED       => "MSG_PAIRED",
                0                    => "(none yet)",
                _                    => $"unknown(0x{_lastCallbackMsg:X})"
            };

            var lines = new List<string>
            {
                $"iDeviceInfo Debug Dump — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"AMD subscription active:    {_amdSubscription != IntPtr.Zero}",
                $"AMD thread alive:           {_amdThread?.IsAlive}",
                $"AMD thread ID:              {_amdThreadId}",
                $"Using CF RunLoop:           {_usingCFRunLoop}",
                $"CF RunLoop ptr:             0x{_amdRunLoop:X}",
                $"Callback fired count:       {_callbackFireCount}",
                $"Last callback message:      {lastMsgName}",
                $"Last read exception:        {_lastReadException ?? "(none)"}",
                $"Tracked connected devices:  {_connected.Count}",
                ""
            };

            if (_connected.Count == 0)
            {
                lines.Add("No devices currently tracked.");
                lines.Add("(If a device is plugged in, disconnect and reconnect it.)");
            }
            else
            {
                int i = 1;
                foreach (var (_, info) in _connected)
                {
                    lines.Add($"── Device {i++} ───────────────────────────────────────────────");
                    lines.Add($"   DeviceName:    {info.DeviceName}");
                    lines.Add($"   Model:         {info.FullModelName}");
                    lines.Add($"   ProductType:   {info.ProductType}");
                    lines.Add($"   Color:         {(string.IsNullOrEmpty(info.Color) ? "N/A" : info.Color)}");
                    lines.Add($"   StorageGB:     {(info.StorageGB > 0 ? info.StorageGB + "GB" : "N/A")}");
                    lines.Add($"   iOSVersion:    {info.iOSVersion}");
                    lines.Add($"   SerialNumber:  {info.SerialNumber}");
                    lines.Add($"   IMEI:          {info.IMEI}");
                    lines.Add($"   IMEI2:         {info.IMEI2}");
                    lines.Add($"   BatteryLevel:  {info.BatteryLevel}");
                    lines.Add($"   BatteryHealth: {info.BatteryHealth}");
                    lines.Add($"   IsCharging:    {info.IsCharging}");
                    lines.Add($"   UDID:          {info.UDID}");
                    lines.Add("");
                }
            }

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

            // Unsubscribe AMD
            if (_amdSubscription != IntPtr.Zero)
            {
                try { AMD.AMDeviceNotificationUnsubscribe(_amdSubscription); } catch { }
                _amdSubscription = IntPtr.Zero;
            }

            // Stop the CF RunLoop (primary pump)
            if (_amdRunLoop != IntPtr.Zero)
            {
                try { CF.CFRunLoopStop(_amdRunLoop); } catch { }
                _amdRunLoop = IntPtr.Zero;
            }

            // Fallback: stop Win32 message loop on the AMD thread
            if (_amdThreadId != 0)
            {
                try { PostThreadMessage(_amdThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
        }
    }
}
