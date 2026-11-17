using System.Diagnostics;
using System.Runtime.InteropServices;
using Employee.Core;

namespace Employee.Infrastructure;
public sealed record ResourceSnapshot(long TotalMemoryMb, long AvailableMemoryMb, long ApplicationMemoryMb)
{
    public override string ToString() => $"RAM available {AvailableMemoryMb:N0} / {TotalMemoryMb:N0} MB · manager {ApplicationMemoryMb:N0} MB";
}

public static class ResourceMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static ResourceSnapshot? Snapshot()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var state = new MemoryStatus
        {
            Length = (uint)Marshal.SizeOf<MemoryStatus>()
        };
        if (!GlobalMemoryStatusEx(ref state))
            return null;
        using var process = Process.GetCurrentProcess();
        return new((long)state.TotalPhysical / 1048576, (long)state.AvailablePhysical / 1048576, process.WorkingSet64 / 1048576);
    }

    public static int EffectiveSlots(ManagerSettings settings)
    {
        var configured = Math.Clamp(settings.Automation.MaxParallelAgents, 1, 4);
        if (!settings.AdaptiveParallelism || Snapshot()is not { } snapshot)
            return configured;
        var reserve = Math.Clamp(settings.MinimumFreeMemoryMb, 512, 32768);
        if (snapshot.AvailableMemoryMb < reserve)
            return 0;
        return Math.Min(configured, (int)Math.Clamp((snapshot.AvailableMemoryMb - reserve) / 1536 + 1, 1, 4));
    }
}
