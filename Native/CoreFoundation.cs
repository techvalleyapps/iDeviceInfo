using System;
using System.Runtime.InteropServices;
using System.Text;

namespace iDeviceInfo.Native
{
    /// <summary>
    /// P/Invoke wrappers for Apple's CoreFoundation.dll.
    /// Located at: C:\Program Files\Common Files\Apple\Mobile Device Support\CoreFoundation.dll
    /// </summary>
    internal static class CF
    {
        internal const string DllPath =
            @"C:\Program Files\Common Files\Apple\Mobile Device Support\CoreFoundation.dll";

        // UTF-8 encoding constant
        public const uint kCFStringEncodingUTF8 = 0x08000100;

        // CFNumber type constants
        public const int kCFNumberSInt32Type = 3;
        public const int kCFNumberSInt64Type = 4;
        public const int kCFNumberIntType    = 9;

        // ── String ────────────────────────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CFStringCreateWithCString(
            IntPtr allocator, string str, uint encoding);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool CFStringGetCString(
            IntPtr cfString, byte[] buffer, int bufferSize, uint encoding);

        // ── Number ────────────────────────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool CFNumberGetValue(
            IntPtr cfNumber, int theType, out long value);

        // ── Boolean ───────────────────────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool CFBooleanGetValue(IntPtr cfBoolean);

        // ── Type IDs ──────────────────────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern long CFGetTypeID(IntPtr cf);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern long CFStringGetTypeID();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern long CFNumberGetTypeID();

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern long CFBooleanGetTypeID();

        // ── Array ─────────────────────────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern long CFArrayGetCount(IntPtr array);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);

        // ── RunLoop ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns (and creates if necessary) the CF RunLoop for the calling thread.
        /// AMD on Apple Devices app v1818+ delivers callbacks via CF RunLoop, so this
        /// must be called before AMDeviceNotificationSubscribe to avoid a native abort().
        /// Do NOT CFRelease the returned pointer (it is a borrowed reference).
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CFRunLoopGetCurrent();

        /// <summary>Runs the current thread's CF RunLoop until CFRunLoopStop is called.</summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CFRunLoopRun();

        /// <summary>Stops a running CF RunLoop, causing CFRunLoopRun to return.</summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CFRunLoopStop(IntPtr runLoop);

        /// <summary>
        /// Runs the current thread's CF RunLoop in the given mode for up to
        /// <paramref name="seconds"/> seconds, or until an event source fires
        /// (when returnAfterSourceHandled = true).
        /// Returns 0 = finished, 1 = stopped, 2 = timed out, 3 = no sources.
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int CFRunLoopRunInMode(
            IntPtr mode, double seconds, bool returnAfterSourceHandled);

        /// <summary>kCFRunLoopDefaultMode — the standard run loop mode string.</summary>
        public static IntPtr CFRunLoopDefaultMode
        {
            get
            {
                // "kCFRunLoopDefaultMode" is a CFStringRef exported from CoreFoundation
                try
                {
                    IntPtr sym = NativeLibrary.GetExport(
                        NativeLibrary.Load(DllPath), "kCFRunLoopDefaultMode");
                    // It's a CFStringRef* — dereference to get the actual pointer
                    return sym != IntPtr.Zero
                        ? System.Runtime.InteropServices.Marshal.ReadIntPtr(sym)
                        : IntPtr.Zero;
                }
                catch { return IntPtr.Zero; }
            }
        }

        // ── Release ───────────────────────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CFRelease(IntPtr cf);

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>Creates a CFStringRef from a .NET string. Caller must CFRelease.</summary>
        public static IntPtr ToCFString(string str)
            => CFStringCreateWithCString(IntPtr.Zero, str, kCFStringEncodingUTF8);

        /// <summary>
        /// Converts a CF value (CFString, CFNumber, CFBoolean) to a .NET string.
        /// Returns null if cfValue is Zero or conversion fails.
        /// </summary>
        public static string? CFValueToString(IntPtr cfValue)
        {
            if (cfValue == IntPtr.Zero) return null;

            try
            {
                long typeId    = CFGetTypeID(cfValue);
                long strType   = CFStringGetTypeID();
                long numType   = CFNumberGetTypeID();
                long boolType  = CFBooleanGetTypeID();

                if (typeId == strType)
                {
                    var buf = new byte[2048];
                    if (!CFStringGetCString(cfValue, buf, buf.Length, kCFStringEncodingUTF8))
                        return null;
                    int len = Array.IndexOf(buf, (byte)0);
                    return Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
                }

                if (typeId == numType)
                {
                    if (CFNumberGetValue(cfValue, kCFNumberSInt64Type, out long n))
                        return n.ToString();
                    return null;
                }

                if (typeId == boolType)
                    return CFBooleanGetValue(cfValue) ? "true" : "false";
            }
            catch
            {
                // Silently ignore — unknown type or bad pointer
            }

            return null;
        }
    }
}
