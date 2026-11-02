// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace Winora.Elevation.Windows;

public enum IdentityReadStatus { Ok, AccessDenied, NotFound, Unsupported, Failed }

public enum IntegrityLevel { Unknown, Untrusted, Low, Medium, MediumPlus, High, System, Protected }

public enum TokenElevationKind { Unknown = 0, Default = 1, Full = 2, Limited = 3 }

/// <summary>Facts read from one token (protocol.md 7.2 step 1, 14.1 step 1). Plain data.</summary>
public sealed record TokenFacts(
    string UserSid, string? LogonSid, uint SessionId, uint IntegrityRid, IntegrityLevel Integrity, TokenElevationKind ElevationType, bool IsElevated,
    string OwnerSid, bool IsImpersonationToken, uint ImpersonationLevel)
{
    /// <summary>The token's default owner differs from its user (for example an elevated administrator token whose owner is the Administrators group).</summary>
    public bool OwnerDiffersFromUser => !string.Equals(UserSid, OwnerSid, StringComparison.Ordinal);
}

/// <summary>Facts about a process other than its token (section 6.2 secondary checks, 7.2 step 3).</summary>
public sealed record ProcessFacts(uint ProcessId, string? ImagePath, ulong? StartFiletime, uint? SessionId);

/// <summary>Typed result: access denied and the other failures are statuses, never exceptions.</summary>
public sealed record IdentityReadResult(IdentityReadStatus Status, int Win32Error, string? FailedStep, TokenFacts? Token, ProcessFacts? Process)
{
    public bool IsOk => Status == IdentityReadStatus.Ok;

    /// <summary>Maps a Win32 error to a status (5 access denied; 87 and 1168 and 2 not found; 6 and others failed).</summary>
    public static IdentityReadStatus StatusFor(int win32Error) => win32Error switch
    {
        0 => IdentityReadStatus.Ok,
        5 => IdentityReadStatus.AccessDenied,
        87 or 1168 or 2 or 1008 => IdentityReadStatus.NotFound,
        _ => IdentityReadStatus.Failed,
    };

    internal static IdentityReadResult Fail(string step, int error) => new(StatusFor(error) == IdentityReadStatus.Ok ? IdentityReadStatus.Failed : StatusFor(error), error, step, null, null);
}

/// <summary>
/// Reads token and process facts. Tests call it only for the current process; the pipe-client case passes a process id or a token handle
/// obtained by the caller (after the impersonation of section 7.2 step 1, which this class never performs).
/// </summary>
public static class ProcessIdentityReader
{
    private const uint TokenQuery = 0x8, ProcessQueryLimitedInformation = 0x1000;
    private const int TokenUserClass = 1, TokenGroupsClass = 2, TokenOwnerClass = 4, TokenTypeClass = 8, TokenImpersonationLevelClass = 9,
        TokenSessionIdClass = 12, TokenElevationTypeClass = 18, TokenElevationClass = 20, TokenIntegrityLevelClass = 25;
    private const uint SeGroupLogonId = 0xC0000000;

    public static IdentityReadResult ReadCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return new IdentityReadResult(IdentityReadStatus.Unsupported, 0, "platform", null, null);
        return ReadProcessCore(Native.CurrentProcessPseudoHandle, (uint)Environment.ProcessId);
    }

    /// <summary>Opens the process with PROCESS_QUERY_LIMITED_INFORMATION and reads its token and process facts.</summary>
    public static IdentityReadResult ReadProcess(uint processId)
    {
        if (!OperatingSystem.IsWindows()) return new IdentityReadResult(IdentityReadStatus.Unsupported, 0, "platform", null, null);
        using var process = Native.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return IdentityReadResult.Fail("open_process", Marshal.GetLastPInvokeError());
        return ReadProcessCore(process.DangerousGetHandle(), processId);
    }

    /// <summary>Reads the facts of a process handle the caller opened (needs PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    public static IdentityReadResult ReadProcessHandle(SafeHandle process)
    {
        if (!OperatingSystem.IsWindows()) return new IdentityReadResult(IdentityReadStatus.Unsupported, 0, "platform", null, null);
        if (process.IsInvalid || process.IsClosed) return IdentityReadResult.Fail("handle", 6);
        uint pid = Native.GetProcessId(process.DangerousGetHandle());
        if (pid == 0) return IdentityReadResult.Fail("process_id", Marshal.GetLastPInvokeError());
        return ReadProcessCore(process.DangerousGetHandle(), pid);
    }

    /// <summary>Reads the facts of a token the caller holds (needs TOKEN_QUERY), for example the thread token while impersonating the pipe client.</summary>
    public static IdentityReadResult ReadToken(SafeHandle token)
    {
        if (!OperatingSystem.IsWindows()) return new IdentityReadResult(IdentityReadStatus.Unsupported, 0, "platform", null, null);
        if (token.IsInvalid || token.IsClosed) return IdentityReadResult.Fail("handle", 6);
        return ReadTokenCore(token, null);
    }

    /// <summary>True when the calling thread currently holds an impersonation token (section 14.1 step 1 requires false). Null when it could not be determined.</summary>
    public static bool? IsCurrentThreadImpersonating()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (Native.OpenThreadToken(Native.CurrentThreadPseudoHandle, TokenQuery, true, out var token))
        {
            token.Dispose();
            return true;
        }
        int err = Marshal.GetLastPInvokeError();
        return err == 1008 ? false : null;
    }

    private static IdentityReadResult ReadProcessCore(nint processHandle, uint pid)
    {
        if (!Native.OpenProcessToken(processHandle, TokenQuery, out var token)) return IdentityReadResult.Fail("open_token", Marshal.GetLastPInvokeError());
        using (token) return ReadTokenCore(token, ReadProcessFacts(processHandle, pid));
    }

    private static unsafe ProcessFacts ReadProcessFacts(nint processHandle, uint pid)
    {
        string? image = null;
        char[] buffer = new char[1024];
        fixed (char* p = buffer)
        {
            uint size = (uint)buffer.Length;
            if (Native.QueryFullProcessImageNameW(processHandle, 0, p, ref size)) image = new string(p, 0, (int)size);
        }
        ulong? start = Native.GetProcessTimes(processHandle, out var creation, out _, out _, out _) ? creation : null;
        uint? session = Native.ProcessIdToSessionId(pid, out var s) ? s : null;
        return new ProcessFacts(pid, image, start, session);
    }

    private static unsafe IdentityReadResult ReadTokenCore(SafeHandle token, ProcessFacts? process)
    {
        byte[] buf = new byte[512];
        int err;

        string? user = null, owner = null, logon = null;
        if (!Query(token, TokenUserClass, ref buf, out err)) return IdentityReadResult.Fail("token_user", err);
        fixed (byte* p = buf) user = SidAt(*(nint*)p);
        if (!Query(token, TokenOwnerClass, ref buf, out err)) return IdentityReadResult.Fail("token_owner", err);
        fixed (byte* p = buf) owner = SidAt(*(nint*)p);
        if (user is null || owner is null) return IdentityReadResult.Fail("sid", 1337);

        if (!Query(token, TokenGroupsClass, ref buf, out err)) return IdentityReadResult.Fail("token_groups", err);
        fixed (byte* p = buf)
        {
            uint count = *(uint*)p;
            byte* array = p + sizeof(nint);
            for (uint i = 0; i < count && i < 1024; i++)
            {
                byte* entry = array + i * 2 * sizeof(nint);
                uint attributes = *(uint*)(entry + sizeof(nint));
                if ((attributes & SeGroupLogonId) == SeGroupLogonId) { logon = SidAt(*(nint*)entry); break; }
            }
        }

        uint session;
        if (!QueryUInt(token, TokenSessionIdClass, out session, out err)) return IdentityReadResult.Fail("token_session", err);
        if (!Query(token, TokenIntegrityLevelClass, ref buf, out err)) return IdentityReadResult.Fail("token_integrity", err);
        uint rid;
        fixed (byte* p = buf)
        {
            byte* sid = (byte*)*(nint*)p;
            rid = sid is null || sid[1] == 0 ? 0 : *(uint*)(sid + 8 + 4 * (sid[1] - 1));
        }
        uint elevationType = 0, elevated = 0, tokenType = 0, impersonationLevel = 0;
        if (!QueryUInt(token, TokenElevationTypeClass, out elevationType, out err)) return IdentityReadResult.Fail("token_elevation_type", err);
        if (!QueryUInt(token, TokenElevationClass, out elevated, out err)) return IdentityReadResult.Fail("token_elevation", err);
        if (!QueryUInt(token, TokenTypeClass, out tokenType, out err)) return IdentityReadResult.Fail("token_type", err);
        if (tokenType == 2 && !QueryUInt(token, TokenImpersonationLevelClass, out impersonationLevel, out err)) return IdentityReadResult.Fail("token_impersonation_level", err);

        var facts = new TokenFacts(user, logon, session, rid, IntegrityFor(rid), elevationType is >= 1 and <= 3 ? (TokenElevationKind)elevationType : TokenElevationKind.Unknown,
            elevated != 0, owner, tokenType == 2, impersonationLevel);
        return new IdentityReadResult(IdentityReadStatus.Ok, 0, null, facts, process);
    }

    internal static IntegrityLevel IntegrityFor(uint rid) => rid switch
    {
        0 => IntegrityLevel.Untrusted,
        0x1000 => IntegrityLevel.Low,
        0x2000 => IntegrityLevel.Medium,
        0x2100 => IntegrityLevel.MediumPlus,
        0x3000 => IntegrityLevel.High,
        0x4000 => IntegrityLevel.System,
        0x5000 => IntegrityLevel.Protected,
        _ => IntegrityLevel.Unknown,
    };

    private static unsafe string? SidAt(nint pointer)
    {
        if (pointer == 0) return null;
        byte* sid = (byte*)pointer;
        int length = 8 + 4 * sid[1];
        return SidText.FromBinary(new ReadOnlySpan<byte>(sid, length), out _);
    }

    private static unsafe bool Query(SafeHandle token, int cls, ref byte[] buffer, out int error)
    {
        error = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint returned;
            bool ok;
            fixed (byte* p = buffer) ok = Native.GetTokenInformation(token, cls, p, (uint)buffer.Length, out returned);
            if (ok) return true;
            error = Marshal.GetLastPInvokeError();
            if (error != 122 || returned == 0 || returned > 65536) return false;
            buffer = new byte[returned];
        }
        return false;
    }

    private static unsafe bool QueryUInt(SafeHandle token, int cls, out uint value, out int error)
    {
        uint v = 0;
        bool ok = Native.GetTokenInformation(token, cls, (byte*)&v, sizeof(uint), out _);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        value = v;
        return ok;
    }
}
