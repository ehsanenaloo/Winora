// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol.Definitions;

/// <summary>One entry of <c>revoked</c> (trust.md 6.6): versions of <see cref="Id"/> below <see cref="BelowVersion"/> can no longer be applied.</summary>
public sealed record RevokedDefinition(string Id, int BelowVersion, bool AllowRestore);

/// <summary>A parsed machine definition set (trust.md 6.2). Parsing does not verify any signature; callers verify the bytes first.</summary>
public sealed record DefinitionSet(
    string SetId, long Serial, int MinProtocol, int MinWorkerBuild,
    IReadOnlyList<RevokedDefinition> Revoked, IReadOnlyList<MachineSettingDefinition> Definitions)
{
    public MachineSettingDefinition? Find(string id) => Definitions.FirstOrDefault(d => d.Id == id);

    /// <summary>The revocation entry that blocks <paramref name="version"/> of <paramref name="id"/>, or null.</summary>
    public RevokedDefinition? RevocationOf(string id, int version) =>
        Revoked.FirstOrDefault(r => r.Id == id && version < r.BelowVersion);
}

/// <summary>Result of a strict parse: either a set or a reason, never both.</summary>
public sealed record DefinitionParseResult(DefinitionSet? Set, string? Problem)
{
    public bool Ok => Set is not null;
}

/// <summary>Limits of the restricted JSON profile and of the schema (trust.md 6.1, 7.1, 7.2).</summary>
public static class DefinitionLimits
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxDepth = 8;
    public const int MaxDefinitions = 256;
    public const int MaxRevoked = 64;
    public const int MaxChoices = 8;
    public const int MaxRestoreDomain = 16;
    public const int MaxRestoreCompatible = 16;
    public const int MaxManagedBy = 8;
    public const int MaxSourceRefs = 8;
    public const int MaxCommunitySources = 8;
    public const int MaxEditions = 4;
    public const int MaxKeyPathChars = 255;
    public const int MaxValueNameChars = 128;
    public const int MaxServiceNameChars = 64;
    public const int MaxStringValueChars = 512;
    public const int MinSupportedBuild = 22621;
    public const string Format = "winora.machine-definitions/1";
}
