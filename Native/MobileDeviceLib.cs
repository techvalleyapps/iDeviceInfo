using System;
using System.Runtime.InteropServices;

namespace iDeviceInfo.Native
{
    /// <summary>
    /// P/Invoke wrappers for Apple's MobileDevice.dll.
    /// Located at: C:\Program Files\Common Files\Apple\Mobile Device Support\MobileDevice.dll
    ///
    /// Works with both the iTunes (v12.x) and Apple Devices app (v1818+) versions.
    /// The Apple Devices app version no longer delivers AMDeviceNotificationSubscribe
    /// callbacks via the Win32 message pump, so we use AMDCreateDeviceList polling instead.
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

        // ── Device Enumeration (Apple Devices app / v1818+) ───────────────

        /// <summary>
        /// Returns a CFArrayRef containing AMDeviceRef objects for all currently
        /// connected devices known to the Apple Mobile Device Service.
        /// Caller must CFRelease the returned array.
        /// This is the reliable alternative to the notification callback in newer DLL versions.
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr AMDCreateDeviceList();

        /// <summary>
        /// Returns a CFStringRef containing the device's UDID.
        /// Does not require AMDeviceConnect or a lockdown session.
        /// Caller must CFRelease the returned string.
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr AMDeviceCopyDeviceIdentifier(IntPtr device);

        // ── Notification Subscription (iTunes / legacy) ───────────────────

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

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceStartService(
            IntPtr device,
            IntPtr serviceName,
            out IntPtr serviceConnection,
            IntPtr unknown);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceSecureStartService(
            IntPtr device,
            IntPtr serviceName,
            IntPtr options,
            out IntPtr serviceConnection);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr AMDServiceConnectionSend(
            IntPtr serviceConnection,
            byte[] buffer,
            UIntPtr length);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr AMDServiceConnectionReceive(
            IntPtr serviceConnection,
            byte[] buffer,
            UIntPtr length);

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AMDServiceConnectionInvalidate(IntPtr serviceConnection);

        // ── Value Reading ─────────────────────────────────────────────────

        /// <summary>
        /// Reads a value from the device lockdown service.
        /// domain: IntPtr.Zero for the default domain, or a CFStringRef (e.g. "com.apple.mobile.battery").
        /// key: CFStringRef for the key name.
        /// Returns a CFTypeRef. Caller must CFRelease.
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr AMDeviceCopyValue(
            IntPtr device,
            IntPtr domain,
            IntPtr key);

        // ── Interface type ────────────────────────────────────────────────

        /// <summary>
        /// Returns the physical connection type for a device handle.
        /// Does not require AMDeviceConnect.
        ///   1 (INTERFACE_USB)  = wired USB
        ///   2 (INTERFACE_WIFI) = WiFi / network
        /// </summary>
        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AMDeviceGetInterfaceType(IntPtr device);

        public const int INTERFACE_USB  = 1;
        public const int INTERFACE_WIFI = 2;

        // ── Misc ──────────────────────────────────────────────────────────

        [DllImport(DllPath, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AMDSetLogLevel(int level);
    }
}
