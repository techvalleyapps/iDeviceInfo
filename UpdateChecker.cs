using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace iDeviceInfo
{
    /// <summary>Result of a successful update check.</summary>
    internal sealed record UpdateInfo(Version Version, string TagName, string HtmlUrl, string? InstallerUrl);

    /// <summary>Checks GitHub Releases for a newer published version than the running build.</summary>
    internal static class UpdateChecker
    {
        private const string LatestReleaseUrl =
            "https://api.github.com/repos/techvalleyapps/iDeviceInfo/releases/latest";

        /// <summary>
        /// Returns info about the latest release if it is newer than the currently running
        /// version, or null if up to date / the check failed (network, rate limit, etc).
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("iDeviceInfo-UpdateChecker");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                http.Timeout = TimeSpan.FromSeconds(10);

                using var resp = await http.GetAsync(LatestReleaseUrl).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc    = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("tag_name", out var tagProp)) return null;
                string? tag = tagProp.GetString();
                if (string.IsNullOrWhiteSpace(tag)) return null;

                string versionText = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? tag[1..]
                    : tag;
                if (!Version.TryParse(versionText, out Version? latest)) return null;

                Version current = Assembly.GetExecutingAssembly().GetName().Version
                                  ?? new Version(0, 0, 0, 0);

                if (latest <= current) return null;

                string htmlUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp)
                    ? urlProp.GetString() ?? DefaultReleaseUrl
                    : DefaultReleaseUrl;

                string? installerUrl = null;
                if (doc.RootElement.TryGetProperty("assets", out var assetsProp) &&
                    assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                        if (name != null && name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase) &&
                            asset.TryGetProperty("browser_download_url", out var dlProp))
                        {
                            installerUrl = dlProp.GetString();
                            break;
                        }
                    }
                }

                return new UpdateInfo(latest, tag, htmlUrl, installerUrl);
            }
            catch
            {
                // Offline, rate-limited, DNS failure, etc. — silently skip; not worth bothering the user.
                return null;
            }
        }

        private const string DefaultReleaseUrl =
            "https://github.com/techvalleyapps/iDeviceInfo/releases/latest";

        /// <summary>Downloads the installer asset to a temp file and returns its path.</summary>
        public static async Task<string> DownloadInstallerAsync(string installerUrl, string tagName)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"iDeviceInfoSetup-{tagName}.exe");

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("iDeviceInfo-UpdateChecker");
            http.Timeout = TimeSpan.FromMinutes(5);

            using var resp = await http.GetAsync(installerUrl).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using (var fs = File.Create(tempPath))
                await resp.Content.CopyToAsync(fs).ConfigureAwait(false);

            return tempPath;
        }
    }
}
