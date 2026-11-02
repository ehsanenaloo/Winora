// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Elevation.Protocol.Definitions;

/// <summary>
/// Strict parser of a machine definition set. Every field is required except where a field is documented as nullable,
/// unknown fields are rejected, every collection is bounded. Admission rules (path policy, restore domain) are in <see cref="DefinitionInvariants"/>.
/// </summary>
public static class DefinitionSetParser
{
    public static DefinitionParseResult Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new DefinitionParseResult(ReadSet(StrictJson.Parse(bytes)), null);
        }
        catch (DefinitionFormatException ex) { return new DefinitionParseResult(null, ex.Message); }
    }

    private static DefinitionSet ReadSet(JNode root)
    {
        var o = new Fields(root, "$", ["format", "setId", "serial", "minProtocol", "minWorkerBuild", "revoked", "definitions"], []);
        if (o.Str("format") != DefinitionLimits.Format) throw new DefinitionFormatException("$.format: unknown format");
        var setId = o.Str("setId");
        if (!Grammar.IsSetId(setId)) throw new DefinitionFormatException("$.setId: out of grammar");
        var serial = o.Int("serial", 1, long.MaxValue);
        var minProtocol = (int)o.Int("minProtocol", 1, 255);
        var minBuild = (int)o.Int("minWorkerBuild", DefinitionLimits.MinSupportedBuild, 1_000_000);
        var revoked = o.Array("revoked", DefinitionLimits.MaxRevoked).Select((e, i) => ReadRevoked(e, $"$.revoked[{i}]")).ToList();
        var defs = o.Array("definitions", DefinitionLimits.MaxDefinitions).Select((e, i) => ReadDefinition(e, $"$.definitions[{i}]")).ToList();
        return new DefinitionSet(setId, serial, minProtocol, minBuild, revoked, defs);
    }

    private static RevokedDefinition ReadRevoked(JNode e, string path)
    {
        var o = new Fields(e, path, ["id", "belowVersion", "allowRestore"], []);
        var id = o.Str("id");
        if (!Grammar.IsDefinitionId(id)) throw new DefinitionFormatException($"{path}.id: out of grammar");
        return new RevokedDefinition(id, (int)o.Int("belowVersion", 1, int.MaxValue), o.Bool("allowRestore"));
    }

    private static MachineSettingDefinition ReadDefinition(JNode e, string path)
    {
        var o = new Fields(e, path,
            ["id", "version", "scope", "titleKey", "descriptionKey", "category", "risk", "recovery", "requiresRestart", "minBuild", "maxBuild", "target",
             "choices", "defaultChoiceId", "recommendedChoiceId", "restoreDomain", "restoreCompatible", "allowRestore", "managedBy", "sourceRefs", "reviewRef"],
            ["sourceClass", "communitySources", "editions"]);
        if (o.Str("scope") != "machine") throw new DefinitionFormatException($"{path}.scope: must be \"machine\"");
        var (kind, registry, service, display) = ReadTarget(o.Object("target"), path + ".target");
        var choices = o.Array("choices", DefinitionLimits.MaxChoices).Select((c, i) => ReadChoice(c, $"{path}.choices[{i}]")).ToList();
        var domain = o.Array("restoreDomain", DefinitionLimits.MaxRestoreDomain).Select((v, i) => ReadValue(v, $"{path}.restoreDomain[{i}]")).ToList();
        var compat = o.Array("restoreCompatible", DefinitionLimits.MaxRestoreCompatible).Select((v, i) => (int)Fields.AsInt(v, $"{path}.restoreCompatible[{i}]", 1, int.MaxValue)).ToList();
        var managed = o.Array("managedBy", DefinitionLimits.MaxManagedBy).Select((m, i) => ReadManagedBy(m, $"{path}.managedBy[{i}]")).ToList();
        var sources = o.Array("sourceRefs", DefinitionLimits.MaxSourceRefs).Select((s, i) => Fields.AsStr(s, $"{path}.sourceRefs[{i}]")).ToList();
        var maxBuild = o.NullableInt("maxBuild", 1, 1_000_000);
        var sourceClass = o.Has("sourceClass") ? o.Enum<SourceClass>("sourceClass") : SourceClass.A;
        var community = o.Has("communitySources") ? o.Array("communitySources", DefinitionLimits.MaxCommunitySources).Select((x, i) => Fields.AsStr(x, $"{path}.communitySources[{i}]")).ToList() : [];
        var editions = o.Has("editions") ? o.Array("editions", DefinitionLimits.MaxEditions).Select((x, i) => Fields.AsStr(x, $"{path}.editions[{i}]")).ToList() : [];
        return new MachineSettingDefinition(
            o.Str("id"), (int)o.Int("version", 1, int.MaxValue), o.Str("titleKey"), o.Str("descriptionKey"),
            o.Enum<SettingCategory>("category"), o.Enum<RiskLevel>("risk"), o.Enum<MachineRecovery>("recovery"), o.Bool("requiresRestart"),
            (int)o.Int("minBuild", 1, 1_000_000), maxBuild is null ? null : (int)maxBuild.Value,
            kind, registry, service, choices, o.Str("defaultChoiceId"), o.NullableStr("recommendedChoiceId"),
            domain, compat, o.Bool("allowRestore"), managed, sources, o.Has("reviewRef") ? o.Str("reviewRef") : "") { SourceClass = sourceClass, CommunitySources = community, Editions = editions, DisplayDriver = display };
    }

    private static (MachineTargetKind, MachineRegistryTarget?, MachineServiceTarget?, MachineDisplayDriverTarget?) ReadTarget(JNode e, string path)
    {
        if (e.Kind != JKind.Object || !e.TryGet("kind", out var k) || k.Kind != JKind.String)
            throw new DefinitionFormatException($"{path}.kind: missing");
        switch (k.Text)
        {
            case "registry":
            {
                var o = new Fields(e, path, ["kind", "subKey", "valueName", "view"], ["bits", "allowPercent"]);
                MachineBitField? bits = null;
                if (o.Has("bits"))
                {
                    var b = new Fields(o.Object("bits"), path + ".bits", ["mask", "storage"], []);
                    bits = new MachineBitField((uint)b.Int("mask", 1, uint.MaxValue), b.Enum<BitStorage>("storage"));
                }
                var allowPercent = o.Has("allowPercent") && o.Bool("allowPercent");
                return (MachineTargetKind.RegistryValue, new MachineRegistryTarget(o.Str("subKey"), o.Str("valueName"), o.Enum<RegistryViewKind>("view")) { Bits = bits, AllowPercentInText = allowPercent }, null, null);
            }
            case "service":
            {
                var o = new Fields(e, path, ["kind", "serviceName"], []);
                return (MachineTargetKind.ServiceStartType, null, new MachineServiceTarget(o.Str("serviceName")), null);
            }
            case "displayDriver":
            {
                var o = new Fields(e, path, ["kind", "valueName", "vendorId"], []);
                return (MachineTargetKind.DisplayDriverValue, null, null, new MachineDisplayDriverTarget(o.Str("valueName"), o.Str("vendorId")));
            }
            default: throw new DefinitionFormatException($"{path}.kind: must be \"registry\", \"service\" or \"displayDriver\"");
        }
    }

    private static MachineChoice ReadChoice(JNode e, string path)
    {
        var o = new Fields(e, path, ["id", "labelKey"], ["value", "start"]);
        var hasValue = o.Has("value"); var hasStart = o.Has("start");
        if (hasValue == hasStart) throw new DefinitionFormatException($"{path}: exactly one of value and start");
        return new MachineChoice(o.Str("id"), o.Str("labelKey"), hasValue ? ReadValue(o.Object("value"), path + ".value") : null,
            hasStart ? o.Enum<ServiceStartKind>("start") : null);
    }

    private static MachineValue ReadValue(JNode e, string path)
    {
        if (e.Kind != JKind.Object || !e.TryGet("kind", out var k) || k.Kind != JKind.String)
            throw new DefinitionFormatException($"{path}.kind: missing");
        try
        {
            switch (k.Text)
            {
                case "absent": _ = new Fields(e, path, ["kind"], []); return MachineValue.Absent;
                case "dword": return MachineValue.DWord((uint)new Fields(e, path, ["kind", "number"], []).Int("number", 0, uint.MaxValue));
                case "qword": return MachineValue.QWord((ulong)new Fields(e, path, ["kind", "number"], []).Int("number", 0, long.MaxValue));
                case "string": return MachineValue.String(new Fields(e, path, ["kind", "text"], []).Str("text"));
                default: throw new DefinitionFormatException($"{path}.kind: unknown value kind");
            }
        }
        catch (ArgumentException ex) { throw new DefinitionFormatException($"{path}: {ex.Message}"); }
    }

    private static ManagedByChannel ReadManagedBy(JNode e, string path)
    {
        var o = new Fields(e, path, ["kind", "location", "valueName"], []);
        return new ManagedByChannel(o.Enum<ManagedByKind>("kind"), o.Str("location"), o.NullableStr("valueName"));
    }

    /// <summary>Required and optional field access over one JSON object; rejects unknown and missing fields.</summary>
    private readonly struct Fields
    {
        private readonly JNode element;
        private readonly string path;

        public Fields(JNode e, string path, string[] required, string[] optional)
        {
            if (e.Kind != JKind.Object) throw new DefinitionFormatException($"{path}: expected an object");
            element = e; this.path = path;
            foreach (var m in e.Members)
                if (System.Array.IndexOf(required, m.Key) < 0 && System.Array.IndexOf(optional, m.Key) < 0) throw new DefinitionFormatException($"{path}: unknown field {m.Key}");
            foreach (var r in required)
                if (!e.TryGet(r, out _)) throw new DefinitionFormatException($"{path}: missing field {r}");
        }

        public bool Has(string name) => element.TryGet(name, out _);
        private JNode Get(string name) => element.TryGet(name, out var v) ? v : throw new DefinitionFormatException($"{path}: missing field {name}");
        public string Str(string name) => AsStr(Get(name), $"{path}.{name}");
        public string? NullableStr(string name) => Get(name).Kind == JKind.Null ? null : Str(name);
        public long Int(string name, long min, long max) => AsInt(Get(name), $"{path}.{name}", min, max);
        public long? NullableInt(string name, long min, long max) => Get(name).Kind == JKind.Null ? null : Int(name, min, max);

        public bool Bool(string name) => Get(name).Kind switch
        {
            JKind.True => true,
            JKind.False => false,
            _ => throw new DefinitionFormatException($"{path}.{name}: expected a boolean"),
        };

        public JNode Object(string name) => Get(name);

        public IEnumerable<JNode> Array(string name, int max)
        {
            var a = Get(name);
            if (a.Kind != JKind.Array) throw new DefinitionFormatException($"{path}.{name}: expected an array");
            if (a.Items.Count > max) throw new DefinitionFormatException($"{path}.{name}: more than {max} items");
            return a.Items;
        }

        public T Enum<T>(string name) where T : struct, System.Enum
        {
            var text = Str(name);
            if (!System.Enum.TryParse<T>(text, false, out var v) || !System.Enum.IsDefined(v) || v.ToString() != text)
                throw new DefinitionFormatException($"{path}.{name}: unknown value");
            return v;
        }

        public static string AsStr(JNode e, string at) =>
            e.Kind == JKind.String ? e.Text! : throw new DefinitionFormatException($"{at}: expected a string");

        public static long AsInt(JNode e, string at, long min, long max)
        {
            if (e.Kind != JKind.Integer) throw new DefinitionFormatException($"{at}: expected an integer");
            var v = e.Number;
            return v < min || v > max ? throw new DefinitionFormatException($"{at}: out of range") : v;
        }
    }
}
