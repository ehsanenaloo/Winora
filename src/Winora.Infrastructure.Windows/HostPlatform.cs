// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
namespace Winora.Infrastructure.Windows;

/// <summary>Read-only host information, not a promise of feature compatibility.</summary>
public sealed record HostPlatform(bool IsWindows, string OsDescription, Architecture Architecture)
{
    public static HostPlatform Discover() => new(OperatingSystem.IsWindows(),
        RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture);
}
