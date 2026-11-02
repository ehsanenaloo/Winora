// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
namespace Winora.Infrastructure.Persistence;

/// <summary>One line of an operation journal as the support archive shows it: what kind of change, its outcome and when. Never a name, a path or a value.</summary>
public sealed record DiagnosticsJournalRow(Guid Id, string What, string State, string Code, DateTimeOffset AtUtc);

/// <summary>A journal area for the support archive. <see cref="Read"/> returns the newest rows first.</summary>
public sealed record DiagnosticsJournalSource(string Area, Func<IReadOnlyList<DiagnosticsJournalRow>> Read);

/// <summary>The journals of the system-changing features as bounded summaries. A record without an end line is shown as "interrupted".</summary>
public static class DiagnosticsJournals
{
    public const int PerArea = 50;
    private const string Interrupted = "interrupted";

    public static IReadOnlyList<DiagnosticsJournalSource> For(OptimizeService optimize, PowerChangeService power, NetworkService network, PermissionService permissions, StartupService startup,
        ServiceManagerService services, ScheduledTaskService tasks, RepairService repair, AppUninstallService uninstall, BloatwareService bloatware) =>
    [
        new("optimize", () => optimize.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, r.Change.DefinitionId, S(r.State), r.Code, r.BeganUtc)).ToList()),
        new("power", () => power.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, r.Change.Kind.ToString(), S(r.State), r.Code, r.BeganUtc)).ToList()),
        new("network", () => network.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, "dns", S(r.State), r.Code, r.BeganUtc)).ToList()),
        new("permissions", () => permissions.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, r.Change.Group.ToString(), S(r.State), r.Code, r.BeganUtc)).ToList()),
        new("startup", () => startup.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, r.Change.Action.ToString(), S(r.State), r.Code, r.BeganUtc)).ToList()),
        new("services", () => services.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, r.Change.Kind.ToString(), S(r.State), r.Code, r.BeganUtc)).ToList()),
        new("scheduled-tasks", () => tasks.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, r.Change.Kind.ToString(), S(r.State), r.Code, r.BeganUtc)).ToList()),
        new("repair", () => repair.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Begin.Id, r.Begin.Tool.ToString(), r.End is null ? Interrupted : r.End.Outcome.ToString(), r.End?.Code ?? Interrupted, r.BeganUtc)).ToList()),
        new("uninstall", () => uninstall.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, r.Change.Method.ToString(), r.End is null ? Interrupted : r.End.State.ToString(), r.End?.Code ?? Interrupted, r.BeganUtc)).ToList()),
        new("bloatware", () => bloatware.History(PerArea).Select(r => new DiagnosticsJournalRow(r.Change.Id, r.Change.Kind.ToString(), S(r.State), r.State is null ? Interrupted : r.Steps.LastOrDefault(s => !s.Ok)?.Code ?? "ok", r.BeganUtc)).ToList()),
    ];

    private static string S<T>(T? state) where T : struct => state is null ? Interrupted : state.Value.ToString()!;
}
