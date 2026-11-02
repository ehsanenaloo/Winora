// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Xml.Linq;
using Winora.Application;
using Winora.Domain;

namespace Winora.Infrastructure.Windows.Performance;

/// <summary>Pure parsing for the performance check, tested with fixed input.</summary>
public static class PerformanceParsers
{
    /// <summary>The boot duration in seconds from the XML of event 100 of the Diagnostics-Performance log (stored in milliseconds). Null for anything else.</summary>
    public static double? BootSeconds(string? eventXml)
    {
        if (string.IsNullOrWhiteSpace(eventXml)) return null;
        try
        {
            var data = XDocument.Parse(eventXml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Data" && (string?)e.Attribute("Name") == "BootTime");
            if (data is null || !double.TryParse(data.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)) return null;
            return ms is > 0 and < 3_600_000 ? Math.Round(ms / 1000d, 1) : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>Processor load in percent between two samples of the system times (idle, kernel including idle, user), or null when no time passed or a counter went back.</summary>
    public static double? CpuPercent((ulong Idle, ulong Kernel, ulong User) a, (ulong Idle, ulong Kernel, ulong User) b)
    {
        if (b.Kernel < a.Kernel || b.User < a.User || b.Idle < a.Idle) return null;
        var total = (b.Kernel - a.Kernel) + (b.User - a.User);
        if (total == 0) return null;
        return Math.Clamp(100d * (1d - (double)(b.Idle - a.Idle) / total), 0, 100);
    }
}

/// <summary>
/// The read-only probes of the performance check (decision DEC-IW-231): GlobalMemoryStatusEx, the drive functions of the .NET library, GetSystemTimes for the processor load and one
/// read of the Diagnostics-Performance event log (wevtapi) for the boot time. No WMI, no child process, no write. A probe that fails returns null (the log needs rights some accounts lack).
/// </summary>
public sealed class WindowsPerformanceProbe : IPerformanceProbe
{
    private const string BootLog = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public async Task<PerformanceReadings> ReadAsync(IProgress<string>? stage, CancellationToken token)
    {
        stage?.Report(MetricIds.Boot);
        var boot = await Task.Run(ReadBootSeconds, token);
        token.ThrowIfCancellationRequested();
        stage?.Report(MetricIds.Memory);
        var memory = ReadMemoryPercent();
        stage?.Report(MetricIds.Disk);
        var disk = ReadSystemDriveFreePercent();
        stage?.Report(MetricIds.Cpu);
        var cpu = await ReadCpuPercentAsync(token);
        return new PerformanceReadings(boot, null, memory, disk, cpu);
    }

    /// <summary>Reads the newest event 100 of the boot log through the documented Windows Event Log functions (wevtapi). Returns null when the log cannot be read.</summary>
    private static double? ReadBootSeconds()
    {
        IntPtr query = IntPtr.Zero, handle = IntPtr.Zero;
        try
        {
            query = EvtQuery(IntPtr.Zero, BootLog, "*[System[(EventID=100)]]", EvtQueryChannelPath | EvtQueryReverseDirection);
            if (query == IntPtr.Zero) return null;
            var handles = new IntPtr[1];
            if (!EvtNext(query, 1, handles, 1000, 0, out var returned) || returned == 0) return null;
            handle = handles[0];
            EvtRender(IntPtr.Zero, handle, EvtRenderEventXml, 0, IntPtr.Zero, out var used, out _);
            if (used == 0 || used > 1_000_000) return null;
            var buffer = Marshal.AllocHGlobal((int)used);
            try
            {
                if (!EvtRender(IntPtr.Zero, handle, EvtRenderEventXml, used, buffer, out _, out _)) return null;
                return PerformanceParsers.BootSeconds(Marshal.PtrToStringUni(buffer));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException or OverflowException) { return null; }
        finally
        {
            if (handle != IntPtr.Zero) EvtClose(handle);
            if (query != IntPtr.Zero) EvtClose(query);
        }
    }

    private static double? ReadMemoryPercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) && status.TotalPhys > 0 ? status.MemoryLoad : null;
    }

    private static double? ReadSystemDriveFreePercent()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return drive.IsReady && drive.TotalSize > 0 ? 100d * drive.AvailableFreeSpace / drive.TotalSize : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Three samples of about 700 ms; the result is their mean, so a single spike does not decide it.</summary>
    private static async Task<double?> ReadCpuPercentAsync(CancellationToken token)
    {
        var values = new List<double>();
        if (!Times(out var previous)) return null;
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(700, token);
            if (!Times(out var next)) return null;
            if (PerformanceParsers.CpuPercent(previous, next) is { } load) values.Add(load);
            previous = next;
        }
        return values.Count == 0 ? null : values.Average();
    }

    private static bool Times(out (ulong Idle, ulong Kernel, ulong User) times)
    {
        times = default;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return false;
        times = (ToUlong(idle), ToUlong(kernel), ToUlong(user));
        return true;
    }

    private static ulong ToUlong(FILETIME t) => ((ulong)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    private const uint EvtQueryChannelPath = 0x1, EvtQueryReverseDirection = 0x200, EvtRenderEventXml = 1;

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtQuery(IntPtr session, string path, string query, uint flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(IntPtr resultSet, uint size, [Out] IntPtr[] events, uint timeout, uint flags, out uint returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(IntPtr context, IntPtr fragment, uint flags, uint bufferSize, IntPtr buffer, out uint bufferUsed, out uint propertyCount);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}
