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

        // ── Storage ───────────────────────────────────────────────────────

        /// <summary>
        /// Storage capacity rounded to nearest standard size (e.g. 128).
        /// 0 means not available.
        /// </summary>
        public int StorageGB { get; set; } = 0;

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Full model string: "iPad Pro 10.5-inch Wi-Fi 256GB".
        /// Falls back gracefully when storage is not available.
        /// Appends Wi-Fi / Wi-Fi + Cellular suffix for iPads.
        /// </summary>
        public string FullModelName
        {
            get
            {
                string model = string.IsNullOrEmpty(ModelName) ? ProductType : ModelName;
                bool isIpad = ProductType.StartsWith("iPad", StringComparison.OrdinalIgnoreCase);
                if (isIpad)
                {
                    bool hasCellular = !string.IsNullOrEmpty(IMEI) && IMEI != "N/A";
                    model += hasCellular ? " Wi-Fi + Cellular" : " Wi-Fi";
                }
                if (StorageGB > 0) model += " " + (StorageGB >= 1024 ? "1TB" : StorageGB + "GB");
                return model;
            }
        }

        /// <summary>
        /// Formats all key fields into a single clipboard-friendly string.
        /// </summary>
        public string ToClipboardText() =>
            $"Device Name:    {DeviceName}\n" +
            $"Model:          {FullModelName}\n" +
            $"iOS Build:      {iOSVersion}\n" +
            $"Serial Number:  {SerialNumber}\n" +
            $"IMEI:           {IMEI}\n" +
            (string.IsNullOrEmpty(IMEI2) ? "" : $"IMEI 2:         {IMEI2}\n") +
            $"Battery Level:  {BatteryLevel}{(IsCharging ? " (Charging)" : "")}\n" +
            $"Battery Health: {BatteryHealth}\n" +
            $"UDID:           {UDID}";
    }
}
