using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SsdGuardian.Windows;

internal static class NativeMethods
{
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const int ProcessProtectionLevelInfo = 7;
    internal const uint ProtectionLevelNone = 0xFFFF_FFFE;
    internal const uint IoctlStorageQueryProperty = 0x002D_1400;
    internal const uint IoctlVolumeGetVolumeDiskExtents = 0x0056_0000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input,
        int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint QueryDosDevice(string? name, [Out] char[] target, int length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindFirstVolume(StringBuilder volume, int length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextVolume(IntPtr search, StringBuilder volume, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindVolumeClose(IntPtr search);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumePathNamesForVolumeName(string volume, [Out] char[] paths,
        int length, out int required);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint GetDriveType(string root);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
        StringBuilder path, ref int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessInformation(SafeProcessHandle process, int informationClass,
        out uint information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int StartTrace(out ulong sessionHandle, string sessionName, [In, Out] byte[] properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ControlTrace(ulong sessionHandle, string? sessionName, [In, Out] byte[] properties,
        uint controlCode);

    internal static SafeFileHandle OpenDevice(string path) =>
        CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);

    internal static DateTimeOffset? ReadCreationTime(SafeProcessHandle process) =>
        GetProcessTimes(process, out long creation, out _, out _, out _) && creation > 0
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(creation))
            : null;

    internal static bool TryReadRunningCreation(SafeProcessHandle process, out DateTimeOffset creation)
    {
        const uint stillActive = 259;
        if (GetExitCodeProcess(process, out uint exitCode) && exitCode == stillActive &&
            ReadCreationTime(process) is { } started)
        {
            creation = started;
            return true;
        }

        creation = default;
        return false;
    }

    internal static string? ReadImagePath(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(512);
        int length = buffer.Capacity;
        if (QueryFullProcessImageName(process, 0, buffer, ref length))
            return buffer.ToString();

        if (Marshal.GetLastWin32Error() != 122)
            return null;

        buffer.EnsureCapacity(32768);
        length = buffer.Capacity;
        return QueryFullProcessImageName(process, 0, buffer, ref length) ? buffer.ToString() : null;
    }
}
