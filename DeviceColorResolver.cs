using System.Reflection;
using System.Text.Json;

namespace iDeviceInfo
{
    /// <summary>
    /// Resolves a device's marketing color name (e.g. "Desert Titanium") from the
    /// lockdownd values DeviceColor / DeviceEnclosureColor plus the ProductType.
    ///
    /// Data source: apple_device_colors.json (compiled from AppleDB). The file is
    /// loaded from the exe directory if present (so it can be updated when new
    /// devices launch) and falls back to the embedded copy.
    ///
    /// lockdownd returns three different formats depending on device generation:
    ///   • old devices            → a color name, e.g. "black"
    ///   • iPhone 6 – X era       → a hex string,  e.g. "#e1e4e3"
    ///   • iPhone 7 and newer     → DeviceEnclosureColor is a small integer enum
    ///                              that is model-specific (1 = Black on an XR,
    ///                              1 = Space Gray on an 8, ...)
    /// </summary>
    public static class DeviceColorResolver
    {
        public record ColorEntry(string Name, string? Hex);

        private static readonly Lazy<Dictionary<string, List<ColorEntry>>> _colors  = new(LoadColors);
        private static readonly Lazy<Dictionary<string, string>>           _names   = new(LoadNames);

        private static Dictionary<string, List<ColorEntry>> ColorMap => _colors.Value;

        /// <summary>Marketing model names from the JSON ("iPhone18,2" → "iPhone 17 Pro Max").
        /// Useful as a fallback for devices newer than the built-in table.</summary>
        public static string? LookupProductName(string productType)
            => _names.Value.TryGetValue(productType, out string? n) ? n : null;

        /// <summary>
        /// Resolve the color for a device. Returns (name, hex) or (null, null) when unknown.
        /// <paramref name="deviceColor"/> / <paramref name="enclosureColor"/> are the raw
        /// lockdownd string values (may be null).
        /// </summary>
        public static (string? Name, string? Hex) Resolve(
            string productType, string? deviceColor, string? enclosureColor)
        {
            ColorMap.TryGetValue(productType ?? "", out List<ColorEntry>? candidates);

            // Prefer the enclosure (back/housing) color — that's the color the
            // device is marketed/sold as. DeviceColor is only the front glass.
            foreach (string? raw in new[] { enclosureColor, deviceColor })
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string v = raw.Trim();

                // 1) Hex value ("#3b3b3c" or "3b3b3c")
                string hexPart = v.StartsWith('#') ? v[1..] : v;
                if (hexPart.Length == 6 && hexPart.All(Uri.IsHexDigit))
                {
                    var hit = MatchByHex(hexPart, candidates);
                    if (hit != null) return (hit.Name, hit.Hex);
                    return (null, "#" + hexPart.ToUpperInvariant()); // unknown name, keep swatch
                }

                // 2) Integer enum (iPhone 7 and newer DeviceEnclosureColor)
                if (int.TryParse(v, out int code))
                {
                    string? name = MapEnclosureEnum(productType ?? "", code);
                    if (name == null && candidates is { Count: 1 })
                        name = candidates[0].Name;          // only one color exists
                    if (name != null)
                        return (name, FindHexForName(name, candidates));
                    continue;                               // try the other key
                }

                // 3) Plain name ("black", "white", "space gray")
                var byName = candidates?.FirstOrDefault(c =>
                    c.Name.Equals(v, StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Replace(" ", "").Equals(v.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                if (byName != null) return (byName.Name, byName.Hex);
                return (TitleCase(v), null);
            }

            return (null, null);
        }

        // ── Matching helpers ──────────────────────────────────────────────

        private static ColorEntry? MatchByHex(string hex, List<ColorEntry>? candidates)
        {
            if (candidates == null || candidates.Count == 0) return null;

            (int r, int g, int b) target = SplitRgb(hex);
            ColorEntry? best = null;
            double bestDist = double.MaxValue;

            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c.Hex)) continue;
                (int r, int g, int b) cc = SplitRgb(c.Hex.TrimStart('#'));
                double d = Math.Pow(target.r - cc.r, 2)
                         + Math.Pow(target.g - cc.g, 2)
                         + Math.Pow(target.b - cc.b, 2);
                if (d < bestDist) { bestDist = d; best = c; }
            }

            // Accept exact matches always; fuzzy matches only within a sane distance
            // (~80 per channel) so we never label a color we can't justify.
            return bestDist <= 3 * 80 * 80 ? best : null;
        }

        private static (int r, int g, int b) SplitRgb(string hex) =>
            (Convert.ToInt32(hex[..2], 16),
             Convert.ToInt32(hex[2..4], 16),
             Convert.ToInt32(hex[4..6], 16));

        private static string? FindHexForName(string name, List<ColorEntry>? candidates)
            => candidates?.FirstOrDefault(c =>
                   c.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                   name.Contains(c.Name, StringComparison.OrdinalIgnoreCase))?.Hex;

        /// <summary>
        /// Known DeviceEnclosureColor integer → color name mappings.
        /// Sourced from Apple's Find My device-image URL scheme
        /// (statici.icloud.com/fmipmobile/.../{ProductType}-{DeviceColor}-{EnclosureColor}-0),
        /// documented in libimobiledevice issue #818. Only families with confirmed
        /// data are mapped — unknown codes return null rather than a guess.
        /// </summary>
        private static string? MapEnclosureEnum(string productType, int code)
        {
            Dictionary<int, string>? map = productType switch
            {
                // iPhone 7 / 7 Plus
                "iPhone9,1" or "iPhone9,2" or "iPhone9,3" or "iPhone9,4" => new()
                { [1] = "Black", [2] = "Silver", [3] = "Gold", [4] = "Rose Gold", [5] = "Jet Black", [6] = "(PRODUCT)RED" },

                // iPhone 8 / 8 Plus
                "iPhone10,1" or "iPhone10,2" or "iPhone10,4" or "iPhone10,5" => new()
                { [1] = "Space Gray", [2] = "Silver", [3] = "Gold", [6] = "(PRODUCT)RED" },

                // iPhone X
                "iPhone10,3" or "iPhone10,6" => new()
                { [1] = "Space Gray", [2] = "Silver" },

                // iPhone XS / XS Max
                "iPhone11,2" or "iPhone11,4" or "iPhone11,6" => new()
                { [1] = "Space Gray", [2] = "Silver", [4] = "Gold" },

                // iPhone XR
                "iPhone11,8" => new()
                { [1] = "Black", [2] = "White", [6] = "(PRODUCT)RED", [7] = "Yellow", [8] = "Coral", [9] = "Blue" },

                _ => null,
            };

            return map != null && map.TryGetValue(code, out string? name) ? name : null;
        }

        private static string TitleCase(string s) =>
            System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

        // ── JSON loading ──────────────────────────────────────────────────

        private static JsonDocument? OpenJson()
        {
            try
            {
                // 1) Updatable copy next to the exe
                string path = Path.Combine(AppContext.BaseDirectory, "apple_device_colors.json");
                if (File.Exists(path))
                    return JsonDocument.Parse(File.ReadAllText(path));

                // 2) Embedded fallback
                using Stream? s = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("iDeviceInfo.apple_device_colors.json");
                if (s != null)
                    return JsonDocument.Parse(new StreamReader(s).ReadToEnd());
            }
            catch { /* never break device reading over color data */ }
            return null;
        }

        private static Dictionary<string, List<ColorEntry>> LoadColors()
        {
            var result = new Dictionary<string, List<ColorEntry>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using JsonDocument? doc = OpenJson();
                if (doc == null) return result;

                foreach (JsonProperty dev in doc.RootElement.GetProperty("devices").EnumerateObject())
                {
                    var list = new List<ColorEntry>();
                    foreach (JsonElement c in dev.Value.GetProperty("colors").EnumerateArray())
                    {
                        string  name = c.GetProperty("name").GetString() ?? "";
                        JsonElement h = c.GetProperty("hex");
                        // hex is "#RRGGBB" or ["#front", "#back"] on a few old devices
                        string? hex = h.ValueKind == JsonValueKind.Array
                            ? h[0].GetString()
                            : h.GetString();
                        if (name.Length > 0) list.Add(new ColorEntry(name, hex));
                    }
                    if (list.Count > 0) result[dev.Name] = list;
                }
            }
            catch { /* ignore malformed data */ }
            return result;
        }

        private static Dictionary<string, string> LoadNames()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using JsonDocument? doc = OpenJson();
                if (doc == null) return result;
                foreach (JsonProperty dev in doc.RootElement.GetProperty("devices").EnumerateObject())
                {
                    string? n = dev.Value.GetProperty("name").GetString();
                    if (!string.IsNullOrEmpty(n)) result[dev.Name] = n;
                }
            }
            catch { }
            return result;
        }
    }
}
