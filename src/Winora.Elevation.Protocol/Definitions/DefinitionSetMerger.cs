// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Elevation.Protocol.Definitions;

/// <summary>
/// Joins the area files of the embedded definition set into one set. Each file goes through the strict parser on its own (so every per-file limit holds), then the union
/// goes through the admission invariants and the cross-file checks. The caller passes the files in their stable order.
/// </summary>
public static class DefinitionSetMerger
{
    public const int MaxFiles = 16;

    public static DefinitionParseResult Merge(IReadOnlyList<byte[]> files)
    {
        if (files.Count is 0 or > MaxFiles) return new(null, "the number of definition files is out of range");
        var sets = new List<DefinitionSet>();
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Length is 0 or > DefinitionLimits.MaxBytes) return new(null, $"file {i}: size out of range");
            var parsed = DefinitionSetParser.Parse(files[i]);
            if (!parsed.Ok) return new(null, $"file {i}: {parsed.Problem}");
            sets.Add(parsed.Set!);
        }
        var revoked = sets.SelectMany(s => s.Revoked).DistinctBy(r => (r.Id, r.BelowVersion)).ToList();
        var merged = new DefinitionSet(sets[0].SetId, sets.Max(s => s.Serial), sets.Max(s => s.MinProtocol), sets.Max(s => s.MinWorkerBuild), revoked, sets.SelectMany(s => s.Definitions).ToList());
        var problems = DefinitionInvariants.Validate(merged).Concat(DefinitionInvariants.ValidateUniqueness(merged)).ToList();
        return problems.Count == 0 ? new(merged, null) : new(null, problems[0]);
    }
}
