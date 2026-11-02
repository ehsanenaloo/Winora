// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Winora.Domain;
namespace Winora.Application;

public static class ProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    public static DesiredProfile Import(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > ProfileLimits.MaximumBytes) throw new InvalidDataException(ErrorCode.ProfileSize);
        using (var document = JsonDocument.Parse(json)) RejectDuplicateProperties(document.RootElement);
        var profile = JsonSerializer.Deserialize<DesiredProfile>(json, Options) ?? throw new InvalidDataException(ErrorCode.ProfileInvalid);
        if (profile.SchemaVersion != 1 || profile.Settings is null || profile.Settings.Count > 100 ||
            profile.Settings.Any(s => s is null || s.FeatureId != "simulation.boolean") ||
            profile.Settings.Select(s => s.FeatureId).Distinct(StringComparer.Ordinal).Count() != profile.Settings.Count)
            throw new InvalidDataException(ErrorCode.ProfileInvalid);
        return profile;
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException(ErrorCode.ProfileDuplicateProperty);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
    public static string Export(DesiredProfile profile)
    {
        var json = JsonSerializer.Serialize(profile, Options);
        _ = Import(json);
        return json;
    }
}
public static class ConflictPlanner
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<FeatureDefinition> features)
    {
        var errors = new List<string>();
        var ids = features.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != features.Count) errors.Add(ErrorCode.PlanDuplicateFeature);
        foreach (var feature in features)
            foreach (var dependency in feature.Dependencies)
                if (!ids.Contains(dependency)) errors.Add(ErrorCode.PlanMissingDependency + ":" + feature.Id);
        for (var i = 0; i < features.Count; i++)
            for (var j = i + 1; j < features.Count; j++)
                if (features[i].Resource.Overlaps(features[j].Resource)) errors.Add(ErrorCode.PlanResourceConflict + ":" + features[i].Id + ":" + features[j].Id);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features) if (!Visit(feature)) { errors.Add(ErrorCode.PlanDependencyCycle); break; }
        return errors;
        bool Visit(FeatureDefinition feature)
        {
            if (visited.Contains(feature.Id)) return true;
            if (!visiting.Add(feature.Id)) return false;
            foreach (var dependency in feature.Dependencies)
            {
                var target = features.FirstOrDefault(f => f.Id == dependency);
                if (target is not null && !Visit(target)) return false;
            }
            visiting.Remove(feature.Id); visited.Add(feature.Id); return true;
        }
    }
}
