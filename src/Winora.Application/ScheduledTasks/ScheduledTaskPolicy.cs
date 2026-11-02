// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using System.Text.Json;

namespace Winora.Application;

public enum TaskPolicyClass
{
    /// <summary>Any task outside the Microsoft folders.</summary>
    Other,
    /// <summary>A task under \Microsoft\ outside \Microsoft\Windows\, or a task of another folder that names Microsoft as its author.</summary>
    MicrosoftOther,
    /// <summary>A task under \Microsoft\Windows\ that is on neither list. Fail-closed: it cannot be disabled, stopped or deleted.</summary>
    WindowsUnlisted,
    /// <summary>A Windows task on the reviewed list of optional telemetry and consent tasks.</summary>
    KnownOptional,
    /// <summary>A task of the reviewed protected set: updates, security, time, system integrity.</summary>
    Protected,
}

public enum TaskRisk { None, Recommended, ReducesDiagnostics }

/// <summary><see cref="WarningKey"/> names a plain warning text for the task (resource key TaskWarn_{WarningKey}); <see cref="Reason"/> is the reason key of a protected task.</summary>
public sealed record TaskPolicyInfo(TaskPolicyClass Class, TaskRisk Risk, string? WarningKey, string? Reason);

public sealed record ProtectedTaskRule(string Prefix, string Reason, string SourceClass, string Source);

public sealed record KnownOptionalTask(string Path, TaskRisk Risk, string? WarningKey, string SourceClass, string Source);

/// <summary>
/// Which tasks Winora may disable, stop or delete. The two reviewed lists come from the embedded data file <c>definitions/tasks/ScheduledTaskPolicy.v1.json</c> (every entry has a source note).
/// A task under \Microsoft\Windows\ may be changed only when it is on the known-optional list, so a task that is on no list is never touched. Enabling and running a task are always allowed.
/// Nothing is ever deleted when it lives under \Microsoft\ or names Microsoft as its author.
/// </summary>
public sealed class ScheduledTaskPolicy
{
    public const string WindowsRoot = @"\Microsoft\Windows\";
    public const string MicrosoftRoot = @"\Microsoft\";
    private const string ResourceName = "Winora.ScheduledTaskPolicy.v1.json";

    private static readonly Lazy<ScheduledTaskPolicy> DefaultPolicy = new(LoadEmbedded);

    private readonly IReadOnlyList<ProtectedTaskRule> protectedRules;
    private readonly Dictionary<string, KnownOptionalTask> optional;

    public ScheduledTaskPolicy(IReadOnlyList<ProtectedTaskRule> protectedRules, IReadOnlyList<KnownOptionalTask> knownOptional)
    {
        this.protectedRules = protectedRules;
        optional = new Dictionary<string, KnownOptionalTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in protectedRules)
        {
            if (!rule.Prefix.StartsWith(MicrosoftRoot, StringComparison.OrdinalIgnoreCase) || !rule.Prefix.EndsWith('\\')) throw new FormatException("Protected rule is not a folder prefix: " + rule.Prefix);
            RequireSource(rule.SourceClass, rule.Source, rule.Prefix);
        }
        foreach (var item in knownOptional)
        {
            if (!item.Path.StartsWith(WindowsRoot, StringComparison.OrdinalIgnoreCase) || item.Path.EndsWith('\\')) throw new FormatException("Optional task is not a Windows task path: " + item.Path);
            RequireSource(item.SourceClass, item.Source, item.Path);
            if (protectedRules.Any(r => item.Path.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase))) throw new FormatException("Optional task is also protected: " + item.Path);
            if (!optional.TryAdd(item.Path, item)) throw new FormatException("Optional task is listed twice: " + item.Path);
        }
    }

    public static ScheduledTaskPolicy Default => DefaultPolicy.Value;

    public IReadOnlyList<ProtectedTaskRule> Protected => protectedRules;
    public IReadOnlyCollection<KnownOptionalTask> KnownOptional => optional.Values;

    private static void RequireSource(string sourceClass, string source, string entry)
    {
        if (sourceClass is not ("observed" or "microsoft")) throw new FormatException("Unknown source class for " + entry);
        if (string.IsNullOrWhiteSpace(source)) throw new FormatException("Missing source note for " + entry);
    }

    public static ScheduledTaskPolicy Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "winora.task-policy/1") throw new FormatException("Unknown task policy format.");
        var rules = root.GetProperty("protected").EnumerateArray()
            .Select(e => new ProtectedTaskRule(Folder(e.GetProperty("pattern").GetString()!), e.GetProperty("reason").GetString()!, e.GetProperty("sourceClass").GetString()!, e.GetProperty("source").GetString()!)).ToList();
        var optionalTasks = root.GetProperty("knownOptional").EnumerateArray()
            .Select(e => new KnownOptionalTask(e.GetProperty("path").GetString()!, ParseRisk(e.GetProperty("risk").GetString()), e.TryGetProperty("warning", out var w) ? w.GetString() : null,
                e.GetProperty("sourceClass").GetString()!, e.GetProperty("source").GetString()!)).ToList();
        return new ScheduledTaskPolicy(rules, optionalTasks);
    }

    private static string Folder(string pattern) => pattern.EndsWith(@"\*", StringComparison.Ordinal) ? pattern[..^1] : throw new FormatException("A protected pattern must end with a backslash and a star: " + pattern);

    private static TaskRisk ParseRisk(string? value) => value switch
    {
        "recommended" => TaskRisk.Recommended,
        "reducesDiagnostics" => TaskRisk.ReducesDiagnostics,
        _ => throw new FormatException("Unknown risk class."),
    };

    private static ScheduledTaskPolicy LoadEmbedded()
    {
        using var stream = typeof(ScheduledTaskPolicy).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("The task policy is not embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public TaskPolicyInfo Classify(string path, string? author)
    {
        var protectedRule = protectedRules.FirstOrDefault(r => path.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase));
        if (protectedRule is not null) return new(TaskPolicyClass.Protected, TaskRisk.None, null, protectedRule.Reason);
        if (optional.TryGetValue(path, out var known)) return new(TaskPolicyClass.KnownOptional, known.Risk, known.WarningKey, null);
        if (path.StartsWith(WindowsRoot, StringComparison.OrdinalIgnoreCase)) return new(TaskPolicyClass.WindowsUnlisted, TaskRisk.None, null, null);
        if (IsMicrosoft(path, author)) return new(TaskPolicyClass.MicrosoftOther, TaskRisk.None, null, null);
        return new(TaskPolicyClass.Other, TaskRisk.None, null, null);
    }

    public static bool IsMicrosoft(string path, string? author) =>
        path.StartsWith(MicrosoftRoot, StringComparison.OrdinalIgnoreCase) || (author is not null && author.TrimStart().StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase));

    /// <summary>The refusal code for a change, or null when the policy allows it.</summary>
    public static string? Refusal(ScheduledTaskChangeKind kind, TaskPolicyInfo info)
    {
        switch (kind)
        {
            case ScheduledTaskChangeKind.Disable:
            case ScheduledTaskChangeKind.Stop:
                return info.Class switch
                {
                    TaskPolicyClass.Protected => ScheduledTaskCode.Protected,
                    TaskPolicyClass.WindowsUnlisted => ScheduledTaskCode.NotOptional,
                    _ => null,
                };
            case ScheduledTaskChangeKind.Delete:
                return info.Class switch
                {
                    TaskPolicyClass.Protected => ScheduledTaskCode.Protected,
                    TaskPolicyClass.Other => null,
                    _ => ScheduledTaskCode.MicrosoftDelete,
                };
            default:
                return null;
        }
    }
}
