// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Application;
using Winora.Domain;
using static Winora.Cli.CliText;
namespace Winora.Cli;

internal enum DiscoverKind { Startup, Tasks, Services, Apps, All }
internal sealed record DiscoverOptions(DiscoverKind Kind, bool Json, string? Filter, int? Limit);

// JSON item shapes (schemaVersion 1). Property names are camelCase on the wire; enum values are their stable Domain names.
internal sealed record StartupJson(string Kind, string Id, string Name, string Command, StartupSource Source, InstallationScope Scope, StartupApproval Approval, string? Publisher);
internal sealed record TaskJson(string Kind, string Id, string Path, string Name, TaskRunState State, bool Hidden, string? Author, DateTimeOffset? LastRunUtc, DateTimeOffset? NextRunUtc, bool RunsAtLogonOrBoot, string? Command, bool IsVendorMicrosoft);
internal sealed record ServiceJson(string Kind, string Id, string DisplayName, ServiceStartMode StartMode, ServiceRunState RunState, ServiceKind ServiceType, string? Account, string? ImagePath, string? Description);
internal sealed record AppJson(string Kind, string Id, string DisplayName, string? Version, string? Publisher, InstallationKind InstallationKind, InstallationScope Scope, RegistryViewKind? RegistryView, string? InstallLocation, DateOnly? InstalledOn, long? EstimatedSizeKb, bool IsSystemComponent, string Provenance);
internal sealed record IssueJson(string Source, string Code, string Category, string? Detail);

/// <summary>Read-only `winora discover` command: argument parsing, collection through <see cref="DiscoveryService"/>, text and JSON rendering.</summary>
internal static class DiscoverCommand
{
    public const int SchemaVersion = 1;
    public const int MaxLimit = 100_000;
    private const int MaxCell = 40;
    private const int MaxDetail = 200;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record Row(string Kind, string SortKey, string StableId, string Search, string[] Cells, object Json);
    private sealed record Section(string Kind, string TitleKey, string[] HeaderKeys, IReadOnlyList<Row> Matches, int RawCount, IReadOnlyList<DiscoveryIssue> Issues, DateTimeOffset CollectedAtUtc)
    {
        public bool Failed => RawCount == 0 && Issues.Any(i => i.Error.Code != ErrorCode.DiscoveryItemUnreadable);
    }

    /// <summary>Parses the arguments after "discover". Returns false for any malformed input; nothing is partially applied.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out DiscoverOptions options)
    {
        options = new(DiscoverKind.All, false, null, null);
        DiscoverKind? kind = null; var json = false; string? filter = null; int? limit = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--json" when !json: json = true; break;
                case "--filter" when filter is null && i + 1 < args.Count && args[i + 1].Trim().Length > 0: filter = args[++i]; break;
                case "--limit" when limit is null && i + 1 < args.Count
                    && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= MaxLimit: limit = n; i++; break;
                case "startup" or "tasks" or "services" or "apps" or "all" when kind is null:
                    kind = a switch { "startup" => DiscoverKind.Startup, "tasks" => DiscoverKind.Tasks, "services" => DiscoverKind.Services, "apps" => DiscoverKind.Apps, _ => DiscoverKind.All };
                    break;
                default: return false;
            }
        }
        if (kind is null) return false;
        options = new(kind.Value, json, filter, limit);
        return true;
    }

    public static async Task<int> RunAsync(DiscoverOptions options, DiscoveryService service, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        List<Section> sections;
        try { sections = await CollectAsync(options, service, ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            stderr.WriteLine(T("DiscoverCancelled"));
            return ErrorCatalog.ExitCode(ErrorCategory.Cancelled);
        }
        catch (Exception e)
        {
            // Never print exception text; classify to a stable code and show only its localized guidance.
            var error = new ErrorMapper().Map(e);
            stderr.WriteLine(ErrorLine(error));
            return error.ExitCode;
        }

        var totalFailure = sections.All(s => s.Failed);
        var exit = 0;
        if (totalFailure)
        {
            var primary = sections.SelectMany(s => s.Issues).Where(i => i.Error.Code != ErrorCode.DiscoveryItemUnreadable)
                .OrderByDescending(i => i.Error.Severity).First().Error;
            exit = primary.ExitCode;
            stderr.WriteLine(ErrorLine(primary));
        }
        if (options.Json) WriteJson(options, sections, stdout); else WriteText(options, sections, stdout);
        return exit;
    }

    private static async Task<List<Section>> CollectAsync(DiscoverOptions o, DiscoveryService svc, CancellationToken ct)
    {
        var wanted = o.Kind == DiscoverKind.All ? new[] { DiscoverKind.Startup, DiscoverKind.Tasks, DiscoverKind.Services, DiscoverKind.Apps } : [o.Kind];
        var tasks = wanted.Select(k => k switch
        {
            DiscoverKind.Startup => Build("startup", "KindStartup", ["ColName", "ColSource", "ColScope", "ColApproval", "ColCommand"], svc.StartupAsync(ct), StartupRow, o),
            DiscoverKind.Tasks => Build("tasks", "KindTasks", ["ColName", "ColState", "ColPath", "ColCommand"], svc.TasksAsync(ct), TaskRow, o),
            DiscoverKind.Services => Build("services", "KindServices", ["ColName", "ColStartMode", "ColRunState", "ColAccount"], svc.ServicesAsync(ct), ServiceRow, o),
            _ => Build("apps", "KindApps", ["ColName", "ColVersion", "ColPublisher", "ColScope"], svc.ApplicationsAsync(ct), AppRow, o),
        }).ToList();
        return [.. await Task.WhenAll(tasks).ConfigureAwait(false)];
    }

    private static async Task<Section> Build<TItem>(string kind, string titleKey, string[] headers, Task<DiscoveryResult<TItem>> pending, Func<TItem, Row> map, DiscoverOptions o)
    {
        var result = await pending.ConfigureAwait(false);
        var rows = result.Items.Select(map);
        if (o.Filter is { } f) rows = rows.Where(r => r.Search.Contains(f, StringComparison.OrdinalIgnoreCase));
        var ordered = rows.OrderBy(r => r.SortKey, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.StableId, StringComparer.Ordinal).ToList();
        return new(kind, titleKey, headers, ordered, result.Items.Count, result.Issues, result.CollectedAtUtc);
    }

    private static Row StartupRow(StartupItem i) => new("startup", i.Name, i.StableId,
        Join(i.Name, i.Command, i.Publisher, i.StableId), [i.Name, i.Source.ToString(), i.Scope.ToString(), i.Approval.ToString(), i.Command],
        new StartupJson("startup", i.StableId, i.Name, i.Command, i.Source, i.Scope, i.Approval, i.Publisher));

    private static Row TaskRow(ScheduledTaskInfo t) => new("tasks", t.Name, t.StableId,
        Join(t.Name, t.Path, t.Command, t.Author), [t.Name, t.State.ToString(), t.Path, t.Command ?? ""],
        new TaskJson("tasks", t.StableId, t.Path, t.Name, t.State, t.Hidden, t.Author, t.LastRunUtc, t.NextRunUtc, t.RunsAtLogonOrBoot, t.Command, t.IsVendorMicrosoft));

    private static Row ServiceRow(ServiceInfo s) => new("services", s.DisplayName, s.StableId,
        Join(s.DisplayName, s.StableId, s.Account, s.ImagePath, s.Description), [s.DisplayName, s.StartMode.ToString(), s.RunState.ToString(), s.Account ?? ""],
        new ServiceJson("services", s.StableId, s.DisplayName, s.StartMode, s.RunState, s.Kind, s.Account, s.ImagePath, s.Description));

    private static Row AppRow(InstalledApplication a) => new("apps", a.DisplayName, a.Identity.StableIdentity,
        Join(a.DisplayName, a.Identity.Publisher, a.Version, a.InstallLocation, a.Identity.StableIdentity),
        [a.DisplayName, a.Version ?? "", a.Identity.Publisher ?? "", a.Identity.Scope.ToString()],
        new AppJson("apps", a.Identity.StableIdentity, a.DisplayName, a.Version, a.Identity.Publisher, a.Identity.Kind, a.Identity.Scope,
            a.Identity.RegistryView, a.InstallLocation, a.InstalledOn, a.EstimatedSizeKb, a.IsSystemComponent, a.RegistrationProvenance));

    private static string Join(params string?[] parts) => string.Join('\n', parts.Where(p => !string.IsNullOrEmpty(p)));

    private static IReadOnlyList<Row> Limited(Section s, DiscoverOptions o) => o.Limit is { } n ? s.Matches.Take(n).ToList() : s.Matches;

    private static void WriteJson(DiscoverOptions o, List<Section> sections, TextWriter output)
    {
        var items = sections.SelectMany(s => Limited(s, o).Select(r => r.Json)).ToList();
        var issues = sections.SelectMany(s => s.Issues).Select(i => new IssueJson(Safe(i.Source), i.Error.Code, i.Error.Category.ToString(), i.Error.Detail is null ? null : Safe(i.Error.Detail))).ToList();
        var document = new
        {
            SchemaVersion,
            ReadOnly = true,
            CollectedAtUtc = sections.Max(s => s.CollectedAtUtc),
            Kind = o.Kind.ToString().ToLowerInvariant(),
            TotalCount = sections.Sum(s => s.Matches.Count),
            Items = items,
            Issues = issues,
        };
        output.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
    }

    private static void WriteText(DiscoverOptions o, List<Section> sections, TextWriter output)
    {
        output.WriteLine(T("DiscoverReadOnly"));
        foreach (var section in sections)
        {
            var rows = Limited(section, o);
            output.WriteLine();
            output.WriteLine(F("DiscoverSection", T(section.TitleKey), rows.Count, section.Matches.Count));
            if (rows.Count == 0) { output.WriteLine(T("DiscoverNone")); continue; }
            var table = new List<string[]> { section.HeaderKeys.Select(T).ToArray() };
            table.AddRange(rows.Select(r => r.Cells.Select(Cell).ToArray()));
            var widths = Enumerable.Range(0, section.HeaderKeys.Length).Select(c => table.Max(r => r[c].Length)).ToArray();
            foreach (var r in table)
                output.WriteLine(string.Join("  ", r.Select((v, c) => c == r.Length - 1 ? v : v.PadRight(widths[c]))).TrimEnd());
        }
        var shown = sections.Sum(s => Limited(s, o).Count);
        var total = sections.Sum(s => s.Matches.Count);
        var issues = sections.SelectMany(s => s.Issues).ToList();
        output.WriteLine();
        output.WriteLine(F("DiscoverSummary", shown, total, issues.Count));
        foreach (var issue in issues)
            output.WriteLine(F("DiscoverIssue", Safe(issue.Source), issue.Error.Code, T(issue.Error.UserMessageKey)));
    }

    private static string ErrorLine(WinoraError e) =>
        F("DiscoverError", T(e.UserMessageKey), e.Code) + (e.CorrelationId is null ? "" : " " + F("DiscoverReference", e.CorrelationId));

    private static string Cell(string value) => Truncate(Clean(value), MaxCell);

    /// <summary>Text safe for one terminal line: control characters become spaces and length is bounded.</summary>
    internal static string Safe(string value) => Truncate(Clean(value), MaxDetail);

    private static string Clean(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value) sb.Append(char.IsControl(ch) ? ' ' : ch);
        return sb.ToString().Trim();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
