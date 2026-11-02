// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Winora.Infrastructure.Windows;

/// <summary>
/// Where an elevated host keeps the operation journal (settings, machine rows, preferences; ISS-2713): one database per account in the admin-only journals folder,
/// <c>%ProgramData%\Winora\journals\operations-&lt;SID&gt;.db</c>. The account is the user of the token, so it matches the HKCU that the setting rows describe.
/// A process that is not elevated keeps the old file in <c>%LocalAppData%\Winora</c>: it acts with the user's own rights, so a row a program of the user wrote gives it nothing more.
/// </summary>
public static class ProtectedJournal
{
    public const string LegacyFileName = "simulation.db";

    /// <summary>The old, user-writable journal: <c>%LocalAppData%\Winora\simulation.db</c>.</summary>
    public static string LegacyPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winora", LegacyFileName);

    [SupportedOSPlatform("windows")]
    public static string FileName(SecurityIdentifier user) => "operations-" + user.Value + ".db";

    /// <summary>The file name of a journal whose records describe the current user's own settings (their HKCU): <c>&lt;name&gt;-&lt;SID&gt;.jsonl</c>. Another account never reads or undoes these records.</summary>
    [SupportedOSPlatform("windows")]
    public static string UserFileName(string name, SecurityIdentifier user) => name + "-" + user.Value + ".jsonl";

    /// <summary>The per-account journal file of the token user in the admin-only journals folder.</summary>
    [SupportedOSPlatform("windows")]
    public static string UserPathFor(string name, string? folder = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The token has no user.");
        return Path.Combine(AdminOnlyFolder.Ensure(folder ?? AdminOnlyFolder.DefaultPath()), UserFileName(name, user));
    }

    /// <summary>The protected journal of the token user. Creates and checks the journals folder; throws <see cref="AdminOnlyFolderException"/> when it is refused.</summary>
    /// <param name="folder">The journals folder. Production passes none (<see cref="AdminOnlyFolder.DefaultPath"/>).</param>
    [SupportedOSPlatform("windows")]
    public static string PathFor(string? folder = null, IEnumerable<SecurityIdentifier>? extraTrusted = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The token has no user.");
        return Path.Combine(AdminOnlyFolder.Ensure(folder ?? AdminOnlyFolder.DefaultPath(), extraTrusted), FileName(user));
    }
}
