using System.IO;

namespace iDeviceInfo.Native
{
    /// <summary>
    /// Checks whether Apple's "Mobile Device Support" component (installed by the
    /// Apple Devices app or iTunes) is present. Without it, MobileDevice.dll /
    /// CoreFoundation.dll don't exist and any P/Invoke into them crashes the process
    /// (native abort — not a catchable managed exception).
    /// </summary>
    internal static class AppleDriverCheck
    {
        public static bool IsInstalled =>
            File.Exists(AMD.DllPath) && File.Exists(CF.DllPath);
    }
}
