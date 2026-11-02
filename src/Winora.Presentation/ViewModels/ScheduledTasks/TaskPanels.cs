// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;

namespace Winora.Desktop;

/// <summary>The notice above the list: administrator hint and the result of the last change. Both are announced to a screen reader.</summary>
public sealed class TaskNoticePanel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly IElevationState elevation;
    private string? notice;
    private bool isError;

    public TaskNoticePanel(LocalizationService text, IElevationState elevation) { this.text = text; this.elevation = elevation; }

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

public sealed class TaskHistoryRow(ScheduledTaskChangeRecord record, string description, string stateText, string when, bool canRevert, string undoLabel, RelayCommand<TaskHistoryRow> revert)
{
    public ScheduledTaskChangeRecord Record { get; } = record;
    public string Description { get; } = description;
    public string StateText { get; } = stateText;
    public string When { get; } = when;
    public bool CanRevert { get; } = canRevert;
    public string UndoLabel { get; } = undoLabel;
    public RelayCommand<TaskHistoryRow> RevertCommand { get; } = revert;
    public string AutomationName => $"{Description}, {StateText}, {When}";
}

/// <summary>The change history block below the list.</summary>
public sealed class TaskHistoryPanel(PageHistoryModel list) : ObservableObject
{
    public PageHistoryModel List => list;
    public void Refresh() { OnPropertyChanged(string.Empty); list.Refresh(); }
}
