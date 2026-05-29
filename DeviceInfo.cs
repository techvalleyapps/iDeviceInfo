namespace iDeviceInfo
{
    /// <summary>
    /// All the information we read from a connected iOS device.
    /// </summary>
    public class DeviceInfo
    {
        // ── Identity ──────────────────────────────────────────────────────

        /// <summary>User-visible device name (e.g. "John's iPhone").</summary>
        public string DeviceName    { get; set; } = "Unknown Device";

        /// <summary>Human-readable model line, e.g. "iPhone 14 Pro Max".</summary>
        public string ModelName     { get; set; } = "";

        /// <summary>Internal model identifier, e.g. "iPhone15,2".</summary>
        public string ProductType   { get; set; } = "";

        /// <summary>iOS / iPadOS version string, e.g. "17.4.1".</summary>
        public string iOSVersion    { get; set; } = "";

        // ── Hardware IDs ─────────────────────────────────────────────────

        /// <summary>Device serial number (alphanumeric, ~12 chars).</summary>
        public string SerialNumber  { get; set; } = "";

        /// <summary>IMEI — null / "N/A" for Wi-Fi-only iPads.</summary>
        public string IMEI          { get; set; } = "N/A";

        /// <summary>IMEI2 on dual-SIM devices; otherwise empty.</summary>
        public string IMEI2         { get; set; } = "";

        /// <summary>Unique Device ID (UDID) — 40-char hex string.</summary>
        public string UDID          { get; set; } = "";

        // ── Battery ───────────────────────────────────────────────────────

        /// <summary>Battery charge level, e.g. "84%".</summary>
        public string BatteryLevel  { get; set; } = "N/A";

        /// <summary>
        /// Battery health (maximum capacity vs design capacity), e.g. "91%".
        /// Requires diagnostics relay; may be "N/A" if device is too new.
        /// </summary>
        public string BatteryHealth { get; set; } = "N/A";

        /// <summary>True if the device is currently charging.</summary>
        public bool   IsCharging    { get; set; }

        // ── Diagnostics ───────────────────────────────────────────────────────

        /// <summary>
        /// Number of crash report files in the device's CrashReporter directory.
        /// -1 means the service could not be reached (device not trusted, etc.).
        /// </summary>
        public int CrashReportCount { get; set; } = -1;

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Formats all key fields into a single clipboard-friendly string.
        /// </summary>
        public string ToClipboardText() =>
            $"Device Name:    {DeviceName}\n" +
            $"Model:          {(string.IsNullOrEmpty(ModelName) ? ProductType : ModelName)}\n" +
            $"iOS Build:      {iOSVersion}\n" +
            $"Serial Number:  {SerialNumber}\n" +
            $"IMEI:           {IMEI}\n" +
            (string.IsNullOrEmpty(IMEI2) ? "" : $"IMEI 2:         {IMEI2}\n") +
            $"Battery Level:  {BatteryLevel}{(IsCharging ? " (Charging)" : "")}\n" +
            $"Battery Health: {BatteryHealth}\n" +
            (CrashReportCount >= 0 ? $"Crash Reports:  {CrashReportCount}\n" : "") +
            $"UDID:           {UDID}";
    }
}
