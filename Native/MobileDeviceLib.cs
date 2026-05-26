using System;
using System.Runtime.InteropServices;

namespace iDeviceInfo.Native
{
    /// <summary>
    /// P/Invoke wrappers for Apple's MobileDevice.dll.
    /// Located at: C:\Program Files\Common Files\Apple\Mobile Device Support\MobileDevice.dll
    ///
    /// This is the same library iTunes and 3uTools use to communicate with iOS devices.
    /// Requires Apple Mobile Device Service to be running (installed with iTunes/Apple Devices).
    /// </summary>
    internal static class AMD
    {
        internal const string DllPath =
            @"C:\Program Files\Common Files\Apple\Mobile Device Support\MobileDevice.dll";

        // ── Device Notification Messages ──────────────────────────────────

        public const uint MSG_CONNECTED    = 1;
        public const uint MSG_DISCONNECTED = 2;
        public const uint MSG_PAIRED       = 3;

        // ── Structures ────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceCallbackInfo
        {
            public IntPtr Device;       // AMDeviceRef
            public uint   Message;      // MSG_CONNECTED / MSG_DISCONNECTED / MSG_PAIRED
            public IntPtr Subscription; // AMDeviceNotificationRef
        }

        // ── Delegates ─────────────────────────────────────────────────────

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DeviceNotificationCallback(
            ref DeviceCallbackInfo info, IntPtr cookie);

        // ── Notification Subscription ─────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceNotificationSubscribe(
            DeviceNotificationCallback callback,
            uint unused1, uint unused2,
            IntPtr cookie,
            out IntPtr subscription);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceNotificationUnsubscribe(IntPtr subscription);

        // ── Connection Lifecycle ──────────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceConnect(IntPtr device);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDevicePair(IntPtr device);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceValidatePairing(IntPtr device);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceStartSession(IntPtr device);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceStopSession(IntPtr device);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceDisconnect(IntPtr device);

        // ── Value Reading ─────────────────────────────────────────────────

        /// <summary>
        /// Reads a value from the device lockdown service.
        /// domain: pass IntPtr.Zero for the default domain, or a CFStringRef for a specific domain
        ///         (e.g. "com.apple.mobile.battery").
        /// key: CFStringRef for the key name.
        /// Returns a CFTypeRef (CFString, CFNumber, or CFBoolean). Caller must CFRelease.
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr AMDeviceCopyValue(
            IntPtr device,
            IntPtr domain,   // CFStringRef or IntPtr.Zero
            IntPtr key);     // CFStringRef

        // ── Service Start ─────────────────────────────────────────────────

        /// <summary>
        /// Starts a lockdown service on the device (e.g. diagnostics relay).
        /// serviceHandle receives a socket/SSL handle on success.
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceStartService(
            IntPtr device,
            IntPtr serviceName,    // CFStringRef
            out IntPtr serviceHandle,
            IntPtr unknown);       // pass IntPtr.Zero

        // ── Run Loop ──────────────────────────────────────────────────────

        /// <summary>
        /// Pumps the CoreFoundation run loop so device notifications are delivered.
        /// Call this in a background thread loop.
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AMDSetLogLevel(int level);
    }
}
