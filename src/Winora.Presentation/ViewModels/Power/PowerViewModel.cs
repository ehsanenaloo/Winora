// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>One choice of a combo box. ToString is the label because screen readers read the item text from it.</summary>
public sealed record PowerValueItem(uint Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class PowerPlanRow(PowerPlanRef plan, string name, string kindText, string activeText, bool owned)
{
    public Guid Id { get; } = plan.Id;
    /// <summary>The name to show: the Windows plans in the language of the page, other names pinned when they are Latin.</summary>
    public string Name { get; } = name;
    /// <summary>The name Windows has for the plan, used for new names written to Windows.</summary>
    public string RawName { get; } = plan.Name;
    public bool IsActive { get; } = plan.IsActive;
    public bool IsBuiltIn { get; } = plan.IsBuiltIn;
    public bool IsOwnedCopy { get; } = owned;
    public string KindText { get; } = kindText;
    public string ActiveText { get; } = activeText;
    public bool ShowActive => IsActive;
    public string AutomationName => IsActive ? $"{Name}, {ActiveText}, {KindText}" : $"{Name}, {KindText}";
}

/// <summary>One setting of the selected plan with its value on mains power and on battery. A row is dirty when the user picked another value than the plan has now.</summary>
public sealed class PowerSettingRow : ObservableObject
{
    private PowerValueItem? ac, dc;
    private readonly Action changed;

    public PowerSettingRow(PowerSettingDefinition definition, string title, string description, IReadOnlyList<PowerValueItem> acOptions, IReadOnlyList<PowerValueItem> dcOptions, uint? currentAc, uint? currentDc, Action changed, string? dcWarning = null)
    {
        Definition = definition; Title = title; Description = description; AcOptions = acOptions; DcOptions = dcOptions; CurrentAc = currentAc; CurrentDc = currentDc; this.changed = changed; DcWarning = dcWarning;
        ac = acOptions.FirstOrDefault(o => o.Value == currentAc); dc = dcOptions.FirstOrDefault(o => o.Value == currentDc);
    }

    public PowerSettingDefinition Definition { get; }
    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<PowerValueItem> AcOptions { get; }
    public IReadOnlyList<PowerValueItem> DcOptions { get; }
    public uint? CurrentAc { get; }
    public uint? CurrentDc { get; }
    public bool AcAvailable => CurrentAc is not null;
    public bool DcAvailable => CurrentDc is not null;
    public string? DcWarning { get; }
    public bool HasDcWarning => DcWarning is not null && CurrentDc is not null;

    public PowerValueItem? SelectedAc { get => ac; set { if (SetProperty(ref ac, value)) { OnPropertyChanged(nameof(IsDirty)); changed(); } } }
    public PowerValueItem? SelectedDc { get => dc; set { if (SetProperty(ref dc, value)) { OnPropertyChanged(nameof(IsDirty)); changed(); } } }
    public bool AcDirty => ac is not null && CurrentAc is { } c && ac.Value != c;
    public bool DcDirty => dc is not null && CurrentDc is { } c && dc.Value != c;
    public bool IsDirty => AcDirty || DcDirty;
    public void Discard() { SelectedAc = AcOptions.FirstOrDefault(o => o.Value == CurrentAc); SelectedDc = DcOptions.FirstOrDefault(o => o.Value == CurrentDc); }
}

public sealed class PowerHistoryRow(PowerChangeRecord record, string description, string stateText, string when, bool canRevert, RelayCommand<PowerHistoryRow> revert, Guid? group = null)
{
    public PowerChangeRecord Record { get; } = record;
    public Guid? Group { get; } = group;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public RelayCommand<PowerHistoryRow> RevertCommand { get; } = revert;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}

public sealed class PowerProfileRow(PowerProfile profile, string title, string description, RelayCommand<PowerProfileRow> apply)
{
    public PowerProfile Profile { get; } = profile;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public RelayCommand<PowerProfileRow> ApplyCommand { get; } = apply;
    public string AutomationName => $"{Title}. {Description}";
}

/// <summary>
/// The Power page: the plans of this PC, the settings of the selected plan on mains power and on battery, switching the active plan, copying a plan, and the history of changes with undo.
/// Every change shows what will change, asks once, is recorded before it is made, is read back afterwards and can be undone while nothing else changed it (decision DEC-IW-022).
/// </summary>
public sealed class PowerViewModel : PageViewModel, INoticePage
{
    private int selectedTab;
    /// <summary>The tab of the page, kept while the app runs.</summary>
    public int SelectedTab { get => selectedTab; set => SetProperty(ref selectedTab, value); }

    private const int HistoryLimit = 30;
    private readonly PowerChangeService service;
    private readonly IPowerPlans plans;
    private readonly IReviewPrompt prompt;
    private readonly IElevationState elevation;
    private readonly IRestorePointGate? restorePoint;
    private string? restoreNote;
    private Task work = Task.CompletedTask;
    private PowerPlanRow? selected;
    private string? notice;
    private bool noticeIsError;
    private bool busy;
    private bool hiddenSettings;
    private string? loadError;

    public PowerViewModel(LocalizationService text, SimulationModel model, PowerChangeService service, IPowerPlans plans, IReviewPrompt prompt, IElevationState elevation, IRestorePointGate? restorePoint = null) : base("Power", text, model)
    {
        HistoryList = new(text, HistoryArea.Power, () => OpenHistory);
        this.service = service; this.plans = plans; this.prompt = prompt; this.elevation = elevation; this.restorePoint = restorePoint;
        RefreshCommand = new(() => work = ReloadAsync(), () => !Busy);
        ActivateCommand = new(() => work = ActivateAsync(), () => CanActivate);
        ApplyCommand = new(() => work = ApplyAsync(), () => CanApply);
        DiscardCommand = new(Discard, () => HasPending && !Busy);
        CopyCommand = new(() => work = CopyAsync(), () => CanCopy);
        DeleteCopyCommand = new(() => work = DeleteCopyAsync(), () => CanDeleteCopy);
        RevertHistory = new(row => { if (row is not null && !Busy) work = RevertAsync(row); }, _ => !Busy);
        ApplyProfile = new(row => { if (row is not null && CanApplyProfile) work = ApplyProfileAsync(row.Profile); });
        HibernationCommand = new(() => work = ToggleHibernationAsync(), () => CanToggleHibernation);
        InstallPerformanceCommand = new(() => work = InstallPerformanceAsync(), () => CanInstallPerformance);
        BuildProfiles();
    }

    private void BuildProfiles()
    {
        Profiles.Clear();
        foreach (var p in PowerProfiles.All) Profiles.Add(new(p, Text[$"PowerProfile_{p.Id}_Title"], Text[$"PowerProfile_{p.Id}_Description"], ApplyProfile));
    }

    private static readonly Dictionary<Guid, string> WindowsPlans = new()
    {
        [new("381b4222-f694-41f0-9685-ff5bb260df2e")] = "balanced",
        [new("a1841308-3541-4fab-bc81-f71556f20b4a")] = "power_saver",
        [new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")] = "high_performance",
        [PowerPlanCatalog.UltimatePerformance] = "ultimate_performance",
    };

    /// <summary>The plans Windows ships keep an English name on an English Windows; they are shown in the page language. Other names are the user's own.</summary>
    public string PlanName(Guid id, string name) => PlanName(Text, id, name);
    public static string PlanName(LocalizationService text, Guid? id, string name) => id is { } g && WindowsPlans.TryGetValue(g, out var key) ? text["PowerPlanName_" + key] : text.Name(name);

    public ObservableCollection<PowerProfileRow> Profiles { get; } = [];
    public RelayCommand<PowerProfileRow> ApplyProfile { get; }
    public RelayCommand HibernationCommand { get; }
    public RelayCommand InstallPerformanceCommand { get; }

    private IPowerSystem? Host => service.PowerSystem;
    public bool HasBattery => Host?.HasBattery() ?? false;
    public string DcWarningText => Text["PowerDcWarning"];
    public string BatteryNotice => Text["PowerBatteryNotice"];
    public bool ShowBatteryNotice => HasBattery;

    // ---- hibernation ----

    private bool? hibernation;
    public bool HibernationKnown => hibernation is not null;
    public string HibernationText => hibernation is { } on ? Text[on ? "PowerHibernationOn" : "PowerHibernationOff"] : "";
    public string HibernationButton => Text[hibernation == true ? "PowerHibernationTurnOff" : "PowerHibernationTurnOn"];
    public bool CanToggleHibernation => IsElevated && !Busy && hibernation is not null;
    public bool CanApplyProfile => IsElevated && !Busy && selected is not null;
    public bool CanInstallPerformance => IsElevated && !Busy;

    private void RefreshHibernation()
    {
        try { hibernation = Host?.HibernationEnabled(); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { hibernation = null; }
        foreach (var n in new[] { nameof(HibernationKnown), nameof(HibernationText), nameof(HibernationButton), nameof(CanToggleHibernation), nameof(ShowBatteryNotice) }) OnPropertyChanged(n);
        HibernationCommand.NotifyCanExecuteChanged();
    }

    private async Task ToggleHibernationAsync()
    {
        if (hibernation is not { } on) return;
        var target = on ? 0u : 1u;
        var lines = new List<string> { Text.Format("PowerHibernationLine", Text[on ? "PowerHibernationOn" : "PowerHibernationOff"], Text[on ? "PowerHibernationOff" : "PowerHibernationOn"]) };
        if (on) lines.Add(Text["PowerHibernationLoss"]);
        if (on && HasBattery) lines.Add(Text["PowerHibernationLaptop"]);
        var confirmation = new ActionConfirmation(Text[on ? "PowerHibernationOffTitle" : "PowerHibernationOnTitle"], Text[on ? "PowerHibernationOffBody" : "PowerHibernationOnBody"], lines, Text["PowerUndoNote"], Text[on ? "PowerHibernationTurnOff" : "PowerHibernationTurnOn"])
        {
            Changes = [new ConfirmChange(Text["PowerHibernationHeading"], Text[on ? "PowerHibernationOn" : "PowerHibernationOff"], Text[on ? "PowerHibernationOff" : "PowerHibernationOn"])],
            Risk = on ? RiskLevel.Medium : RiskLevel.Low, Undo = ConfirmUndo.Exact, Restart = false,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        await RunChangesAsync([new PowerRequest(PowerChangeKind.SetHibernation, Guid.Empty, Value: target, ExpectedBefore: on ? 1 : 0)]).ConfigureAwait(true);
    }

    // ---- performance plan ----

    private async Task InstallPerformanceAsync()
    {
        var previous = Plans.FirstOrDefault(p => p.IsActive)?.Name ?? "";
        var name = Text["PowerPerformancePlanName"];
        var lines = new List<string> { Text.Format("PowerInstallLine", name), Text.Format("PowerActivateLine", previous, name) };
        if (HasBattery) lines.Add(Text["PowerInstallLaptop"]);
        var confirmation = new ActionConfirmation(Text["PowerInstallTitle"], Text["PowerInstallBody"], lines, Text["PowerInstallUndoNote"], Text["PowerInstallPrimary"])
        {
            Changes = [new ConfirmChange(name, Text["PowerNotInstalled"], Text["PowerInstalled"]), new ConfirmChange(Text["PowerActivePlan"], previous, name)],
            Risk = RiskLevel.Medium, Undo = ConfirmUndo.Exact, Restart = false, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        using var run = Runs.Begin(Text.Format("ACRunApply", Title), true);
        if (!run.Started) return;
        if (!await RestorePointStepAsync().ConfigureAwait(true)) { run.Finish(RunResult.Stopped); return; }
        Busy = true; Notice = null;
        try { Report(await service.InstallPerformancePlanAsync(name).ConfigureAwait(true)); run.Finish(NoticeIsError ? RunResult.Warning : RunResult.Ok, Notice); }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title)); }
        finally { Busy = false; }
        await ReloadAsync().ConfigureAwait(true);
    }

    // ---- profiles ----

    private List<PowerRequest> ProfileRequests(PowerProfile profile)
    {
        var requests = new List<PowerRequest>();
        foreach (var entry in profile.Entries)
        {
            if (PowerSettings.Find(entry.SettingId) is not { } def || selected is null) continue;
            if (plans.ReadValue(selected.Id, def, entry.Source) is not { } current || current == entry.Value) continue;
            requests.Add(new(PowerChangeKind.SetValue, selected.Id, def.Id, entry.Source, entry.Value, ExpectedBefore: current));
        }
        return requests;
    }

    private async Task ApplyProfileAsync(PowerProfile profile)
    {
        if (selected is null) return;
        var requests = ProfileRequests(profile);
        var title = Text[$"PowerProfile_{profile.Id}_Title"];
        if (requests.Count == 0) { Notice = Text.Format("PowerProfileNothing", title); NoticeIsError = false; OnPropertyChanged(nameof(NoticeIsError)); return; }
        var lines = new List<string>();
        foreach (var request in requests)
        {
            var def = PowerSettings.Find(request.SettingId!)!;
            lines.Add(Line(def, Text[$"Power_{def.Id}_Title"], request.Source!.Value, request.ExpectedBefore!.Value, request.Value!.Value));
        }
        if (profile.RaisesPower && HasBattery) lines.Add(Text["PowerProfileLaptop"]);
        var confirmation = new ActionConfirmation(Text.Format("PowerProfileTitle", title), Text.Format("PowerProfileBody", selected.Name, requests.Count), lines, Text["PowerProfileUndoNote"], Text["PowerProfilePrimary"])
        {
            Changes = requests.Select(r => ChangeOf(PowerSettings.Find(r.SettingId!)!, r.Source!.Value, r.ExpectedBefore!.Value, r.Value!.Value)).ToList(),
            Risk = RiskLevel.Medium, Undo = ConfirmUndo.Exact, Restart = false, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        using var run = Runs.Begin(Text.Format("ACRunApply", Title), true);
        if (!run.Started) return;
        if (!await RestorePointStepAsync().ConfigureAwait(true)) { run.Finish(RunResult.Stopped); return; }
        Busy = true; Notice = null;
        try { Report(await service.ApplyGroupAsync(requests, "profile_" + profile.Id).ConfigureAwait(true)); run.Finish(NoticeIsError ? RunResult.Warning : RunResult.Ok, Notice); }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title)); }
        finally { Busy = false; }
        await ReloadAsync().ConfigureAwait(true);
    }

    public ObservableCollection<PowerPlanRow> Plans { get; } = [];
    public ObservableCollection<PowerSettingRow> Settings { get; } = [];
    /// <summary>The same settings as cards: one for mains power and one for battery per setting, in the order of the catalog.</summary>
    public ObservableCollection<PowerSideRow> SettingCards { get; } = [];
    public ObservableCollection<PowerHistoryRow> History { get; } = [];
    public PageHistoryModel HistoryList { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand DeleteCopyCommand { get; }
    public RelayCommand<PowerHistoryRow> RevertHistory { get; }

    public override bool IsBusy => Busy;
    private bool Busy { get => busy; set { if (SetProperty(ref busy, value)) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(ShowLoading)); OnPropertyChanged(nameof(ShowEmpty)); Notify(); } } }

    public bool IsElevated => elevation.IsElevated;
    public string AcLabel => Text["PowerOnAc"];
    public string DcLabel => Text["PowerOnBattery"];
    public string AcHelp => Text["PowerAcHelp"];
    public string DcHelp => Text["PowerDcHelp"];
    public string UndoLabel => Text["PowerUndo"];
    public string UndoHelp => Text["PowerUndoHelp"];
    public bool ShowNeedsAdministrator => !elevation.IsElevated;
    public string NeedsAdministratorTitle => elevation.UnsafeLocation ? Text["AppLocationUnsafeTitle"] : Text["AdminNoticeTitle"];
    public string NeedsAdministratorText => elevation.UnsafeLocation ? Text["AppLocationUnsafe"] : Text["AdminNoticeMessage"];
    public bool ShowAdminHowTo => !elevation.UnsafeLocation;
    public bool HasPlans => Plans.Count > 0;
    /// <summary>Windows returned no plan at all (the list is empty and the read did not fail).</summary>
    public bool ShowEmpty => !HasPlans && !Busy && !HasLoadError;
    public string? LoadError => loadError;
    public bool HasLoadError => loadError is not null;
    /// <summary>The first read of the plans is running: the plans tab shows the loading skeleton.</summary>
    public bool ShowLoading => Busy && !HasPlans && !HasLoadError;

    public PowerPlanRow? SelectedPlan
    {
        get => selected;
        set { if (SetProperty(ref selected, value)) { LoadSettings(); Notify(); OnPropertyChanged(nameof(SelectedPlanName)); OnPropertyChanged(nameof(HasSelection)); } }
    }

    public bool HasSelection => selected is not null;
    public string SelectedPlanName => selected?.Name ?? "";
    public bool HasHiddenSettings => hiddenSettings;
    public string? Notice { get => notice; private set { if (SetProperty(ref notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool NoticeIsError { get => noticeIsError; private set { if (SetProperty(ref noticeIsError, value)) OnPropertyChanged(nameof(NoticeSeverity)); } }
    public BannerSeverity NoticeSeverity => noticeIsError ? BannerSeverity.Warning : BannerSeverity.Success;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public bool HasPending => Settings.Any(s => s.IsDirty);
    public override int PendingCount => Settings.Count(s => s.AcDirty) + Settings.Count(s => s.DcDirty);
    public bool CanApply => IsElevated && !Busy && HasPending;
    public bool CanActivate => IsElevated && !Busy && selected is { IsActive: false };
    public bool CanCopy => IsElevated && !Busy && selected is not null;
    public bool CanDeleteCopy => IsElevated && !Busy && selected is { IsOwnedCopy: true, IsActive: false };
    public string ActionBarSummary => PendingCount == 1 ? Text["ABPendingOne"] : Text.Format("ABPendingFormat", PendingCount);
    public IReadOnlyList<string> PendingLines => Settings.Where(s => s.IsDirty).SelectMany(s => PendingLinesOf(s)).ToList();
    public override StagingView? Staging => PendingCount == 0 ? null : new(Key,
        Settings.Where(s => s.IsDirty).SelectMany(PowerChanges).ToList(),
        ActionBarSummary, Text["PowerApply"], Text["PowerApplyHelp"], Text["PowerDiscard"], Text["PowerDiscardHelp"],
        () => ApplyCommand.CanExecute(null), () => ApplyCommand.Execute(null), () => DiscardCommand.CanExecute(null), () => DiscardCommand.Execute(null));
    private IEnumerable<StagedChange> PowerChanges(PowerSettingRow row)
    {
        if (row.AcDirty)
            yield return new($"{row.Definition.Id}@ac", $"{row.Title} ({Source(Text, PowerSource.Ac)})", row.AcOptions.FirstOrDefault(o => o.Value == row.CurrentAc)?.Label ?? "", row.SelectedAc!.Label, null,
                () => { if (!Busy) row.SelectedAc = row.AcOptions.FirstOrDefault(o => o.Value == row.CurrentAc); });
        if (row.DcDirty)
            yield return new($"{row.Definition.Id}@dc", $"{row.Title} ({Source(Text, PowerSource.Dc)})", row.DcOptions.FirstOrDefault(o => o.Value == row.CurrentDc)?.Label ?? "", row.SelectedDc!.Label, null,
                () => { if (!Busy) row.SelectedDc = row.DcOptions.FirstOrDefault(o => o.Value == row.CurrentDc); });
    }
    private IEnumerable<string> PendingLinesOf(PowerSettingRow row)
    {
        if (row.AcDirty) yield return Line(row, PowerSource.Ac, row.CurrentAc!.Value, row.SelectedAc!.Value);
        if (row.DcDirty) yield return Line(row, PowerSource.Dc, row.CurrentDc!.Value, row.SelectedDc!.Value);
    }
    public string PendingText => Settings.Count(s => s.AcDirty) + Settings.Count(s => s.DcDirty) is var n and > 0 ? Text.Format("PowerPending", n) : Text["PowerNoPending"];

    private void Notify()
    {
        RefreshCommand.NotifyCanExecuteChanged(); ActivateCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); DiscardCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged(); DeleteCopyCommand.NotifyCanExecuteChanged(); RevertHistory.NotifyCanExecuteChanged();
        HibernationCommand.NotifyCanExecuteChanged(); InstallPerformanceCommand.NotifyCanExecuteChanged();
        foreach (var n in new[] { nameof(CanToggleHibernation), nameof(CanApplyProfile), nameof(CanInstallPerformance), nameof(CanApply),nameof(CanActivate), nameof(CanCopy), nameof(CanDeleteCopy), nameof(HasPending), nameof(PendingText), nameof(ShowEmpty), nameof(PendingCount), nameof(ActionBarSummary), nameof(PendingLines) }) OnPropertyChanged(n);
    }

    public override void Activate() { if ((Plans.Count == 0 || Journal.IsStale) && !Busy) work = ReloadAsync(); }
    public override Task WhenIdleAsync() => work;
    public override System.Windows.Input.ICommand? ShortcutRefresh() => RefreshCommand;
    protected override void OnLanguageChanged() { base.OnLanguageChanged(); BuildProfiles(); if (Plans.Count > 0) { var id = selected?.Id; Rebuild(id); var reading = work; work = Task.WhenAll(reading, RefreshHistoryAsync()); } }

    // ---- loading ----

    private Task ReloadAsync() => ReloadAsync(selected?.Id);

    private async Task ReloadAsync(Guid? keep)
    {
        Journal.MarkRead();
        Busy = true;
        try
        {
            var snapshot = await Task.Run(() => plans.Read(CancellationToken.None)).ConfigureAwait(true);
            loadError = null;
            Fill(snapshot, keep);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            loadError = Text["PowerLoadFailed"];
        }
        finally { Busy = false; foreach (var n in new[] { nameof(LoadError), nameof(HasLoadError), nameof(HasPlans), nameof(ShowEmpty), nameof(ShowLoading) }) OnPropertyChanged(n); Notify(); }
        RefreshHibernation();
        await RefreshHistoryAsync().ConfigureAwait(true);
    }

    private PowerSnapshot? last;

    private void Fill(PowerSnapshot snapshot, Guid? keep)
    {
        last = snapshot;
        Rebuild(keep);
    }

    private void Rebuild(Guid? keep)
    {
        if (last is null) return;
        Plans.Clear();
        var owned = service.OwnedCopySet();
        foreach (var p in last.Plans.OrderByDescending(p => p.IsActive).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Plans.Add(new(p, PlanName(p.Id, p.Name), Text.Enum(p.IsBuiltIn ? PowerPlanKind.BuiltIn : PowerPlanKind.Custom), Text["BadgeActive"], owned.Contains(p.Id)));
        SelectedPlan = Plans.FirstOrDefault(p => p.Id == keep) ?? Plans.FirstOrDefault(p => p.IsActive) ?? Plans.FirstOrDefault();
        OnPropertyChanged(nameof(HasPlans));
    }

    private void LoadSettings()
    {
        Settings.Clear(); SettingCards.Clear();
        hiddenSettings = false;
        if (selected is null) { OnPropertyChanged(nameof(HasHiddenSettings)); return; }
        foreach (var def in PowerSettings.All)
        {
            var ac = plans.ReadValue(selected.Id, def, PowerSource.Ac);
            var dc = plans.ReadValue(selected.Id, def, PowerSource.Dc);
            if (ac is null && dc is null) { hiddenSettings = true; continue; }
            Settings.Add(new(def, Text[$"Power_{def.Id}_Title"], Text[$"Power_{def.Id}_Description"], Options(def, ac), Options(def, dc), ac, dc, Notify, def.CostsBattery && HasBattery ? DcWarningText : null));
            var added = Settings[^1];
            if (added.AcAvailable) SettingCards.Add(new(Text, added, PowerSide.Mains, () => IsElevated));
            if (added.DcAvailable) SettingCards.Add(new(Text, added, PowerSide.Battery, () => IsElevated));
        }
        OnPropertyChanged(nameof(HasHiddenSettings));
    }

    private IReadOnlyList<PowerValueItem> Options(PowerSettingDefinition def, uint? current)
    {
        var items = def.Options.Select(o => new PowerValueItem(o.Value, Label(def, o))).ToList();
        if (current is { } c && items.All(i => i.Value != c))
        {
            items.Add(new(c, Label(def, new PowerOption(c, ""))));
            if (def.Kind != PowerSettingKind.Choice) items.Sort((a, b) => a.Value.CompareTo(b.Value));
        }
        return items;
    }

    public string Label(PowerSettingDefinition def, PowerOption option) => Label(Text, def, option);

    public static string Label(LocalizationService text, PowerSettingDefinition def, PowerOption option) => def.Kind switch
    {
        PowerSettingKind.Choice => text.Has(option.LabelKey) ? text[option.LabelKey] : option.Value.ToString(text.Culture),
        PowerSettingKind.Percent => text.Format("PowerPercent", option.Value),
        _ => FormatSeconds(text, option.Value),
    };

    public string FormatSeconds(uint seconds) => FormatSeconds(Text, seconds);

    public static string FormatSeconds(LocalizationService text, uint seconds)
    {
        if (seconds == 0) return text["PowerNever"];
        if (seconds < 60) return text.Format("PowerSeconds", seconds);
        if (seconds % 3600 == 0) return text.Format("PowerHours", seconds / 3600);
        if (seconds % 60 == 0) return text.Format("PowerMinutes", seconds / 60);
        return text.Format("PowerSeconds", seconds);
    }

    private static string Source(LocalizationService text, PowerSource source) => text[source == PowerSource.Ac ? "PowerOnAc" : "PowerOnBattery"];

    // ---- actions ----

    private void Discard() { foreach (var row in Settings) row.Discard(); Notice = null; Notify(); }

    private async Task ActivateAsync()
    {
        if (selected is not { IsActive: false } target) return;
        var previous = Plans.FirstOrDefault(p => p.IsActive)?.Name ?? "";
        var confirmation = new ActionConfirmation(Text["PowerActivateTitle"], Text["PowerActivateBody"], [Text.Format("PowerActivateLine", previous, target.Name)], Text["PowerUndoNote"], Text["PowerActivatePrimary"])
        {
            Changes = [new ConfirmChange(Text["PowerActivePlan"], previous, target.Name)], Risk = RiskLevel.Low, Undo = ConfirmUndo.Exact, Restart = false, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        await RunChangesAsync([new PowerRequest(PowerChangeKind.SetActive, target.Id)]).ConfigureAwait(true);
    }

    private async Task ApplyAsync()
    {
        if (selected is null) return;
        var requests = new List<PowerRequest>();
        var lines = new List<string>();
        var changes = new List<ConfirmChange>();
        foreach (var row in Settings.Where(s => s.IsDirty))
        {
            if (row.AcDirty) { requests.Add(Request(row, PowerSource.Ac, row.SelectedAc!.Value, row.CurrentAc!.Value)); lines.Add(Line(row, PowerSource.Ac, row.CurrentAc!.Value, row.SelectedAc.Value)); changes.Add(ChangeOf(row.Definition, PowerSource.Ac, row.CurrentAc!.Value, row.SelectedAc.Value, row.Title)); }
            if (row.DcDirty) { requests.Add(Request(row, PowerSource.Dc, row.SelectedDc!.Value, row.CurrentDc!.Value)); lines.Add(Line(row, PowerSource.Dc, row.CurrentDc!.Value, row.SelectedDc.Value)); changes.Add(ChangeOf(row.Definition, PowerSource.Dc, row.CurrentDc!.Value, row.SelectedDc.Value, row.Title)); }
        }
        if (requests.Count == 0) return;
        var confirmation = new ActionConfirmation(Text["PowerApplyTitle"], Text.Format("PowerApplyBody", selected.Name), lines, Text["PowerUndoNote"], Text["PowerApplyPrimary"])
        {
            Changes = changes, Risk = RiskLevel.Low, Undo = ConfirmUndo.Exact, Restart = false, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        await RunChangesAsync(requests).ConfigureAwait(true);
    }

    private PowerRequest Request(PowerSettingRow row, PowerSource source, uint value, uint current) => new(PowerChangeKind.SetValue, selected!.Id, row.Definition.Id, source, value, ExpectedBefore: current);
    private string OptionLabel(PowerSettingDefinition def, long value) => Label(def, new PowerOption((uint)value, def.Options.FirstOrDefault(o => o.Value == value)?.LabelKey ?? ""));

    private ConfirmChange ChangeOf(PowerSettingDefinition def, PowerSource source, long before, long after, string? title = null) =>
        new($"{title ?? Text[$"Power_{def.Id}_Title"]} ({Source(Text, source)})", OptionLabel(def, before), OptionLabel(def, after));

    private string Line(PowerSettingRow row, PowerSource source, uint before, uint after) => Line(row.Definition, row.Title, source, before, after);

    private string Line(PowerSettingDefinition def, string title, PowerSource source, long before, long after) =>
        Text.Format("PowerChangeLine", title, Source(Text, source), Label(def, new PowerOption((uint)before, def.Options.FirstOrDefault(o => o.Value == before)?.LabelKey ?? "")), Label(def, new PowerOption((uint)after, def.Options.FirstOrDefault(o => o.Value == after)?.LabelKey ?? "")));

    private async Task CopyAsync()
    {
        if (selected is null) return;
        var confirmation = new ActionConfirmation(Text["PowerCopyTitle"], Text.Format("PowerCopyBody", selected.Name), [], Text["PowerUndoNote"], Text["PowerCopyPrimary"])
        {
            Changes = [new ConfirmChange(Text.Name(selected.RawName + " " + Text["PowerCopySuffix"]))], Risk = RiskLevel.Low, Undo = ConfirmUndo.Exact, Restart = false, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        await RunChangesAsync([new PowerRequest(PowerChangeKind.CreateCopy, selected.Id, Name: selected.RawName + " " + Text["PowerCopySuffix"])]).ConfigureAwait(true);
    }

    private async Task DeleteCopyAsync()
    {
        if (selected is not { IsOwnedCopy: true, IsActive: false } target) return;
        var confirmation = new ActionConfirmation(Text["PowerDeleteTitle"], Text.Format("PowerDeleteBody", target.Name), [], Text["PowerDeleteWarning"], Text["PowerDeletePrimary"])
        {
            Changes = [new ConfirmChange(target.Name)], Risk = RiskLevel.Medium, Undo = ConfirmUndo.No, Restart = false, Irreversible = true, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return;
        await RunChangesAsync([new PowerRequest(PowerChangeKind.DeleteCopy, target.Id)]).ConfigureAwait(true);
    }

    private async Task RevertAsync(PowerHistoryRow row)
    {
        if (Busy) return;
        var confirmation = new ActionConfirmation(Text["PowerRevertTitle"], Text["PowerRevertBody"], [row.Description], Text["PowerUndoNote"], Text["PowerRevertPrimary"])
        {
            Changes = [new ConfirmChange(row.Description)], Risk = RiskLevel.Low, Undo = ConfirmUndo.WhileUnchanged, Restart = false, UsesRestorePoint = restorePoint is not null,
        };
        if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true) || Busy) return;
        using var run = Runs.Begin(Text.Format("ACRunRevert", Title), true);
        if (!run.Started) return;
        if (!await RestorePointStepAsync().ConfigureAwait(true) || Busy) { run.Finish(RunResult.Stopped); return; }
        Busy = true; Notice = null;
        try
        {
            if (row.Group is { } group)
            {
                var (results, skipped) = await service.RevertGroupAsync(group).ConfigureAwait(true);
                if (results.Count == 0) { Notice = CodeText(PowerCode.CannotRevert); NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); }
                else Report(results);
                if (skipped > 0 && results.Count > 0) Notice += " " + Text.Format("PowerGroupSkipped", skipped);
            }
            else
            {
                var result = await service.RevertAsync(row.Record.Change.Id).ConfigureAwait(true);
                Report([result]);
            }
            run.Finish(NoticeIsError ? RunResult.Warning : RunResult.Ok, Notice);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError)); run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title)); }
        finally { Busy = false; }
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Asks once per session whether to create a restore point first. False when the user cancelled.</summary>
    private async Task<bool> RestorePointStepAsync()
    {
        restoreNote = null;
        if (restorePoint is null) return true;
        var gate = await restorePoint.BeforeChangeAsync("Power").ConfigureAwait(true);
        if (!gate.Proceed) return false;
        restoreNote = RestorePointText.Notice(Text, gate);
        return true;
    }

    private async Task RunChangesAsync(IReadOnlyList<PowerRequest> requests)
    {
        using var run = Runs.Begin(Text.Format("ACRunApply", Title), true);
        if (!run.Started) return;
        if (!await RestorePointStepAsync().ConfigureAwait(true)) { run.Finish(RunResult.Stopped); return; }
        Busy = true; Notice = null;
        var results = new List<PowerApplyResult>();
        try
        {
            foreach (var request in requests) results.Add(await service.ApplyAsync(request).ConfigureAwait(true));
            Report(results);
            run.Finish(NoticeIsError ? RunResult.Warning : RunResult.Ok, Notice);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Notice = Text["ActionFailedUnexpected"]; NoticeIsError = true; OnPropertyChanged(nameof(NoticeIsError));
            run.Finish(RunResult.Error, Text.Format("ACRunFailedText", Title));
        }
        finally { Busy = false; }
        await ReloadAsync().ConfigureAwait(true);
    }

    private void Report(IReadOnlyList<PowerApplyResult> results)
    {
        var applied = results.Count(r => r.Ok);
        var problems = results.Where(r => !r.Ok).Select(r => r.Code).Distinct().Select(CodeText).ToList();
        var reapply = results.Any(r => r.Ok && r.Code == PowerCode.ReapplyFailed);
        if (problems.Count == 0)
        {
            Notice = reapply ? Text.Format("PowerAppliedReapply", applied) : applied == 1 ? Text["PowerAppliedOne"] : Text.Format("PowerApplied", applied);
            NoticeIsError = reapply;
        }
        else
        {
            Notice = applied > 0 ? Text.Format("PowerPartial", applied, string.Join(" ", problems)) : string.Join(" ", problems);
            NoticeIsError = true;
        }
        if (results.Any(r => r.RecordLost)) Notice += " " + Text["RecordLostNote"];
        if (restoreNote is not null && applied > 0) Notice += " " + restoreNote;
        OnPropertyChanged(nameof(NoticeIsError));
    }

    public string CodeText(string code)
    {
        var key = "PowerCode_" + code.Replace('.', '_');
        return Text.Has(key) ? Text[key] : Text["PowerCode_power_failed"];
    }

    // ---- history ----

    private sealed record HistoryData(IReadOnlyList<PowerChangeRecord> Records, IReadOnlyList<bool> Revertable, IReadOnlyDictionary<Guid, bool> GroupCanRevert);

    /// <summary>The journal and plan reads of the history. No UI object is touched, so it can run on a pool thread.</summary>
    private HistoryData GatherHistory()
    {
        IReadOnlyList<PowerChangeRecord> records;
        try { records = service.History(HistoryLimit); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { records = []; }
        var elevated = IsElevated;
        var revertable = elevated ? service.CanRevertAll(records) : [];
        var groups = new Dictionary<Guid, bool>();
        if (elevated)
            foreach (var group in records.Where(r => r.Change.Group is not null).Select(r => r.Change.Group!.Value).Distinct())
                groups[group] = service.CanRevertGroup(group);
        return new(records, revertable, groups);
    }

    private void RefreshHistory() => ApplyHistory(GatherHistory());

    private async Task RefreshHistoryAsync() => ApplyHistory(await Task.Run(GatherHistory).ConfigureAwait(true));

    private void ApplyHistory(HistoryData data)
    {
        History.Clear();
        var shown = new List<HistoryRowViewModel>();
        var records = data.Records;
        var revertable = data.Revertable;
        var seen = new HashSet<Guid>();
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (r.Change.Group is { } group)
            {
                if (!seen.Add(group)) continue;
                var members = records.Where(m => m.Change.Group == group).ToList();
                var undo = members.Any(m => m.Change.RevertOf is not null);
                var state = members.All(m => m.State == PowerChangeState.Applied) ? Text.Enum(PowerChangeState.Applied) : members.Any(m => m.State is null) ? Text["PowerInterrupted"] : Text.Enum(PowerChangeState.Failed);
                var title = Text.Has($"PowerGroup_{r.Change.Label}") ? Text[$"PowerGroup_{r.Change.Label}"] : Text["PowerGroup_default"];
                var text = Text.Format("PowerHistoryGroup", title, members.Count) + (undo ? " " + Text["PowerUndoSuffix"] : "");
                History.Add(new(r, text, state, Text.When(r.BeganUtc.ToLocalTime(), "g"), IsElevated && !undo && data.GroupCanRevert.GetValueOrDefault(group), RevertHistory, group));
                var g = History[^1];
                shown.Add(HistoryList.Row(group, r.BeganUtc, members.All(m => m.State == PowerChangeState.Applied) ? HistoryResult.Applied : HistoryResult.Failed, g.Description, g.CanRevert, g.RevertCommand, g, new(UndoLabel, UndoHelp)));
                continue;
            }
            History.Add(new(r, Describe(r.Change), r.State is { } s ? Text.Enum(s) : Text["PowerInterrupted"], Text.When(r.BeganUtc.ToLocalTime(), "g"), IsElevated && revertable[i], RevertHistory));
            var h = History[^1];
            shown.Add(HistoryList.Row(r.Change.Id, r.BeganUtc, r.State == PowerChangeState.Applied ? (records.Any(o => o.Change.RevertOf == r.Change.Id && o.State == PowerChangeState.Applied) ? HistoryResult.Undone : HistoryResult.Applied) : r.State == PowerChangeState.Dismissed ? HistoryResult.Dismissed : r.State is null ? HistoryResult.Unconfirmed : HistoryResult.Failed, h.Description, h.CanRevert, h.RevertCommand, h, new(UndoLabel, UndoHelp)));
        }
        HistoryList.Set(shown);
        OnPropertyChanged(nameof(HasHistory));
    }

    public bool HasHistory => History.Count > 0;

    private string Describe(PowerChange c) => Describe(Text, c);

    /// <summary>The history title of a power change; the Power page, the History page and Overview all use it, so the same record reads the same everywhere.</summary>
    public static string Describe(LocalizationService text, PowerChange c)
    {
        var undo = c.RevertOf is null ? "" : " " + text["PowerUndoSuffix"];
        var plan = PlanName(text, c.Plan, c.PlanName);
        switch (c.Kind)
        {
            case PowerChangeKind.SetActive: return text.Format("PowerHistorySwitch", plan) + undo;
            case PowerChangeKind.CreateCopy: return text.Format("PowerHistoryCopy", plan) + undo;
            case PowerChangeKind.DeleteCopy: return text.Format("PowerHistoryDelete", plan) + undo;
            case PowerChangeKind.SetHibernation: return text.Format("PowerHistoryHibernation", text[c.After == 1 ? "PowerHibernationOn" : "PowerHibernationOff"]) + undo;
            default:
                if (PowerSettings.Find(c.SettingId ?? "") is not { } def || c.Source is null) return plan;
                string V(long? v) => v is { } x ? Label(text, def, new PowerOption((uint)x, def.Options.FirstOrDefault(o => o.Value == x)?.LabelKey ?? "")) : "?";
                return text.Format("PowerHistoryValue", text[$"Power_{def.Id}_Title"], Source(text, c.Source.Value), plan, V(c.Before), V(c.After)) + undo;
        }
    }
}

public enum PowerSide { Mains, Battery }

/// <summary>
/// One side (mains or battery) of a power setting as a card of the settings page component. It wraps the page's own <see cref="PowerSettingRow"/>, so choosing a value only stages it: the
/// pending bar counts it and the review dialog applies it through the same service as before.
/// </summary>
public sealed class PowerSideRow : PlainSettingRow
{
    private readonly PowerSettingRow row;
    private readonly PowerSide side;
    private readonly Func<bool> canChange;
    private IReadOnlyList<RowChoice>? choices;

    public PowerSideRow(LocalizationService text, PowerSettingRow row, PowerSide side, Func<bool> canChange) : base(text)
    {
        this.row = row; this.side = side; this.canChange = canChange;
        row.PropertyChanged += (_, _) => Refresh();
        DiscardRelay = new(() => { if (side == PowerSide.Mains) row.SelectedAc = row.AcOptions.FirstOrDefault(o => o.Value == row.CurrentAc); else row.SelectedDc = row.DcOptions.FirstOrDefault(o => o.Value == row.CurrentDc); }, () => IsPending);
    }

    private IReadOnlyList<PowerValueItem> Options => side == PowerSide.Mains ? row.AcOptions : row.DcOptions;
    private uint? CurrentValue => side == PowerSide.Mains ? row.CurrentAc : row.CurrentDc;
    private PowerValueItem? Selected => side == PowerSide.Mains ? row.SelectedAc : row.SelectedDc;
    private RelayCommand DiscardRelay { get; }

    public PowerSettingRow Row => row;
    public PowerSide Side => side;
    public override string Id => row.Definition.Id + (side == PowerSide.Mains ? "@ac" : "@dc");
    public override string Title => Text.Format("PowerSideTitle", row.Title, Text[side == PowerSide.Mains ? "PowerOnAc" : "PowerOnBattery"]);
    public override string Description => row.Description;
    public override string CategoryKey => "Power";
    public override string CategoryLabel => Text["Power"];
    public override bool IsAvailable => CurrentValue is not null;
    public override string CurrentLabel => Options.FirstOrDefault(o => o.Value == CurrentValue)?.Label ?? "";
    public override IReadOnlyList<RowChoice> Choices => choices ??= Options.Select(o => new RowChoice(o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), o.Label, row.Definition.Id == "usb_suspend" ? o.Value == 1 : null)).ToList();
    public override RowChoice? SelectedChoice
    {
        get => Selected is { } s ? Choices.FirstOrDefault(c => c.Id == s.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null;
        set
        {
            var item = value is null ? null : Options.FirstOrDefault(o => o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == value.Id);
            if (item is null) { Refresh(); return; }
            if (side == PowerSide.Mains) row.SelectedAc = item; else row.SelectedDc = item;
        }
    }
    public override bool CanChoose => IsAvailable && canChange();
    public override bool IsPending => side == PowerSide.Mains ? row.AcDirty : row.DcDirty;
    public override string ConditionalNote => side == PowerSide.Battery && row.HasDcWarning ? row.DcWarning! : "";
    public override ICommand DiscardCommand => DiscardRelay;
}
