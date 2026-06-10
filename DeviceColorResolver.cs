using System.Drawing;
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
            int dcCode = -1, ecCode = -1;

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
                    if (ReferenceEquals(raw, enclosureColor)) ecCode = code; else dcCode = code;

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

            // 4) Unmapped integer code (iPhone 11 / modern iPads) — identify the
            //    color by downloading Apple's own Find My device image for this
            //    exact ProductType+color-code combination and matching its body
            //    color against the known palette.
            if (ecCode >= 0)
                return ProbeAppleImage(productType ?? "", dcCode < 0 ? ecCode : dcCode, ecCode, candidates);

            return (null, null);
        }

        // ── Apple Find-My image probe ─────────────────────────────────────
        //
        // Apple hosts renderings of every device in every color, addressed by the
        // SAME numeric codes lockdownd returns:
        //   statici.icloud.com/fmipmobile/deviceImages-9.0/iPhone/iPhone12,1-{dc}-{ec}-0/online-infobox__3x.png
        // We download the image once, sample the body pixels (ignoring the screen
        // area and transparency) and pick the nearest palette color.

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
        private static readonly Dictionary<string, (string? Name, string? Hex)> _probeCache = new();
        private static readonly object _probeLock = new();

        private static (string? Name, string? Hex) ProbeAppleImage(
            string productType, int dc, int ec, List<ColorEntry>? candidates)
        {
            if (string.IsNullOrEmpty(productType) || candidates == null || candidates.Count == 0)
                return (null, null);

            string cacheKey = $"{productType}-{dc}-{ec}";
            lock (_probeLock)
                if (_probeCache.TryGetValue(cacheKey, out var cached)) return cached;

            (string? Name, string? Hex) result = (null, null);
            try
            {
                string family = productType.StartsWith("iPad", StringComparison.OrdinalIgnoreCase)
                                ? "iPad" : "iPhone";
                string url = $"https://statici.icloud.com/fmipmobile/deviceImages-9.0/" +
                             $"{family}/{productType}-{dc}-{ec}-0/online-infobox__3x.png";

                byte[] png = _http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                using var ms  = new MemoryStream(png);
                using var bmp = new Bitmap(ms);

                var best = MatchBodyColor(bmp, candidates);
                if (best != null) result = (best.Name, best.Hex);
            }
            catch { /* offline / 404 for brand-new device — just stay unknown */ }

            lock (_probeLock) _probeCache[cacheKey] = result;
            return result;
        }

        /// <summary>Diagnostics from the last image probe — shown in the debug dump.</summary>
        public static string LastProbeDebug { get; private set; } = "(no probe yet)";

        /// <summary>
        /// Identifies the housing color by sampling only the thin rim of the
        /// device silhouette (just inside the outline). The screen — which is
        /// most of a modern device's front — and the image background are never
        /// sampled, so they can't skew the result. Shading on the rendered rim
        /// is tolerated by comparing chroma (hue) more strongly than brightness.
        /// </summary>
        private static ColorEntry? MatchBodyColor(Bitmap bmp, List<ColorEntry> candidates)
        {
            var palette = candidates.Where(c => !string.IsNullOrEmpty(c.Hex))
                                    .Select(c => (entry: c, rgb: SplitRgb(c.Hex!.TrimStart('#'))))
                                    .ToList();
            if (palette.Count == 0) return null;

            int w = bmp.Width, h = bmp.Height;

            // Background = average of the four 3x3 corner patches. The probe must
            // work whether the PNG background is transparent OR a solid color.
            (double r, double g, double b, double a) bg = (0, 0, 0, 0);
            int n = 0;
            foreach ((int cx, int cy) in new[] { (1, 1), (w - 2, 1), (1, h - 2), (w - 2, h - 2) })
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    Color p = bmp.GetPixel(cx + dx, cy + dy);
                    bg = (bg.r + p.R, bg.g + p.G, bg.b + p.B, bg.a + p.A); n++;
                }
            bg = (bg.r / n, bg.g / n, bg.b / n, bg.a / n);

            bool IsBackground(Color p)
            {
                if (p.A < 220) return true;                       // transparent
                if (bg.a < 220) return false;                     // bg transparent, px opaque
                double d = Math.Pow(p.R - bg.r, 2) + Math.Pow(p.G - bg.g, 2) + Math.Pow(p.B - bg.b, 2);
                return d < 35 * 35 * 3;                           // same as bg color
            }

            // Walk each row inward from both sides; the first non-background run
            // is the device outline — sample a few pixels just inside it.
            var votes = new int[palette.Count];
            int samples = 0;
            int yStart = (int)(h * 0.18), yEnd = (int)(h * 0.82); // avoid rounded corners
            const int skipEdge = 2, rimWidth = 6;

            for (int y = yStart; y < yEnd; y += 2)
            {
                foreach (bool fromLeft in new[] { true, false })
                {
                    int x = fromLeft ? 0 : w - 1, step = fromLeft ? 1 : -1, run = 0;
                    while (x >= 0 && x < w)
                    {
                        if (!IsBackground(bmp.GetPixel(x, y))) { if (++run >= 2) break; }
                        else run = 0;
                        x += step;
                    }
                    if (x < 0 || x >= w) continue;

                    for (int k = skipEdge; k < skipEdge + rimWidth; k++)
                    {
                        int sx = x + k * step;
                        if (sx < 0 || sx >= w) break;
                        Color px = bmp.GetPixel(sx, y);
                        if (IsBackground(px)) break;

                        int best = -1; double bestD = double.MaxValue;
                        for (int i = 0; i < palette.Count; i++)
                        {
                            double d = ChromaDistance(px, palette[i].rgb);
                            if (d < bestD) { bestD = d; best = i; }
                        }
                        if (best >= 0) { votes[best]++; samples++; }
                    }
                }
            }

            int winner = -1, max = 0;
            for (int i = 0; i < votes.Length; i++)
                if (votes[i] > max) { max = votes[i]; winner = i; }

            LastProbeDebug =
                $"samples={samples}, votes=[{string.Join(", ", palette.Select((p, i) => $"{p.entry.Name}:{votes[i]}"))}]";

            // Require enough rim pixels and a >40% winner share.
            return winner >= 0 && samples > 60 && max > samples * 0.40
                   ? palette[winner].entry : null;
        }

        /// <summary>
        /// Distance that prioritizes hue/chroma over brightness, so a shaded or
        /// highlighted rendering of "Blue" still lands on Blue rather than Black.
        /// </summary>
        private static double ChromaDistance(Color px, (int r, int g, int b) pal)
        {
            double lp = 0.299 * px.R + 0.587 * px.G + 0.114 * px.B;
            double lc = 0.299 * pal.r + 0.587 * pal.g + 0.114 * pal.b;

            // chroma = color with luminance removed
            double crP = px.R - lp, cgP = px.G - lp, cbP = px.B - lp;
            double crC = pal.r - lc, cgC = pal.g - lc, cbC = pal.b - lc;

            double chroma = Math.Pow(crP - crC, 2) + Math.Pow(cgP - cgC, 2) + Math.Pow(cbP - cbC, 2);
            double luma   = Math.Pow(lp - lc, 2);

            return chroma * 4.0 + luma * 0.6;
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
