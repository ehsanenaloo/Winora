// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Domain.Elevation;

public enum SelftestChoiceKind { Ping, Echo }

/// <summary>
/// The no-op command set: the definition <c>winora.selftest.noop</c> version 1 exists only in the VmTrust flavor
/// and touches no registry, service or file. Choices are <c>ping</c> and <c>echo.&lt;p&gt;</c> with <c>p</c> in <c>[a-z0-9]{1,32}</c>.
/// </summary>
public sealed class SelftestChoice
{
    public SelftestChoiceKind Kind { get; }
    /// <summary>The echo payload (empty for ping).</summary>
    public string Payload { get; }
    private SelftestChoice(SelftestChoiceKind kind, string payload) { Kind = kind; Payload = payload; }
    public string ChoiceId => Kind == SelftestChoiceKind.Ping ? SelftestNoop.PingChoice : SelftestNoop.EchoPrefix + Payload;

    public static SelftestChoice Ping { get; } = new(SelftestChoiceKind.Ping, "");
    public static SelftestChoice Echo(string payload) =>
        IsValidEchoPayload(payload) ? new(SelftestChoiceKind.Echo, payload) : throw new ArgumentException("echo payload is out of grammar", nameof(payload));

    public static bool TryParse(string? choiceId, out SelftestChoice choice)
    {
        choice = Ping;
        if (choiceId is null) return false;
        if (choiceId == SelftestNoop.PingChoice) return true;
        if (!choiceId.StartsWith(SelftestNoop.EchoPrefix, StringComparison.Ordinal)) return false;
        var payload = choiceId[SelftestNoop.EchoPrefix.Length..];
        if (!IsValidEchoPayload(payload)) return false;
        choice = new(SelftestChoiceKind.Echo, payload);
        return true;
    }

    private static bool IsValidEchoPayload(string? p)
    {
        if (p is null || p.Length is < 1 or > 32) return false;
        foreach (var c in p) if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9')) return false;
        return true;
    }
}

public static class SelftestNoop
{
    public const string DefinitionId = "winora.selftest.noop";
    public const uint DefinitionVersion = 1;
    public const string PingChoice = "ping";
    public const string EchoPrefix = "echo.";
    /// <summary>After-state payload of an applied ping.</summary>
    public const string PongPayload = "pong";
    /// <summary>Fixed ASCII definition-set strings whose SHA-256 is H_d before the signed-set layer exists (A.9).</summary>
    public const string VmTrustSetText = "winora-m4a-set:selftest-noop";
    public const string EmptySetText = "winora-m4a-set:empty";
    public static ReadOnlySpan<byte> VmTrustSetBytes => "winora-m4a-set:selftest-noop"u8;
    public static ReadOnlySpan<byte> EmptySetBytes => "winora-m4a-set:empty"u8;
}
