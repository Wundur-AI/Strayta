using System.Runtime.InteropServices;

namespace Strayta.Editor;

/// <summary>
/// Files whose contents live only in cloud storage: on macOS, iCloud Drive with "Optimize Mac Storage" (the Desktop
/// and Documents folders included) leaves a placeholder that is downloaded on first read; on Windows, OneDrive's
/// "files on demand" do the same. Reading one can take many seconds, so the editor says so while it waits.
/// The system writes the download into place all at once, so there is no partial size to show progress from.
/// </summary>
internal static class CloudFiles
{
    /// <summary>True when the file exists but its contents still have to be downloaded before it can be read.</summary>
    public static bool NeedsDownload(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return (MacFlags(path) & SfDataless) != 0;
            if (OperatingSystem.IsWindows()) return ((int)File.GetAttributes(path) & (RecallOnDataAccess | RecallOnOpen)) != 0;
        }
        catch (Exception) { } // a check that fails just means no notice; opening reports real errors
        return false;
    }

    private const uint SfDataless = 0x40000000;      // sys/stat.h: contents not present locally
    private const int RecallOnOpen = 0x40000;         // FILE_ATTRIBUTE_RECALL_ON_OPEN
    private const int RecallOnDataAccess = 0x400000;  // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    // struct stat with 64-bit inodes (the only layout on arm64; "stat$INODE64" on x64): st_flags is at byte 116.
    private const int StatSize = 144, StFlagsOffset = 116;

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatArm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int StatX64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    private static uint MacFlags(string path)
    {
        var buffer = new byte[StatSize];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? StatArm64(path, buffer) : StatX64(path, buffer);
        return result == 0 ? BitConverter.ToUInt32(buffer, StFlagsOffset) : 0;
    }
}
