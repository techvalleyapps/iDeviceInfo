using System;
using System.Runtime.InteropServices;
using System.Threading;
using iDeviceInfo.Native;

namespace iDeviceInfo
{
    /// <summary>
    /// Monitors USB device connect / disconnect events via Apple's MobileDevice.dll.
    ///
    /// IMPORTANT: Start() must be called from the UI thread.
    /// AMDeviceNotificationSubscribe on Windows delivers callbacks via the Windows
    /// message queue of the subscribing thread. The WinForms message pump (already
    /// running on the UI thread via Application.Run) handles delivery automatically.
    /// </summary>
    public sealed class DeviceWatcher : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────

        public event EventHandler<DeviceInfo>? DeviceConnected;
        public event EventHandler?             DeviceDisconnected;

        // ── State ─────────────────────────────────────────────────────────

        private AMD.DeviceNotificationCallback? _callbackDelegate; // keep alive — GC must not collect
        private IntPtr                          _subscription;
        private bool                            _disposed;

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Subscribes to device notifications. MUST be called on the UI thread
        /// so that the WinForms message pump can deliver the callbacks.
        /// </summary>
        public void Start()
        {
            _callbackDelegate = OnDeviceNotification; // root the delegate — GC must not collect this

            int ret = AMD.AMDeviceNotificationSubscribe(
                _callbackDelegate, 0, 0, IntPtr.Zero, out _subscription);

            // ret != 0 → Apple Mobile Device Service not running (iTunes not installed)
        }

        /// <summary>
        /// Re-subscribes (unsubscribe + subscribe). Call this if the initial
        /// subscription missed an already-connected device.
        /// </summary>
        public void Restart()
        {
            if (_subscription != IntPtr.Zero)
            {
                SafeCall(() => AMD.AMDeviceNotificationUnsubscribe(_subscription));
                _subscription = IntPtr.Zero;
            }
            Start();
        }

        // ── Callback (delivered on the UI thread via WinForms message pump) ──

        private void OnDeviceNotification(
            ref AMD.DeviceCallbackInfo info, IntPtr cookie)
        {
            if (info.Message == AMD.MSG_CONNECTED)
            {
                try
                {
                    var deviceInfo = ReadDeviceInfo(info.Device);
                    DeviceConnected?.Invoke(this, deviceInfo);
                }
                catch
                {
                    // Don't crash — device may have been unplugged mid-read
                }
            }
            else if (info.Message == AMD.MSG_DISCONNECTED)
            {
                DeviceDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        // ── Device Info Reading ───────────────────────────────────────────

        private static DeviceInfo ReadDeviceInfo(IntPtr device)
        {
            var info = new DeviceInfo();

            try
            {
                AMD.AMDeviceConnect(device);
                AMD.AMDeviceValidatePairing(device);
                AMD.AMDeviceStartSession(device);

                // ── Identity ─────────────────────────────────────────────

                info.DeviceName   = ReadString(device, null, "DeviceName")     ?? "Unknown";
                info.ProductType  = ReadString(device, null, "ProductType")    ?? "";
                info.iOSVersion   = ReadString(device, null, "ProductVersion") ?? "";
                info.UDID         = ReadString(device, null, "UniqueDeviceID") ?? "";
                info.SerialNumber = ReadString(device, null, "SerialNumber")   ?? "";
                info.ModelName    = MapProductTypeToName(info.ProductType);

                // IMEI — absent on Wi-Fi-only iPads
                info.IMEI  = ReadString(device, null, "InternationalMobileEquipmentIdentity")  ?? "N/A";
                info.IMEI2 = ReadString(device, null, "InternationalMobileEquipmentIdentity2") ?? "";

                // ── Battery ──────────────────────────────────────────────

                const string battDomain = "com.apple.mobile.battery";
                string? level    = ReadString(device, battDomain, "BatteryCurrentCapacity");
                string? health   = ReadString(device, battDomain, "BatteryMaximumCapacity");
                string? charging = ReadString(device, battDomain, "BatteryIsCharging");

                info.BatteryLevel  = level  != null ? $"{level}%"  : "N/A";
                info.BatteryHealth = health != null ? $"{health}%" : "N/A";
                info.IsCharging    = charging is "true" or "1";

                // Fallback: try diagnostics relay for battery health
                if (info.BatteryHealth == "N/A")
                {
                    string? relayHealth = TryReadBatteryHealthViaRelay(device);
                    if (relayHealth != null)
                        info.BatteryHealth = relayHealth;
                }
            }
            catch
            {
                // Return whatever fields were populated before the error
            }
            finally
            {
                SafeCall(() => AMD.AMDeviceStopSession(device));
                SafeCall(() => AMD.AMDeviceDisconnect(device));
            }

            return info;
        }

        // ── Lockdown value helper ─────────────────────────────────────────

        private static string? ReadString(IntPtr device, string? domain, string key)
        {
            IntPtr domainCF = IntPtr.Zero;
            IntPtr keyCF    = IntPtr.Zero;
            IntPtr valueCF  = IntPtr.Zero;

            try
            {
                if (domain != null)
                    domainCF = CF.ToCFString(domain);
                keyCF   = CF.ToCFString(key);
                valueCF = AMD.AMDeviceCopyValue(device, domainCF, keyCF);
                return CF.CFValueToString(valueCF);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (domainCF != IntPtr.Zero) CF.CFRelease(domainCF);
                if (keyCF    != IntPtr.Zero) CF.CFRelease(keyCF);
                if (valueCF  != IntPtr.Zero) CF.CFRelease(valueCF);
            }
        }

        // ── Diagnostics Relay (battery health fallback) ───────────────────

        private static string? TryReadBatteryHealthViaRelay(IntPtr device)
        {
            IntPtr serviceNameCF = IntPtr.Zero;
            try
            {
                serviceNameCF = CF.ToCFString("com.apple.mobile.diagnostics_relay");
                int ret = AMD.AMDeviceStartService(device, serviceNameCF,
                                                   out IntPtr handle, IntPtr.Zero);
                if (ret != 0 || handle == IntPtr.Zero) return null;

                const string plist =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" " +
                    "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" +
                    "<plist version=\"1.0\"><dict>" +
                    "<key>Request</key><string>IORegistry</string>" +
                    "<key>CurrentPlane</key><string>IOService</string>" +
                    "<key>EntryName</key><string>AppleSmartBattery</string>" +
                    "</dict></plist>";

                byte[] plistBytes = System.Text.Encoding.UTF8.GetBytes(plist);
                int networkLen = System.Net.IPAddress.HostToNetworkOrder(plistBytes.Length);

                byte[] lenBuf = new byte[4];
                System.Buffer.BlockCopy(BitConverter.GetBytes(networkLen), 0, lenBuf, 0, 4);

                SendAll(handle, lenBuf, 4);
                SendAll(handle, plistBytes, plistBytes.Length);

                byte[] respLenBuf = new byte[4];
                RecvAll(handle, respLenBuf, 4);
                int respLen = System.Net.IPAddress.NetworkToHostOrder(
                    BitConverter.ToInt32(respLenBuf, 0));

                if (respLen <= 0 || respLen > 1_000_000) return null;

                byte[] respBuf = new byte[respLen];
                RecvAll(handle, respBuf, respLen);
                return ParseBatteryHealthFromPlist(
                    System.Text.Encoding.UTF8.GetString(respBuf));
            }
            catch
            {
                return null;
            }
            finally
            {
                if (serviceNameCF != IntPtr.Zero) CF.CFRelease(serviceNameCF);
            }
        }

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int send(IntPtr s, byte[] buf, int len, int flags);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int recv(IntPtr s, byte[] buf, int len, int flags);

        private static void SendAll(IntPtr sock, byte[] buf, int len)
        {
            int offset = 0;
            while (offset < len)
            {
                byte[] segment = new byte[len - offset];
                Array.Copy(buf, offset, segment, 0, segment.Length);
                int n = send(sock, segment, segment.Length, 0);
                if (n <= 0) throw new InvalidOperationException("Send failed");
                offset += n;
            }
        }

        private static void RecvAll(IntPtr sock, byte[] buf, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = recv(sock, buf, len - got, 0);
                if (n <= 0) throw new InvalidOperationException("Recv failed");
                got += n;
            }
        }

        private static string? ParseBatteryHealthFromPlist(string plist)
        {
            long max    = ExtractIntFromPlist(plist, "MaxCapacity");
            long design = ExtractIntFromPlist(plist, "DesignCapacity");
            if (max <= 0 || design <= 0) return null;
            return $"{Math.Round((double)max / design * 100.0, 1)}%";
        }

        private static long ExtractIntFromPlist(string plist, string key)
        {
            string marker = $"<key>{key}</key>";
            int idx = plist.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return -1;
            int start = plist.IndexOf("<integer>", idx + marker.Length, StringComparison.Ordinal);
            if (start < 0) return -1;
            start += "<integer>".Length;
            int end = plist.IndexOf("</integer>", start, StringComparison.Ordinal);
            if (end < 0) return -1;
            return long.TryParse(plist[start..end].Trim(), out long val) ? val : -1;
        }

        // ── ProductType → friendly name ───────────────────────────────────

        private static string MapProductTypeToName(string productType) =>
            productType switch
            {
                "iPhone17,1" => "iPhone 16 Pro Max",
                "iPhone17,2" => "iPhone 16 Pro",
                "iPhone17,3" => "iPhone 16 Plus",
                "iPhone17,4" => "iPhone 16",
                "iPhone16,1" => "iPhone 15",
                "iPhone16,2" => "iPhone 15 Plus",
                "iPhone16,3" => "iPhone 15 Pro",
                "iPhone16,4" => "iPhone 15 Pro Max",
                "iPhone15,2" => "iPhone 14 Pro",
                "iPhone15,3" => "iPhone 14 Pro Max",
                "iPhone14,7" => "iPhone 14",
                "iPhone14,8" => "iPhone 14 Plus",
                "iPhone14,4" => "iPhone 13 mini",
                "iPhone14,5" => "iPhone 13",
                "iPhone14,2" => "iPhone 13 Pro",
                "iPhone14,3" => "iPhone 13 Pro Max",
                "iPhone13,1" => "iPhone 12 mini",
                "iPhone13,2" => "iPhone 12",
                "iPhone13,3" => "iPhone 12 Pro",
                "iPhone13,4" => "iPhone 12 Pro Max",
                "iPhone12,1" => "iPhone 11",
                "iPhone12,3" => "iPhone 11 Pro",
                "iPhone12,5" => "iPhone 11 Pro Max",
                "iPhone14,6" => "iPhone SE (3rd gen)",
                "iPhone12,8" => "iPhone SE (2nd gen)",
                "iPhone8,4"  => "iPhone SE (1st gen)",
                "iPad13,18"  => "iPad (10th gen)",
                "iPad13,19"  => "iPad (10th gen)",
                "iPad14,1"   => "iPad mini (6th gen)",
                "iPad14,2"   => "iPad mini (6th gen)",
                "iPad14,3"   => "iPad Pro 11\" (4th gen)",
                "iPad14,4"   => "iPad Pro 11\" (4th gen)",
                "iPad14,5"   => "iPad Pro 12.9\" (6th gen)",
                "iPad14,6"   => "iPad Pro 12.9\" (6th gen)",
                _            => productType
            };

        private static void SafeCall(Action action)
        {
            try { action(); } catch { }
        }

        // ── IDisposable ───────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_subscription != IntPtr.Zero)
                SafeCall(() => AMD.AMDeviceNotificationUnsubscribe(_subscription));
        }
    }
}
