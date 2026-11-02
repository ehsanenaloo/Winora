// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Windows.Worker;

/// <summary>The public command line of the worker, parsed and validated. Every value is public by design; none is a secret.</summary>
public sealed record WorkerArguments(
    byte Proto, Bytes16 Rendezvous, uint ClientProcessId, ulong ClientStartFileTime, string InitiatorSid, string InitiatorLogonSid, Bytes32 BatchDigest, byte Operations);

/// <summary>Why an argument list was refused. All of them end in exit code 4 and, when R could be read, the <c>arguments</c> early-failure reason.</summary>
public enum ArgumentError
{
    None, Empty, TooMany, TooLong, NotAscii, ResponseFile, UnknownOption, MissingValue, Duplicate, Missing, BadProto, BadRendezvous, BadClientPid, BadClientStart, BadSid, BadLogonSid, BadDigest, BadOps,
}

/// <summary>Outcome of the strict parser. <see cref="Rendezvous"/> is set whenever a well-formed <c>--rendezvous</c> value was seen, even if another argument is bad.</summary>
public sealed record ArgumentParseResult(WorkerArguments? Arguments, ArgumentError Error, Bytes16? Rendezvous)
{
    public bool IsSuccess => Arguments is not null;
    /// <summary>Exit code of this outcome: 0 is never returned here; the caller maps success itself. A failure is <see cref="WorkerExitCode.BadArguments"/>.</summary>
    public WorkerExitCode FailureExitCode => WorkerExitCode.BadArguments;
}

/// <summary>
/// Strict parser for <c>--proto 1 --rendezvous &lt;32 hex&gt; --client-pid &lt;dec&gt; --client-start &lt;dec&gt; --initiator-sid &lt;SID&gt; --initiator-logon-sid &lt;SID&gt;
/// --batch-digest &lt;64 hex&gt; --ops &lt;1..8&gt;</c>: the exact option set, each exactly once, in any order, as separate tokens
/// (<c>--name=value</c> is an unknown option), bounded lengths, ASCII only, strict decimal and lowercase-hex grammar. A response file
/// (<c>@file</c>), an unknown option, a duplicate, a missing option or an oversized token is refused. The parser reads the array it is given and
/// nothing else: no environment, no command-line API, no file.
/// </summary>
public static class WorkerArgumentParser
{
    public const int MaxTokens = 16;
    public const int MaxTokenLength = 128;

    public static ArgumentParseResult Parse(IReadOnlyList<string> args)
    {
        Bytes16? rendezvous = null;
        // Pre-scan only to recover R for the early-failure record when another argument is bad; it never relaxes the main rules.
        for (int i = 0; i + 1 < args.Count && i < MaxTokens; i++)
            if (args[i] == "--rendezvous" && TryHex16(args[i + 1], out var r)) { rendezvous = r; break; }

        if (args.Count == 0) return Fail(ArgumentError.Empty, rendezvous);
        if (args.Count > MaxTokens) return Fail(ArgumentError.TooMany, rendezvous);
        foreach (var a in args)
        {
            if (a is null || a.Length > MaxTokenLength) return Fail(ArgumentError.TooLong, rendezvous);
            if (a.Length > 0 && a[0] == '@') return Fail(ArgumentError.ResponseFile, rendezvous);
            foreach (var c in a) if (c is < ' ' or > '~') return Fail(ArgumentError.NotAscii, rendezvous);
        }
        if (args.Count % 2 != 0) return Fail(ArgumentError.MissingValue, rendezvous);

        string?[] values = new string?[Options.Length];
        for (int i = 0; i < args.Count; i += 2)
        {
            int slot = Array.IndexOf(Options, args[i]);
            if (slot < 0) return Fail(args[i].Length > 0 && args[i][0] == '@' ? ArgumentError.ResponseFile : ArgumentError.UnknownOption, rendezvous);
            if (values[slot] is not null) return Fail(ArgumentError.Duplicate, rendezvous);
            values[slot] = args[i + 1];
        }
        for (int i = 0; i < values.Length; i++) if (values[i] is null) return Fail(ArgumentError.Missing, rendezvous);

        if (values[0] != "1") return Fail(ArgumentError.BadProto, rendezvous);
        if (!TryHex16(values[1]!, out var rv)) return Fail(ArgumentError.BadRendezvous, rendezvous);
        if (!TryDecimal(values[2]!, uint.MaxValue, out var pid) || pid == 0) return Fail(ArgumentError.BadClientPid, rv);
        if (!TryDecimal(values[3]!, ulong.MaxValue, out var start) || start == 0) return Fail(ArgumentError.BadClientStart, rv);
        if (!SidText.IsValid(values[4]) || SidText.IsLogonSid(values[4])) return Fail(ArgumentError.BadSid, rv);
        if (!SidText.IsLogonSid(values[5])) return Fail(ArgumentError.BadLogonSid, rv);
        if (!TryHex32(values[6]!, out var digest)) return Fail(ArgumentError.BadDigest, rv);
        if (!TryDecimal(values[7]!, ElevationLimits.MaxBatchOperations, out var ops) || ops < 1) return Fail(ArgumentError.BadOps, rv);

        return new ArgumentParseResult(new WorkerArguments(ElevationLimits.ProtocolVersion, rv, (uint)pid, start, values[4]!, values[5]!, digest, (byte)ops), ArgumentError.None, rv);
    }

    private static readonly string[] Options = ["--proto", "--rendezvous", "--client-pid", "--client-start", "--initiator-sid", "--initiator-logon-sid", "--batch-digest", "--ops"];

    private static ArgumentParseResult Fail(ArgumentError error, Bytes16? rendezvous) => new(null, error, rendezvous);

    private static bool TryDecimal(string s, ulong max, out ulong value)
    {
        value = 0;
        if (s.Length is 0 or > 20 || (s.Length > 1 && s[0] == '0')) return false;
        foreach (var c in s) if (c is < '0' or > '9') return false;
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= max;
    }

    private static bool TryHex16(string s, out Bytes16 value)
    {
        value = default;
        if (!IsLowerHex(s, 32)) return false;
        value = new Bytes16(Convert.FromHexString(s));
        return true;
    }

    private static bool TryHex32(string s, out Bytes32 value)
    {
        value = default;
        if (!IsLowerHex(s, 64)) return false;
        value = new Bytes32(Convert.FromHexString(s));
        return true;
    }

    private static bool IsLowerHex(string s, int length)
    {
        if (s.Length != length) return false;
        foreach (var c in s) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}
