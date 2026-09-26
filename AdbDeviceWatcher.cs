using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace iDeviceInfo
{
    /// <summary>
    /// Detects Android phones (Samsung, Pixel, Xiaomi, ...) through the Android Debug
    /// Bridge and reads their details with <c>adb shell</c>. The phone needs USB
    /// debugging enabled and the PC authorised once ("Allow USB debugging?").
    ///
    /// Exposes the same events as <see cref="DeviceWatcher"/>; they fire on the thread
    /// that called <see cref="Start"/> (the WinForms UI thread).
    /// </summary>
    public sealed class AdbDeviceWatcher : IDisposable
    {
        public event EventHandler<DeviceInfo>? DeviceConnected;
        public event EventHandler<string>?     DeviceDisconnected;

        /// <summary>Raised when a device is attached but has not authorised this PC.</summary>
        public event EventHandler<string>? DeviceUnauthorized;

        private readonly HashSet<string> _known        = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unauthorized = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _cts;
        private SynchronizationContext?  _ui;
        private string? _adb;

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
            foreach (var s in _known.ToList()) Raise(() => DeviceDisconnected?.Invoke(this, s));
            _known.Clear();
            _unauthorized.Clear();
            Start();
        }

        private void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        public void Dispose() => Stop();

        // ── Polling ───────────────────────────────────────────────────────

        private void PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { PollOnce(); } catch { /* adb missing / transient error: retry */ }
                ct.WaitHandle.WaitOne(2000);
            }
        }

        private void PollOnce()
        {
            _adb ??= FindAdb();
            if (_adb == null) return;

            string list   = Run(_adb, "devices", 5000);
            var    online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var    unauth = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in list.Split('\n').Skip(1))
            {
                var parts = line.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (parts[1] == "device")            online.Add(parts[0]);
                else if (parts[1] == "unauthorized") unauth.Add(parts[0]);
            }

            foreach (var id in unauth.Where(u => _unauthorized.Add(u)).ToList())
                Raise(() => DeviceUnauthorized?.Invoke(this, id));
            _unauthorized.IntersectWith(unauth);

            foreach (var id in _known.Where(k => !online.Contains(k)).ToList())
            {
                _known.Remove(id);
                Raise(() => DeviceDisconnected?.Invoke(this, id));
            }

            foreach (var id in online.Where(o => !_known.Contains(o)).ToList())
            {
                var info = ReadDevice(id);
                if (info == null) continue;
                _known.Add(id);
                Raise(() => DeviceConnected?.Invoke(this, info));
            }
        }

        private void Raise(Action a)
        {
            if (_ui != null) _ui.Post(_ => a(), null);
            else a();
        }

        // ── Reading a device ──────────────────────────────────────────────

        private DeviceInfo? ReadDevice(string id)
        {
            string Sh(string cmd) => Run(_adb!, $"-s {id} shell {cmd}", 8000).Trim();

            var props = ParseGetProp(Run(_adb!, $"-s {id} shell getprop", 8000));
            if (props.Count == 0) return null;
            string P(string k) => props.TryGetValue(k, out var v) ? v : "";

            string maker   = P("ro.product.manufacturer");
            string model   = P("ro.product.model");
            string market  = FirstNonEmpty(P("ro.product.marketname"), P("ro.config.marketing_name"),
                                           P("ro.vendor.oplus.market.name"), P("ro.product.odm.marketname"));
            string display = !string.IsNullOrEmpty(market) ? market : model;
            string brand   = Capitalize(maker);

            string userName = Sh("settings get global device_name");
            if (userName is "" or "null") userName = display;

            string androidId = Sh("settings get secure android_id");
            if (androidId == "null") androidId = "";

            string patch = P("ro.build.version.security_patch");

            var info = new DeviceInfo
            {
                Platform     = "Android",
                AdbId        = id,
                DeviceName   = userName,
                ModelName    = string.IsNullOrEmpty(brand) || display.StartsWith(brand, StringComparison.OrdinalIgnoreCase)
                                   ? display : $"{brand} {display}",
                ProductType  = model,
                iOSVersion   = $"Android {P("ro.build.version.release")}" +
                               (patch.Length > 0 ? $" (patch {patch})" : ""),
                SerialNumber = FirstNonEmpty(P("ro.serialno"), P("ro.boot.serialno"), id),
                UDID         = androidId,
                // Colour: only a few OEMs expose it
                ColorName    = FirstNonEmpty(P("ro.config.color"), P("ro.boot.hardware.color"),
                                             P("ro.boot.product.hardware.color"), P("ro.product.color")),
            };

            // IMEI: blocked for the shell user on Android 10+ unless rooted, so often stays N/A
            var imeis = ReadImeis(id);
            if (imeis.Count > 0) info.IMEI  = imeis[0];
            if (imeis.Count > 1) info.IMEI2 = imeis[1];

            ReadBattery(id, info);
            info.StorageGB = ReadStorageGB(id);
            return info;
        }

        private List<string> ReadImeis(string id)
        {
            var result = new List<string>();
            foreach (var args in new[] { "1", "1 i32 1" })   // slot 0 / slot 1 on most builds
            {
                string imei = ParseParcelString(Run(_adb!, $"-s {id} shell service call iphonesubinfo {args}", 5000));
                if (imei.Length >= 14 && imei.All(char.IsDigit) && !result.Contains(imei)) result.Add(imei);
            }
            return result;
        }

        private void ReadBattery(string id, DeviceInfo info)
        {
            string dump = Run(_adb!, $"-s {id} shell dumpsys battery", 5000);
            var m = Regex.Match(dump, @"^\s*level:\s*(\d+)", RegexOptions.Multiline);
            if (m.Success) info.BatteryLevel = m.Groups[1].Value + "%";
            var st = Regex.Match(dump, @"^\s*status:\s*(\d+)", RegexOptions.Multiline);
            info.IsCharging = st.Success && st.Groups[1].Value == "2";

            // Health = full capacity / design capacity, when the kernel exposes both.
            foreach (var dir in new[] { "/sys/class/power_supply/battery", "/sys/class/power_supply/bms" })
            {
                string full   = Run(_adb!, $"-s {id} shell cat {dir}/charge_full", 4000).Trim();
                string design = Run(_adb!, $"-s {id} shell cat {dir}/charge_full_design", 4000).Trim();
                if (double.TryParse(full, out var f) && double.TryParse(design, out var d) && d > 0 && f > 0)
                {
                    info.BatteryHealth = Math.Min(100, (int)Math.Round(f / d * 100)) + "%";
                    return;
                }
            }
        }

        private int ReadStorageGB(string id)
        {
            // `df /data` reports usable 1K blocks; round up to the marketed size.
            string df   = Run(_adb!, $"-s {id} shell df -k /data", 5000);
            var    line = df.Split('\n').Skip(1).FirstOrDefault(l => l.Trim().Length > 0);
            if (line == null) return 0;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !double.TryParse(parts[1], out var kb)) return 0;

            double gb = kb / 1024.0 / 1024.0;
            foreach (int size in new[] { 16, 32, 64, 128, 256, 512, 1024, 2048 })
                if (gb <= size) return size;
            return (int)Math.Ceiling(gb);
        }

        // ── Parsing helpers ───────────────────────────────────────────────

        private static Dictionary<string, string> ParseGetProp(string text)
        {
            var d = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(text, @"^\[(.+?)\]: \[(.*?)\]\s*$", RegexOptions.Multiline))
                d[m.Groups[1].Value] = m.Groups[2].Value;
            return d;
        }

        /// <summary>Extracts the text from `service call` parcel output: 0x.. 'abcd' 'efgh'.</summary>
        private static string ParseParcelString(string raw)
        {
            var sb = new StringBuilder();
            foreach (Match m in Regex.Matches(raw, "'([^']*)'"))
                sb.Append(m.Groups[1].Value);
            return sb.ToString().Replace(".", "").Trim();
        }

        private static string FirstNonEmpty(params string[] v) =>
            v.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";

        private static string Capitalize(string s) =>
            s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

        // ── adb discovery / execution ─────────────────────────────────────

        private static string? FindAdb()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "adb.exe"),
                Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            };
            foreach (var env in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
                if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } root)
                    candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", "adb.exe"));
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));

            return candidates.FirstOrDefault(File.Exists);
        }

        private static string Run(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p      = Process.Start(psi)!;
            var       stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
            return stdout.GetAwaiter().GetResult();
        }
    }
}
