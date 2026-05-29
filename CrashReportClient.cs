using System;
using System.Collections.Generic;
using System.Text;
using iDeviceInfo.Native;

namespace iDeviceInfo
{
    /// <summary>
    /// Reads the number of crash reports stored on a connected iOS device.
    ///
    /// Uses the com.apple.crashreportcopymobile service, which provides AFC
    /// (Apple File Conduit) access to /var/mobile/Library/Logs/CrashReporter/.
    /// Requires an active lockdown session (call after AMDeviceStartSession).
    /// </summary>
    internal static class CrashReportClient
    {
        private const string ServiceName = "com.apple.crashreportcopymobile";

        // ── AFC protocol constants ─────────────────────────────────────────
        // AFC packet layout (all values little-endian):
        //   offset  0 –  7 : magic "CFA6LPAA"
        //   offset  8 – 15 : total packet length (header + data)
        //   offset 16 – 23 : header length (always 40)
        //   offset 24 – 31 : packet ID (request/response correlation)
        //   offset 32 – 39 : operation code
        //   offset 40+     : data payload

        private static readonly byte[] AFC_MAGIC   = Encoding.ASCII.GetBytes("CFA6LPAA");
        private const ulong AFC_HEADER_LEN          = 40;
        private const ulong AFC_OP_STATUS           = 1;   // error/status response
        private const ulong AFC_OP_LIST_DIR         = 3;   // ReadDirectory

        /// <summary>
        /// Returns the total number of crash report files found on the device,
        /// or -1 if the service could not be reached.
        /// Must be called while a lockdown session is active.
        /// </summary>
        public static int GetCrashReportCount(IntPtr device)
        {
            IntPtr svcName = IntPtr.Zero;
            IntPtr conn    = IntPtr.Zero;
            try
            {
                svcName = CF.ToCFString(ServiceName);

                // Secure service (iOS 14+)
                int r = AMD.AMDeviceSecureStartService(device, svcName, IntPtr.Zero, out conn);
                if (r != 0 || conn == IntPtr.Zero) return -1;

                // List the AFC root ("/") — maps to the CrashReporter directory
                List<string>? entries = AfcListDir(conn, "/");
                if (entries == null) return -1;

                int count = 0;
                foreach (string name in entries)
                {
                    // Skip navigation entries and ignore subdirectory names
                    // (subdirectory names appear without extension)
                    if (name == "." || name == "..") continue;

                    // Count recognised crash-report file types
                    if (IsCrashFile(name)) count++;
                }

                // If no recognised extensions found, fall back to total entry count
                // (some iOS versions use unfamiliar names)
                return count > 0 ? count : Math.Max(0, entries.Count - 2 /* strip . and .. */);
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (svcName != IntPtr.Zero) { try { CF.CFRelease(svcName); }            catch { } }
                if (conn    != IntPtr.Zero) { try { AMD.AMDServiceConnectionInvalidate(conn); } catch { } }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static bool IsCrashFile(string name)
        {
            // .ips  – standard crash/jetsam log (iOS 15+)
            // .plist – legacy crash report (iOS < 15)
            // .json  – some system diagnostics
            // Other patterns seen in the wild
            return name.EndsWith(".ips",   StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".plist", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".json",  StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("JetsamEvent", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Tombstone",   StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string>? AfcListDir(IntPtr conn, string path)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
            ulong  totalLen  = AFC_HEADER_LEN + (ulong)pathBytes.Length;

            byte[] packet = new byte[(int)totalLen];

            // Header
            Buffer.BlockCopy(AFC_MAGIC,                                  0, packet,  0, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(totalLen),            0, packet,  8, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(AFC_HEADER_LEN),      0, packet, 16, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(1UL),                 0, packet, 24, 8); // packet id
            Buffer.BlockCopy(BitConverter.GetBytes(AFC_OP_LIST_DIR),     0, packet, 32, 8);

            // Payload
            Buffer.BlockCopy(pathBytes, 0, packet, (int)AFC_HEADER_LEN, pathBytes.Length);

            // Send
            UIntPtr sent = AMD.AMDServiceConnectionSend(conn, packet, (UIntPtr)packet.Length);
            if (sent == UIntPtr.Zero) return null;

            // Receive response header
            byte[] respHdr = ReceiveExact(conn, (int)AFC_HEADER_LEN);
            if (respHdr.Length != (int)AFC_HEADER_LEN) return null;

            // Verify magic
            for (int i = 0; i < 8; i++)
                if (respHdr[i] != AFC_MAGIC[i]) return null;

            ulong respTotal  = BitConverter.ToUInt64(respHdr,  8);
            ulong respHdrLen = BitConverter.ToUInt64(respHdr, 16);
            ulong respOp     = BitConverter.ToUInt64(respHdr, 32);

            // STATUS packet means an error (e.g. directory not found)
            if (respOp == AFC_OP_STATUS) return null;

            int dataLen = (int)(respTotal - respHdrLen);
            if (dataLen <= 0) return new List<string>();

            byte[] data = ReceiveExact(conn, dataLen);
            if (data.Length == 0) return new List<string>();

            // Parse: entries are null-separated UTF-8 strings
            var entries = new List<string>();
            int start = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0)
                {
                    if (i > start)
                        entries.Add(Encoding.UTF8.GetString(data, start, i - start));
                    start = i + 1;
                }
            }
            return entries;
        }

        private static byte[] ReceiveExact(IntPtr conn, int length)
        {
            byte[] buf    = new byte[length];
            int    offset = 0;

            while (offset < length)
            {
                byte[]  chunk = new byte[length - offset];
                UIntPtr got   = AMD.AMDServiceConnectionReceive(conn, chunk, (UIntPtr)chunk.Length);
                ulong   n     = got.ToUInt64();
                if (n == 0 || n > (ulong)chunk.Length) break;
                Buffer.BlockCopy(chunk, 0, buf, offset, (int)n);
                offset += (int)n;
            }

            if (offset == length) return buf;

            // Partial read — return what we have
            byte[] partial = new byte[offset];
            Buffer.BlockCopy(buf, 0, partial, 0, offset);
            return partial;
        }
    }
}
