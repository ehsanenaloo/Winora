// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Winora.Infrastructure.Windows;

/// <summary>Read-only questions to Windows Installer about one product. Nothing is installed, changed or started.</summary>
[SupportedOSPlatform("windows")]
internal static class MsiProducts
{
    private const int MachineContext = 4;
    private const string ProductState = "State";
    private const string Installed = "5";

    /// <summary>True when Windows Installer reports the product as installed for all users of this computer (the machine context, not a user's own install).</summary>
    public static bool InstalledPerMachine(string productCode)
    {
        var size = 16u;
        var value = new StringBuilder((int)size);
        try { return MsiGetProductInfoEx(productCode, null, MachineContext, ProductState, value, ref size) == 0 && value.ToString() == Installed; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("msi.dll", EntryPoint = "MsiGetProductInfoExW", CharSet = CharSet.Unicode)]
    private static extern int MsiGetProductInfoEx(string productCode, string? userSid, int context, string property, StringBuilder value, ref uint size);
}
