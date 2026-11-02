// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using Microsoft.Win32;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;
using Winora.Elevation.Protocol.Definitions;
using EW = Winora.Elevation.Windows;
namespace Winora.Infrastructure.Windows.Elevation;

/// <summary>
/// The client's READ-ONLY view of the anti-rollback floor: one <c>REG_BINARY</c> value <c>Floor</c> in <c>HKLM\SOFTWARE\Winora\Trust</c> (64-bit view).
/// Before the value is trusted, the key's effective permissions are checked: the owner and every principal with a write-class right must be SYSTEM, Administrators or
/// TrustedInstaller, otherwise the store is <c>Insecure</c> (<c>trust.store_insecure</c>). A malformed value is <c>Malformed</c>, never a floor of zero. Only the worker advances the
/// floor; <see cref="TryAdvance"/> here always refuses. Known limit: a registry symbolic link at the key is not detected.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryFloorReader : IAntiRollbackStore
{
    public const string TrustSubKey = @"SOFTWARE\Winora\Trust";
    public const string ValueName = "Floor";
    private readonly RegistryHive hive;
    private readonly string subKey;

    public RegistryFloorReader() : this(RegistryHive.LocalMachine, TrustSubKey) { }

    /// <summary>The hive and key are parameters so tests can point at their own sandbox; production uses the default constructor only.</summary>
    internal RegistryFloorReader(RegistryHive hive, string subKey) { this.hive = hive; this.subKey = subKey; }

    public FloorRead Read()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, false);
            if (key is null) return FloorRead.Absent;
            var descriptor = key.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group).GetSecurityDescriptorBinaryForm();
            if (!EW.SecurityDescriptorParser.TryParse(descriptor, out var info) || info is null || AssessSecurity(info).Count != 0) return FloorRead.Insecure;
            var data = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data is null) return FloorRead.Absent;
            if (key.GetValueKind(ValueName) != RegistryValueKind.Binary || data is not byte[] bytes) return FloorRead.Malformed;
            return FloorRecord.TryDecode(bytes, out var record) ? FloorRead.Of(record) : FloorRead.Malformed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException) { return FloorRead.Insecure; }
    }

    /// <summary>The findings that make the key unsafe to trust: an untrusted owner or a write-class right for any other principal.</summary>
    public static IReadOnlyList<EW.AclFinding> AssessSecurity(EW.SecurityDescriptorInfo descriptor) => EW.AclEvaluator.Evaluate(descriptor, EW.AclEvaluator.RegistryWriteMask);

    public bool TryAdvance(FloorRecord expected, FloorRecord next) => false;
}

/// <summary>
/// The verified definition set for the executor: the embedded bytes and signature are verified by <see cref="DefinitionSetVerifier"/> (signature before parse, schema, invariants),
/// then the floor is evaluated. Any failure gives a catalog code and no set. No set is embedded in any build yet (key custody is undecided), so the shipped loader returns
/// the empty verified set; a loader that finds bytes verifies them.
/// </summary>
public sealed class VerifiedDefinitionSource(DefinitionSetVerifier verifier, IAntiRollbackStore floor, Func<(byte[] Set, byte[] Signature)?> load) : IMachineDefinitionSource
{
    public DefinitionSetResult Get()
    {
        if (load() is not { } material) return new(new VerifiedDefinitionSet(0, DefinitionSetHash.Compute([]).ToHex(), [], []), null);
        var verified = verifier.Verify(material.Set, material.Signature);
        if (verified.Set is not { } set) return new(null, verified.Code ?? ErrorCode.DefinitionSignatureInvalid);
        var code = RollbackPolicy.Evaluate(floor, (ulong)set.Serial, verified.SetHash, out _);
        if (code is not null) return new(null, code);
        return new(new VerifiedDefinitionSet(set.Serial, verified.SetHash.ToHex(), set.Definitions, set.Revoked.Select(r => new RevokedEntry(r.Id, r.BelowVersion, r.AllowRestore)).ToList()), null);
    }
}
