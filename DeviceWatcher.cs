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

            if (msg == AMD.MSG_CONNECTED)
            {
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

                    string? charging = ReadKey(device,
                        "com.apple.mobile.battery", "BatteryIsCharging");
                    info.IsCharging = charging == "true" || charging == "1";

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
                    lines.Add($"   ModelName:     {info.ModelName}");
                    lines.Add($"   ProductType:   {info.ProductType}");
                    lines.Add($"   iOSVersion:    {info.iOSVersion}");
                    lines.Add($"   SerialNumber:  {info.SerialNumber}");
                    lines.Add($"   IMEI:          {info.IMEI}");
                    lines.Add($"   IMEI2:         {info.IMEI2}");
                    lines.Add($"   BatteryLevel:  {info.BatteryLevel}");
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
