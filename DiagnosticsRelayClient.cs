using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using iDeviceInfo.Native;

namespace iDeviceInfo
{
    internal static class DiagnosticsRelayClient
    {
        private const string DiagnosticsRelayService = "com.apple.mobile.diagnostics_relay";

        public static string? ReadBatteryHealth(IntPtr device)
        {
            IntPtr connection;
            try
            {
                connection = StartDiagnosticsRelay(device);
            }
            catch
            {
                return null;
            }

            if (connection == IntPtr.Zero) return null;

            try
            {
                foreach ((string key, bool isClass) in new[]
                {
                    ("AppleSmartBattery", false),
                    ("AppleSmartBattery", true),
                    ("AppleARMPMUCharger", false),
                    ("AppleARMPMUCharger", true),
                })
                {
                    XDocument? response = QueryIoRegistry(connection, key, isClass);
                    if (response == null) continue;

                    string? health = BatteryHealthFromDiagnostics(response);
                    if (!string.IsNullOrEmpty(health))
                        return health;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                try { AMD.AMDServiceConnectionInvalidate(connection); } catch { }
            }

            return null;
        }

        private static IntPtr StartDiagnosticsRelay(IntPtr device)
        {
            IntPtr serviceName = CF.ToCFString(DiagnosticsRelayService);
            try
            {
                try
                {
                    int secure = AMD.AMDeviceSecureStartService(
                        device, serviceName, IntPtr.Zero, out IntPtr secureConnection);
                    if (secure == 0 && secureConnection != IntPtr.Zero)
                        return secureConnection;
                }
                catch (EntryPointNotFoundException)
                {
                    // Older Apple MobileDevice builds only expose AMDeviceStartService.
                }

                try
                {
                    int legacy = AMD.AMDeviceStartService(
                        device, serviceName, out IntPtr legacyConnection, IntPtr.Zero);
                    return legacy == 0 ? legacyConnection : IntPtr.Zero;
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }
            finally
            {
                CF.CFRelease(serviceName);
            }
        }

        private static XDocument? QueryIoRegistry(IntPtr connection, string entry, bool queryByClass)
        {
            string selectorKey = queryByClass ? "EntryClass" : "EntryName";
            string escapedEntry = SecurityElementEscape(entry);
            string request =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" " +
                "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" +
                "<plist version=\"1.0\"><dict>" +
                $"<key>{selectorKey}</key><string>{escapedEntry}</string>" +
                "<key>Request</key><string>IORegistry</string>" +
                "</dict></plist>";

            byte[] plistBytes = Encoding.UTF8.GetBytes(request);
            byte[] packet = new byte[4 + plistBytes.Length];
            int len = IPAddress.HostToNetworkOrder(plistBytes.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(len), 0, packet, 0, 4);
            Buffer.BlockCopy(plistBytes, 0, packet, 4, plistBytes.Length);

            UIntPtr sent = AMD.AMDServiceConnectionSend(
                connection, packet, (UIntPtr)packet.Length);
            if (sent == UIntPtr.Zero) return null;

            byte[] header = ReceiveExact(connection, 4);
            if (header.Length != 4) return null;

            int responseLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
            if (responseLength <= 0 || responseLength > 8 * 1024 * 1024) return null;

            byte[] responseBytes = ReceiveExact(connection, responseLength);
            if (responseBytes.Length != responseLength) return null;

            string response = Encoding.UTF8.GetString(responseBytes);
            if (!response.TrimStart().StartsWith("<", StringComparison.Ordinal))
                return null;

            try { return XDocument.Parse(response); }
            catch { return null; }
        }

        private static byte[] ReceiveExact(IntPtr connection, int length)
        {
            byte[] result = new byte[length];
            int offset = 0;

            while (offset < length)
            {
                byte[] chunk = new byte[length - offset];
                UIntPtr received = AMD.AMDServiceConnectionReceive(
                    connection, chunk, (UIntPtr)chunk.Length);
                ulong count = received.ToUInt64();
                if (count == 0 || count > (ulong)chunk.Length) break;

                Buffer.BlockCopy(chunk, 0, result, offset, (int)count);
                offset += (int)count;
            }

            if (offset == length) return result;

            byte[] partial = new byte[offset];
            Buffer.BlockCopy(result, 0, partial, 0, offset);
            return partial;
        }

        private static string? BatteryHealthFromDiagnostics(XDocument doc)
        {
            string? direct = FindPlistStringValue(doc, "MaximumCapacityPercent") ??
                             FindPlistStringValue(doc, "BatteryHealthPercent") ??
                             FindPlistStringValue(doc, "BatteryMaximumCapacity");
            if (TryParseNumber(direct, out double directPercent))
                return FormatPercent(directPercent);

            string? actualText = FindPlistStringValue(doc, "AppleRawMaxCapacity") ??
                                 FindPlistStringValue(doc, "MaxCapacity") ??
                                 FindPlistStringValue(doc, "NominalChargeCapacity");
            string? designText = FindPlistStringValue(doc, "DesignCapacity");

            if (TryParseNumber(actualText, out double actual) &&
                TryParseNumber(designText, out double design) &&
                actual > 0 &&
                design > 0)
            {
                return FormatPercent(actual / design * 100d);
            }

            return null;
        }

        private static string FormatPercent(double value)
        {
            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            return rounded.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static bool TryParseNumber(string? value, out double number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string trimmed = value.Trim().TrimEnd('%');
            return double.TryParse(
                trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        private static string? FindPlistStringValue(XDocument doc, string key)
        {
            foreach (XElement keyElement in doc.Descendants("key"))
            {
                if (!string.Equals(keyElement.Value, key, StringComparison.Ordinal))
                    continue;

                XElement? valueElement = keyElement.ElementsAfterSelf().FirstOrDefault();
                if (valueElement == null) continue;

                return valueElement.Name.LocalName switch
                {
                    "integer" or "real" or "string" => valueElement.Value,
                    "true" => "true",
                    "false" => "false",
                    _ => null
                };
            }

            return null;
        }

        private static string SecurityElementEscape(string value)
            => value
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
