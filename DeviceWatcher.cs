using System;
using System.Runtime.InteropServices;
using System.Threading;
using iDeviceInfo.Native;

namespace iDeviceInfo
{
    /// <summary>
    /// Monitors USB device connect / disconnect events via Apple's MobileDevice.dll
    /// and raises typed .NET events with full device info when a device connects.
    /// </summary>
    public sealed class DeviceWatcher : IDisposable
    {
        // ── Events ────────────────────────────────────────────────────────

        public event EventHandler<DeviceInfo>? DeviceConnected;
        public event EventHandler?             DeviceDisconnected;

        // ── State ─────────────────────────────────────────────────────────

        private AMD.DeviceNotificationCallback? _callbackDelegate; // keep alive!
        private IntPtr                          _subscription;
        private Thread?                         _runLoopThread;
        private bool                            _disposed;

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Starts listening for device connections.
        /// Spins up a background thread that pumps CoreFoundation run-loop events.
        /// </summary>
        public void Start()
        {
            if (_runLoopThread != null) return;

            _callbackDelegate = OnDeviceNotification; // must stay rooted

            _runLoopThread = new Thread(RunLoop)
            {
                Name         = "iDeviceInfo-RunLoop",
                IsBackground = true
            };
            _runLoopThread.Start();
        }

        // ── Private ───────────────────────────────────────────────────────

        /// <summary>
        /// Background thread: registers the notification callback and pumps the
        /// CoreFoundation run loop so notifications are delivered.
        /// </summary>
        private void RunLoop()
        {
            try
            {
                int ret = AMD.AMDeviceNotificationSubscribe(
                    _callbackDelegate!, 0, 0, IntPtr.Zero, out _subscription);

                if (ret != 0)
                    return; // service not running or iTunes not installed

                // Pump the CF run loop — this blocks until the thread is aborted
                // or the subscription is cancelled. We use a simple sleep loop here
                // because the Windows port of MobileDevice.dll delivers callbacks
                // on the subscribing thread via internal GetMessage / WaitForSingleObject.
                while (!_disposed)
                    Thread.Sleep(250);
            }
            catch (ThreadAbortException)
            {
                // Normal shutdown
            }
            catch
            {
                // Apple Mobile Device Service not running, iTunes not installed, etc.
            }
        }

        /// <summary>Called by MobileDevice.dll on the run-loop thread.</summary>
        private void OnDeviceNotification(
            ref AMD.DeviceCallbackInfo info, IntPtr cookie)
        {
            if (info.Message == AMD.MSG_CONNECTED)
            {
                try
                {
                    var deviceInfo = ReadDeviceInfo(info.Device);
                    // Marshal to UI thread
                    DeviceConnected?.Invoke(this, deviceInfo);
                }
                catch
                {
                    // Swallow — don't crash the run-loop thread
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

                info.DeviceName  = ReadString(device, null, "DeviceName")    ?? "Unknown";
                info.ProductType = ReadString(device, null, "ProductType")   ?? "";
                info.iOSVersion  = ReadString(device, null, "ProductVersion")  ?? "";
                info.UDID        = ReadString(device, null, "UniqueDeviceID")  ?? "";
                info.SerialNumber= ReadString(device, null, "SerialNumber")   ?? "";
                info.ModelName   = MapProductTypeToName(info.ProductType);

                // IMEI — not present on Wi-Fi-only iPads
                info.IMEI  = ReadString(device, null, "InternationalMobileEquipmentIdentity")  ?? "N/A";
                info.IMEI2 = ReadString(device, null, "InternationalMobileEquipmentIdentity2") ?? "";

                // ── Battery ──────────────────────────────────────────────

                const string battDomain = "com.apple.mobile.battery";
                string? level    = ReadString(device, battDomain, "BatteryCurrentCapacity");
                string? health   = ReadString(device, battDomain, "BatteryMaximumCapacity");
                string? charging = ReadString(device, battDomain, "BatteryIsCharging");

                info.BatteryLevel  = level    != null ? $"{level}%"  : "N/A";
                info.BatteryHealth = health   != null ? $"{health}%" : "N/A";
                info.IsCharging    = charging is "true" or "1";

                // If standard lockdown doesn't expose health, try diagnostics relay
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

        /// <summary>
        /// Attempts to read BatteryMaximumCapacity / DesignCapacity via the
        /// com.apple.mobile.diagnostics_relay service. Returns e.g. "87%" or null.
        /// </summary>
        private static string? TryReadBatteryHealthViaRelay(IntPtr device)
        {
            IntPtr serviceNameCF = IntPtr.Zero;
            try
            {
                serviceNameCF = CF.ToCFString("com.apple.mobile.diagnostics_relay");
                int ret = AMD.AMDeviceStartService(device, serviceNameCF,
                                                   out IntPtr handle, IntPtr.Zero);
                if (ret != 0 || handle == IntPtr.Zero) return null;

                // Build a plist XML request for the IORegistry entry
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

                // The handle is a raw Winsock SOCKET (IntPtr = SOCKET type)
                // Send: big-endian uint32 length + plist bytes
                byte[] lenBuf = new byte[4];
                int networkLen = System.Net.IPAddress.HostToNetworkOrder(plistBytes.Length);
                System.Buffer.BlockCopy(BitConverter.GetBytes(networkLen), 0, lenBuf, 0, 4);

                SendAll(handle, lenBuf,    4);
                SendAll(handle, plistBytes, plistBytes.Length);

                // Receive response length
                byte[] respLenBuf = new byte[4];
                RecvAll(handle, respLenBuf, 4);
                int respLen = System.Net.IPAddress.NetworkToHostOrder(
                    BitConverter.ToInt32(respLenBuf, 0));

                if (respLen <= 0 || respLen > 1_000_000) return null;

                byte[] respBuf = new byte[respLen];
                RecvAll(handle, respBuf, respLen);
                string response = System.Text.Encoding.UTF8.GetString(respBuf);

                return ParseBatteryHealthFromPlist(response);
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

        // Winsock send/recv helpers
        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int send(IntPtr s, byte[] buf, int len, int flags);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int recv(IntPtr s, byte[] buf, int len, int flags);

        private static void SendAll(IntPtr sock, byte[] buf, int len)
        {
            int sent = 0;
            while (sent < len)
            {
                int n = send(sock, buf, len - sent, 0);
                if (n <= 0) throw new InvalidOperationException("Send failed");
                // Shift buffer manually for partial sends
                if (n < len - sent)
                {
                    byte[] tmp = new byte[len - sent - n];
                    Array.Copy(buf, sent + n, tmp, 0, tmp.Length);
                    buf = tmp;
                    len = tmp.Length;
                    sent = 0;
                }
                else sent += n;
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

        // ── Plist parser (minimal — just extracts two integers) ───────────

        private static string? ParseBatteryHealthFromPlist(string plist)
        {
            // We look for MaxCapacity and DesignCapacity in the flat plist XML.
            // Full XML parsing is avoided to keep dependencies minimal.
            long max    = ExtractIntFromPlist(plist, "MaxCapacity");
            long design = ExtractIntFromPlist(plist, "DesignCapacity");

            if (max <= 0 || design <= 0) return null;

            double health = (double)max / design * 100.0;
            return $"{Math.Round(health, 1)}%";
        }

        private static long ExtractIntFromPlist(string plist, string key)
        {
            // Finds: <key>KeyName</key><integer>VALUE</integer>
            string marker = $"<key>{key}</key>";
            int idx = plist.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return -1;

            int intStart = plist.IndexOf("<integer>", idx + marker.Length, StringComparison.Ordinal);
            if (intStart < 0) return -1;
            intStart += "<integer>".Length;

            int intEnd = plist.IndexOf("</integer>", intStart, StringComparison.Ordinal);
            if (intEnd < 0) return -1;

            return long.TryParse(plist[intStart..intEnd].Trim(), out long val) ? val : -1;
        }

        // ── ProductType → friendly name ───────────────────────────────────

        private static string MapProductTypeToName(string productType) =>
            productType switch
            {
                // iPhone 16 series
                "iPhone17,1" => "iPhone 16 Pro Max",
                "iPhone17,2" => "iPhone 16 Pro",
                "iPhone17,3" => "iPhone 16 Plus",
                "iPhone17,4" => "iPhone 16",
                // iPhone 15 series
                "iPhone16,1" => "iPhone 15",
                "iPhone16,2" => "iPhone 15 Plus",
                "iPhone16,3" => "iPhone 15 Pro",
                "iPhone16,4" => "iPhone 15 Pro Max",
                // iPhone 14 series
                "iPhone15,2" => "iPhone 14 Pro",
                "iPhone15,3" => "iPhone 14 Pro Max",
                "iPhone14,7" => "iPhone 14",
                "iPhone14,8" => "iPhone 14 Plus",
                // iPhone 13 series
                "iPhone14,4" => "iPhone 13 mini",
                "iPhone14,5" => "iPhone 13",
                "iPhone14,2" => "iPhone 13 Pro",
                "iPhone14,3" => "iPhone 13 Pro Max",
                // iPhone 12 series
                "iPhone13,1" => "iPhone 12 mini",
                "iPhone13,2" => "iPhone 12",
                "iPhone13,3" => "iPhone 12 Pro",
                "iPhone13,4" => "iPhone 12 Pro Max",
                // iPhone 11 series
                "iPhone12,1" => "iPhone 11",
                "iPhone12,3" => "iPhone 11 Pro",
                "iPhone12,5" => "iPhone 11 Pro Max",
                // iPhone SE
                "iPhone14,6" => "iPhone SE (3rd gen)",
                "iPhone12,8" => "iPhone SE (2nd gen)",
                "iPhone8,4"  => "iPhone SE (1st gen)",
                // iPad (recent)
                "iPad13,18"  => "iPad (10th gen)",
                "iPad13,19"  => "iPad (10th gen)",
                "iPad14,1"   => "iPad mini (6th gen)",
                "iPad14,2"   => "iPad mini (6th gen)",
                // iPad Pro
                "iPad14,3"   => "iPad Pro 11\" (4th gen)",
                "iPad14,4"   => "iPad Pro 11\" (4th gen)",
                "iPad14,5"   => "iPad Pro 12.9\" (6th gen)",
                "iPad14,6"   => "iPad Pro 12.9\" (6th gen)",
                _            => productType  // Fall back to raw identifier
            };

        // ── Utilities ─────────────────────────────────────────────────────

        private static void SafeCall(Action action)
        {
            try { action(); } catch { /* Ignore cleanup errors */ }
        }

        // ── IDisposable ───────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_subscription != IntPtr.Zero)
                SafeCall(() => AMD.AMDeviceNotificationUnsubscribe(_subscription));

            _runLoopThread?.Join(2000);
        }
    }
}
