// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>One entry of the startup type picker.</summary>
public sealed record ServiceModeChoice(ServiceStartMode Mode, string Label)
{
    /// <summary>The name a screen reader and UI Automation read for an entry of the picker.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The actions of the selected service: Start, Stop, Restart and the startup type picker with its button. A button is disabled when the service or the safety policy does not allow the
/// action; the reason is shown below as one plain sentence. A locked service shows why it is locked.
/// </summary>
public sealed class ServiceActionsPanel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly Func<ServiceChangeKind, Task> perform;
    private readonly Func<bool> canAct;
    private ServiceInfo? item;
    private ServicePolicyInfo? policy;
    private bool enabled;
    private ServiceModeChoice? selectedMode;
    private IReadOnlyList<ServiceModeChoice> modes = [];

    public ServiceActionsPanel(LocalizationService text, Func<ServiceChangeKind, Task> perform, Func<bool> canAct)
    {
        this.text = text; this.perform = perform; this.canAct = canAct;
        StartCommand = new(() => _ = this.perform(ServiceChangeKind.Start), () => Can(ServiceChangeKind.Start));
        StopCommand = new(() => _ = this.perform(ServiceChangeKind.Stop), () => Can(ServiceChangeKind.Stop));
        RestartCommand = new(() => _ = this.perform(ServiceChangeKind.Restart), () => Can(ServiceChangeKind.Restart));
        ApplyModeCommand = new(() => _ = this.perform(ServiceChangeKind.SetStartMode), CanApplyMode);
    }

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand ApplyModeCommand { get; }

    public LocalizationService Text => text;
    public bool HasSelection => item is not null;
    public bool IsLocked => policy?.IsLocked == true;
    public bool CanChange => item is not null && !IsLocked;

    public IReadOnlyList<ServiceModeChoice> Modes { get => modes; private set => SetProperty(ref modes, value); }

    public ServiceModeChoice? SelectedMode
    {
        get => selectedMode;
        set { if (SetProperty(ref selectedMode, value)) ApplyModeCommand.NotifyCanExecuteChanged(); }
    }

    /// <summary>The mode the picker holds, or null when nothing is selected.</summary>
    public ServiceStartMode? PickedMode => selectedMode?.Mode;

    /// <summary>One plain sentence: why the service is locked, or what kind of service it is.</summary>
    public string PolicyText { get; private set; } = "";
    public string WarningText { get; private set; } = "";
    public bool HasWarning => WarningText.Length > 0;
    public string RecommendationText { get; private set; } = "";
    public bool HasRecommendation => RecommendationText.Length > 0;

    private bool Can(ServiceChangeKind kind) => item is not null && policy is not null && enabled && canAct() && ServiceManagerService.Availability(kind, item, policy) is null;

    /// <summary>True when the action is allowed for the selected service now.</summary>
    public bool CanPerform(ServiceChangeKind kind) => Can(kind);

    /// <summary>True when the startup type can be set to this mode now: the service is not locked, the mode differs from the current one and the app has the rights.</summary>
    public bool CanPickMode(ServiceStartMode mode) => item is not null && policy is not null && !policy.IsLocked && enabled && canAct() && mode != item.StartMode;

    /// <summary>Puts a mode in the picker, as the person would.</summary>
    public void Pick(ServiceStartMode mode) => SelectedMode = Modes.FirstOrDefault(m => m.Mode == mode);

    private bool CanApplyMode() => item is not null && policy is not null && !policy.IsLocked && enabled && canAct() && selectedMode is { } pick && pick.Mode != item.StartMode;

    public void Update(ServiceInfo? selected, ServicePolicyInfo? info, bool canChange, string policyText, string warningText, string recommendationText, ServiceStartMode? preselect)
    {
        item = selected; policy = info; enabled = canChange; PolicyText = policyText; WarningText = warningText; RecommendationText = recommendationText;
        Modes = ServiceManagerService.StartModes.Select(m => new ServiceModeChoice(m, text.Enum(m))).ToList();
        selectedMode = preselect is { } p ? Modes.FirstOrDefault(m => m.Mode == p) : null;
        foreach (var name in new[] { nameof(HasSelection), nameof(IsLocked), nameof(CanChange), nameof(SelectedMode), nameof(PickedMode), nameof(PolicyText), nameof(WarningText), nameof(HasWarning), nameof(RecommendationText), nameof(HasRecommendation) })
            OnPropertyChanged(name);
        Notify();
    }

    public void Notify()
    {
        StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged(); ApplyModeCommand.NotifyCanExecuteChanged();
    }

    public void Refresh()
    {
        if (item is not null) Modes = ServiceManagerService.StartModes.Select(m => new ServiceModeChoice(m, text.Enum(m))).ToList();
        if (selectedMode is { } pick) { selectedMode = Modes.FirstOrDefault(m => m.Mode == pick.Mode); OnPropertyChanged(nameof(SelectedMode)); }
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>The notice above the list: administrator hint and the result of the last change. Both are announced to a screen reader.</summary>
public sealed class ServiceNoticePanel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly IElevationState elevation;
    private string? notice;
    private bool isError;

    public ServiceNoticePanel(LocalizationService text, IElevationState elevation) { this.text = text; this.elevation = elevation; }

    /// <summary>Where a success goes: a toast. Set by the page; a failure stays as the banner.</summary>
    public Func<FeedbackCenter?>? FeedbackSource { get; set; }

    public LocalizationService Text => text;
    public bool ShowNeedsAdministrator => !elevation.IsElevated;
    public string NeedsAdministratorTitle => elevation.UnsafeLocation ? text["AppLocationUnsafeTitle"] : text["AdminNoticeTitle"];
    public string NeedsAdministratorText => elevation.UnsafeLocation ? text["AppLocationUnsafe"] : text["AdminNoticeMessage"];
    public bool ShowAdminHowTo => !elevation.UnsafeLocation;
    public string? Notice => notice;
    public bool HasNotice => !string.IsNullOrEmpty(notice);
    public bool ShowSuccess => HasNotice && !isError;
    public bool ShowError => HasNotice && isError;

    public void Show(string? message, bool error)
    {
        notice = message; isError = error;
        if (!error && !string.IsNullOrEmpty(message)) FeedbackSource?.Invoke()?.Success(message);
        foreach (var name in new[] { nameof(Notice), nameof(HasNotice), nameof(ShowSuccess), nameof(ShowError) }) OnPropertyChanged(name);
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class ServiceHistoryRow(ServiceChangeRecord record, string description, string stateText, string when, bool canRevert, string undoLabel, RelayCommand<ServiceHistoryRow> revert)
{
    public ServiceChangeRecord Record { get; } = record;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public string UndoLabel { get; } = undoLabel;
    public RelayCommand<ServiceHistoryRow> RevertCommand { get; } = revert;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}

/// <summary>The change history block below the list.</summary>
public sealed class ServiceHistoryPanel(PageHistoryModel list) : ObservableObject
{
    public PageHistoryModel List => list;
    public void Refresh() { OnPropertyChanged(string.Empty); list.Refresh(); }
}
