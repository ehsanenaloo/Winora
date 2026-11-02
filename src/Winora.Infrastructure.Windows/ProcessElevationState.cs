// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using System.Security.Principal;
using Winora.Application;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// Reads the token of this process and the folder the app runs from. The administrator features are allowed only when the token is elevated (a member of Administrators with the full token)
/// AND the app folder can be changed by administrators only: an elevated process that loads files from a folder a normal program can write to would run that program's code with full rights.
/// </summary>
public sealed class ProcessElevationState : IElevationState
{
    private static readonly Lazy<bool> Token = new(ReadToken);
    private static readonly Lazy<bool> Location = new(ReadLocation);

    public bool IsElevated => Token.Value && Location.Value;
    public bool UnsafeLocation => Token.Value && !Location.Value;

    /// <summary>The administrator features are allowed (token and folder).</summary>
    public static bool Current => Token.Value && Location.Value;

    /// <summary>The token alone. The shell restart uses it: a shell started from any elevated process would itself be elevated.</summary>
    public static bool TokenIsElevated => Token.Value;

    private static bool ReadToken()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return CheckToken(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    private static bool ReadLocation()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return AdminOnlyFolder.IsProtected(AppContext.BaseDirectory); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or InvalidOperationException or IOException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static bool CheckToken()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
