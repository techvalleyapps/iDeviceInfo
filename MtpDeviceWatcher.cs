using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaDevices;

namespace iDeviceInfo
{
    /// <summary>
    /// Fallback for Android phones that do NOT have USB debugging enabled.
    /// Windows sees them as MTP portable devices; through the Windows Portable Devices
    /// API we can still read name, manufacturer, model, battery %, storage size and
    /// (via the parent USB device) the real serial number.
    ///
    /// IMEI, Android version and battery health are not exposed over MTP.
    /// Same events as <see cref="DeviceWatcher"/>; fired on the thread that called Start().
    /// </summary>
    public sealed class MtpDeviceWatcher : IDisposable
    {
        public event EventHandler<DeviceInfo>? DeviceConnected;
        public event EventHandler<string>?     DeviceDisconnected;

        private const string Unavailable = "N/A (needs USB debugging)";
        private const int    MaxRetries  = 10;   // re-read while the phone is locked (no storage yet)

        private readonly Dictionary<string, int> _known = new(StringComparer.OrdinalIgnoreCase); // id → storage retries left
        private CancellationTokenSource? _cts;
        private SynchronizationContext?  _ui;

        public void Start()
        {
            if (_cts != null) return;
            _ui  = SynchronizationContext.Current;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => PollLoop(token), token);
        }

        public void Restart()
        {
            Stop();
            foreach (var id in _known.Keys.ToList()) Raise(() => DeviceDisconnected?.Invoke(this, id));
            _known.Clear();
            Start();
        }

        private void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        public void Dispose() => Stop();

        private void Raise(Action a)
        {
            if (_ui != null) _ui.Post(_ => a(), null);
            else a();
        }

        // ── Polling ───────────────────────────────────────────────────────

        private void PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { PollOnce(); } catch { /* WPD unavailable / transient error: retry */ }
                ct.WaitHandle.WaitOne(3000);
            }
        }

        private void PollOnce()
        {
            var devices = MediaDevice.GetDevices().ToList();
            var present = new HashSet<string>(devices.Select(d => d.DeviceId), StringComparer.OrdinalIgnoreCase);

            foreach (var id in _known.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _known.Remove(id);
                Raise(() => DeviceDisconnected?.Invoke(this, id));
            }

            foreach (var dev in devices)
            {
                try
                {
                    bool isNew = !_known.TryGetValue(dev.DeviceId, out int retries);
                    if (!isNew && retries <= 0) continue;

                    var info = Read(dev);
                    if (info == null) continue;   // not an Android phone

                    _known[dev.DeviceId] = info.StorageGB > 0 ? 0 : (isNew ? MaxRetries : retries - 1);
                    Raise(() => DeviceConnected?.Invoke(this, info));
                }
                catch { /* device busy / locked: try again next poll */ }
                finally { dev.Dispose(); }
            }
        }

        // ── Reading a device ──────────────────────────────────────────────

        private static DeviceInfo? Read(MediaDevice dev)
        {
            dev.Connect();
            try
            {
                string maker = dev.Manufacturer ?? "";
                string model = dev.Model ?? "";

                // iPhones/iPads also show up as portable devices — DeviceWatcher handles those.
                if (maker.Contains("Apple", StringComparison.OrdinalIgnoreCase)) return null;
                // Cameras / media players are not phones.
                string type = dev.DeviceType?.ToString() ?? "";
                if (type.Contains("Camera", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("Media",  StringComparison.OrdinalIgnoreCase)) return null;

                string name  = FirstNonEmpty(dev.FriendlyName, dev.Description, model, "Android Device");
                string brand = maker.Length == 0 ? "" : char.ToUpperInvariant(maker[0]) + maker[1..];
                string shown = FirstNonEmpty(model, name);

                string serial = FirstNonEmpty(UsbSerialFromPnp(dev.PnPDeviceID), RealWpdSerial(dev.SerialNumber),
                                              dev.DeviceId);

                var info = new DeviceInfo
                {
                    Platform      = "Android",
                    Limited       = true,
                    MtpId         = dev.DeviceId,
                    DeviceName    = name,
                    ModelName     = brand.Length == 0 || shown.StartsWith(brand, StringComparison.OrdinalIgnoreCase)
                                        ? shown : $"{brand} {shown}",
                    ProductType   = model,
                    iOSVersion    = Unavailable,
                    SerialNumber  = serial,
                    IMEI          = Unavailable,
                    BatteryHealth = Unavailable,
                };

                if (dev.PowerLevel is uint pl && pl <= 100) info.BatteryLevel = pl + "%";

                try
                {
                    var drives = dev.GetDrives();
                    if (drives != null)
                        foreach (var d in drives)
                        {
                            // First drive is the internal shared storage; an SD card would inflate the size.
                            info.StorageGB = AdbDeviceWatcher.RoundToMarketedGB(d.TotalSize / 1024.0 / 1024.0 / 1024.0);
                            break;
                        }
                }
                catch { /* locked phone: no storage access yet */ }

                return info;
            }
            finally { try { dev.Disconnect(); } catch { } }
        }

        // ── Serial number helpers ─────────────────────────────────────────

        /// <summary>WPD's serial is a GUID-like placeholder on newer phones; reject those.</summary>
        private static string RealWpdSerial(string? s) =>
            string.IsNullOrWhiteSpace(s) || s.Contains('{') || s.Contains('-') && s.Length >= 32 ? "" : s.Trim();

        /// <summary>
        /// The real serial is the USB device's instance id (its iSerial descriptor).
        /// The WPD node is usually a composite-interface child (MI_xx), so use its parent.
        /// </summary>
        private static string UsbSerialFromPnp(string? pnpPath)
        {
            if (string.IsNullOrEmpty(pnpPath)) return "";
            string p = pnpPath;
            if (p.StartsWith(@"\\?\")) p = p[4..];
            int guid = p.IndexOf("#{", StringComparison.Ordinal);
            if (guid >= 0) p = p[..guid];
            string instance = p.Replace('#', '\\').ToUpperInvariant();
            if (!instance.StartsWith("USB\\")) return "";

            if (CM_Locate_DevNodeW(out uint node, instance, 0) != 0) return LastSegment(instance);
            if (instance.Contains("&MI_") && CM_Get_Parent(out uint parent, node, 0) == 0)
            {
                var sb = new StringBuilder(260);
                if (CM_Get_Device_IDW(parent, sb, sb.Capacity, 0) == 0) instance = sb.ToString();
            }
            return LastSegment(instance);
        }

        // A '&' in the last segment means Windows generated the id (device has no USB serial).
        private static string LastSegment(string instance)
        {
            string seg = instance[(instance.LastIndexOf('\\') + 1)..];
            return seg.Contains('&') ? "" : seg;
        }

        private static string FirstNonEmpty(params string?[] v) =>
            v.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? "";

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, uint flags);
    }
}
