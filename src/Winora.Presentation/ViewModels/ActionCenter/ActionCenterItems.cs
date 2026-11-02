// SPDX-License-Identifier: GPL-3.0-only
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>The marks on a navigation item: a run on its page, or a failed run until the page is opened.</summary>
public enum NavMark { None, Running, Failed }

/// <summary>What a notification means; the look (icon and color) comes from one shared mapping in the UI.</summary>
public enum NoteLevel { Ok, Info, Warning, Error, Restart }

/// <summary>One staged change in the Changes tab.</summary>
public sealed class ChangeItemViewModel
{
    private readonly StagedChange change;

    public ChangeItemViewModel(StagedChange change, string discardName, string beforeLabel, string afterLabel, string riskText)
    {
        this.change = change;
        DiscardName = discardName; BeforeLabel = beforeLabel; AfterLabel = afterLabel; RiskText = riskText;
        RemoveCommand = new RelayCommand(() => change.Remove?.Invoke(), () => change.Remove is not null);
    }

    public string Id => change.Id;
    public string Title => change.Title;
    public string Before => change.Before;
    public string After => change.After;
    public bool HasValues => change.HasValues;
    public RiskLevel? Risk => change.Risk;
    public RiskLevel RiskValue => change.Risk ?? RiskLevel.Low;
    /// <summary>The risk chip shows for Medium and High only; Low is the normal case and stays quiet.</summary>
    public bool ShowRisk => change.Risk is RiskLevel.Medium or RiskLevel.High;
    public string RiskText { get; }
    public string BeforeLabel { get; }
    public string AfterLabel { get; }
    public string DiscardName { get; }
    public bool CanRemove => change.Remove is not null;
    public RelayCommand RemoveCommand { get; }
    public string AutomationName => change.HasValues ? $"{change.Title}, {BeforeLabel} {change.Before}, {AfterLabel} {change.After}" : change.Title;
}

/// <summary>The staged changes of one page.</summary>
public sealed class ChangeGroupViewModel(string pageKey, string title, string countText, string openName, IReadOnlyList<ChangeItemViewModel> items, ICommand open)
{
    public string PageKey { get; } = pageKey;
    public string Title { get; } = title;
    public string CountText { get; } = countText;
    public string OpenName { get; } = openName;
    public IReadOnlyList<ChangeItemViewModel> Items { get; } = items;
    public ICommand OpenCommand { get; } = open;
    public string AutomationName => $"{Title}, {CountText}";
}

/// <summary>One run card of the Running section.</summary>
public sealed class RunCardViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private RunSnapshot snapshot;
    private string elapsed = "";

    public RunCardViewModel(RunSnapshot snapshot, LocalizationService text, string pageName, Action<Guid> stop, Action<string> go)
    {
        this.snapshot = snapshot; this.text = text; PageName = pageName;
        StopCommand = new RelayCommand(() => stop(snapshot.Id), () => CanStop);
        GoCommand = new RelayCommand(() => go(snapshot.PageKey));
    }

    public Guid Id => snapshot.Id;
    public string Title => snapshot.Title;
    public string PageKey => snapshot.PageKey;
    public string PageName { get; }
    public bool ChangesSystem => snapshot.ChangesSystem;
    public string KindText => text[ChangesSystem ? "ACChangesSystem" : "ACReadOnly"];
    public DateTimeOffset StartedAt => snapshot.StartedAt;
    public bool IsDeterminate => snapshot.Share is not null;
    public bool IsIndeterminate => snapshot.Share is null;
    public double Share => snapshot.Share ?? 0;
    public string ShareText => snapshot.Share is { } s ? text.Percent(Math.Round(s * 100)) : text["Working"];
    public bool IsStopping => snapshot.State == RunState.Stopping;
    public bool CanStop => snapshot.CanStop && !IsStopping;
    public string StopText => IsStopping ? text["ACStopping"] : text["ACStop"];
    /// <summary>Why Stop is disabled; empty when it works.</summary>
    public string StopReason => snapshot.CanStop ? "" : text["ACCannotStop"];
    public bool HasStopReason => StopReason.Length > 0;
    public string ElapsedText { get => elapsed; private set => SetProperty(ref elapsed, value); }
    public RelayCommand StopCommand { get; }
    public RelayCommand GoCommand { get; }
    public string AutomationName => $"{Title}, {PageName}, {ShareText}, {KindText}";

    public void Update(RunSnapshot next)
    {
        snapshot = next;
        foreach (var n in new[] { nameof(IsDeterminate), nameof(IsIndeterminate), nameof(Share), nameof(ShareText), nameof(IsStopping), nameof(CanStop), nameof(StopText), nameof(StopReason), nameof(HasStopReason), nameof(AutomationName) }) OnPropertyChanged(n);
        StopCommand.NotifyCanExecuteChanged();
    }

    public void Tick(DateTimeOffset now) => ElapsedText = text.Digits(ActionCenterRules.Elapsed(now - snapshot.StartedAt));
    public void Relabel() => OnPropertyChanged(string.Empty);
}

/// <summary>One item of the Recent list.</summary>
public sealed class NoteViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private ActivityNotification note;

    public NoteViewModel(ActivityNotification note, LocalizationService text, string pageName, Action<Guid> remove, Action<string> go)
    {
        this.note = note; this.text = text; PageName = pageName;
        RemoveCommand = new RelayCommand(() => remove(note.Id));
        GoCommand = new RelayCommand(() => go(note.PageKey), () => note.PageKey.Length > 0);
    }

    public Guid Id => note.Id;
    public string Text => note.Text;
    public string PageKey => note.PageKey;
    public string PageName { get; }
    public bool HasPage => note.PageKey.Length > 0;
    public NoteLevel Level => note.Severity switch
    {
        NotificationSeverity.Ok => NoteLevel.Ok,
        NotificationSeverity.Warning => NoteLevel.Warning,
        NotificationSeverity.Error => NoteLevel.Error,
        NotificationSeverity.Restart => NoteLevel.Restart,
        _ => NoteLevel.Info,
    };
    public string LevelName => text[Level switch { NoteLevel.Ok => "ACSevOk", NoteLevel.Warning => "ACSevWarning", NoteLevel.Error => "ACSevError", NoteLevel.Restart => "ACSevRestart", _ => "ACSevInfo" }];
    public bool IsNew => !note.IsRead;
    public string NewText => text["ACNew"];
    public string TimeText
    {
        get
        {
            var local = note.At.ToLocalTime();
            return text.When(local, local.Date == DateTime.Today ? "t" : "g");
        }
    }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand GoCommand { get; }
    public string AutomationName => $"{LevelName}, {Text}, {PageName}, {TimeText}" + (IsNew ? ", " + NewText : "");

    public void Update(ActivityNotification next) { note = next; OnPropertyChanged(nameof(IsNew)); OnPropertyChanged(nameof(AutomationName)); }
}
