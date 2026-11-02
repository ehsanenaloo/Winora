// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
namespace Winora.Instance;

/// <summary>Name, access rules and identity checks of the hand-over pipe.</summary>
public static class InstanceChannel
{
    /// <summary>Read data, write data, read attributes, read control and synchronize: no right to create instances or to change attributes.</summary>
    public const uint ClientRights = 0x00120083;

    /// <summary>The pipe name of one user in one session. The SID is hashed so the name carries no account data.</summary>
    public static string PipeName(string userSid, int sessionId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)), 0, 8);
        return $"Winora.Instance.v1.{sessionId}.{hash}";
    }

    /// <summary>The name for the current process: its token user and its session.</summary>
    public static string CurrentPipeName() => PipeName(CurrentUserSid(), Process.GetCurrentProcess().SessionId);

    public static string CurrentUserSid() => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The process token has no user.");

    /// <summary>Protected list: network clients denied, the user allowed the rights above, nobody else. No integrity label (a medium integrity client reaches a high integrity server).</summary>
    public static string Sddl(string userSid) => $"D:P(D;;FA;;;NU)(A;;0x{ClientRights:X8};;;{userSid})";

    /// <summary>True when the process with this id runs the executable at <paramref name="expectedPath"/>. False for any other image or when the image cannot be read.</summary>
    public static bool ProcessRunsImage(int processId, string expectedPath)
    {
        using var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle.IsInvalid) return false;
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        if (!Native.QueryFullProcessImageName(handle, 0, buffer, ref size)) return false;
        var actual = new string(buffer, 0, (int)size);
        return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
    }
}
