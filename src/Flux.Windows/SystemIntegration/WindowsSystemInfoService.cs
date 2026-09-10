using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Flux.Core;

namespace Flux.Windows.SystemIntegration;

public sealed class WindowsSystemInfoService : ISystemInfoService
{
    public Task<SystemSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var first = ReadSystemTimes();
        await Task.Delay(250, cancellationToken);
        var second = ReadSystemTimes();
        var idle = second.Idle - first.Idle;
        var kernel = second.Kernel - first.Kernel;
        var user = second.User - first.User;
        var total = kernel + user;
        var cpu = total == 0 ? 0 : Math.Clamp((total - idle) * 100d / total, 0d, 100d);

        var memory = new MemoryStatusEx();
        GlobalMemoryStatusEx(memory);

        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(drive => new DriveSnapshot(drive.Name, drive.TotalSize, drive.AvailableFreeSpace))
            .ToArray();

        return new SystemSnapshot(
            cpu,
            memory.TotalPhysical,
            memory.AvailablePhysical,
            drives,
            $"Windows {Environment.OSVersion.Version}",
            NetworkInterface.GetIsNetworkAvailable(),
            GetBatteryStatus());
    }, cancellationToken);

    private static string? GetBatteryStatus()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryFlag == 128)
        {
            return null;
        }

        var charge = status.BatteryLifePercent == 255 ? "unknown" : $"{status.BatteryLifePercent}%";
        var power = status.ACLineStatus == 1 ? "plugged in" : "on battery";
        return $"{charge}, {power}";
    }

    private static (ulong Idle, ulong Kernel, ulong User) ReadSystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return default;
        }

        return (ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}

