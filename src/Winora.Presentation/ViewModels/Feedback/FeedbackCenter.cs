// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;

namespace Winora.Desktop;

public enum ToastKind { Success, Info, Warning, Error, RestartNeeded }

/// <summary>One short message at the bottom of the page. Success and info without an action go away by themselves; warnings, errors, "restart needed" and any toast with an action stay until dismissed.</summary>
public sealed class ToastItem(ToastKind kind, string text, string? actionText = null, Func<Task>? action = null, string? actionHelp = null, bool quiet = false) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Never raised. It lets WPF bindings listen through a weak event; on a plain object they hold the object, and with it the page of its action, for the life of the process.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    private readonly Func<Task>? action = action;
    public ToastKind Kind { get; } = kind;
    public string Text { get; } = text;
    public string? ActionText { get; } = actionText;
    public string? ActionHelp { get; } = actionHelp;
    /// <summary>True for a toast that is already the short view of a notification (or a hint): it is never copied into the notification list again.</summary>
    public bool Quiet { get; } = quiet;
    /// <summary>Warnings, errors, "restart needed" and any toast with an action are notifications too. Plain confirmations stay toasts only.</summary>
    public bool BecomesNotification => !Quiet && (Kind is ToastKind.Warning or ToastKind.Error or ToastKind.RestartNeeded || HasAction);
    public bool HasAction => action is not null && !string.IsNullOrEmpty(ActionText);
    /// <summary>A toast with an action button stays until it is closed: a timed close would take the action away before a keyboard or screen reader user reaches it.</summary>
    public bool IsSticky => Kind is ToastKind.Warning or ToastKind.Error or ToastKind.RestartNeeded || HasAction;
    public Task RunActionAsync() => action is { } run ? run() : Task.CompletedTask;
}

/// <summary>A failed item in a result report: what it was, why it failed and what to do next.</summary>
public sealed record ResultFailure(string Title, string Reason, string? NextStep)
{
    public string AutomationName => NextStep is null ? $"{Title}. {Reason}" : $"{Title}. {Reason} {NextStep}";
}

/// <summary>The report after a multi-item change: counts, the failures and what can be done next. A failure never goes away by itself.</summary>
public sealed class ResultReportModel : ObservableObject
{
    private readonly LocalizationService text;

    public ResultReportModel(LocalizationService text, string headline, int applied, int alreadySet, IReadOnlyList<ResultFailure> failures, Func<Task>? undoAll = null, Action? copy = null, Action? close = null)
    {
        this.text = text; Headline = headline; Applied = applied; AlreadySet = alreadySet; Failures = failures;
        UndoAllCommand = undoAll is null ? null : new AsyncRelayCommand(undoAll);
        CopyCommand = new RelayCommand(() => copy?.Invoke());
        CloseCommand = new RelayCommand(() => close?.Invoke());
    }

    public string Headline { get; }
    public int Applied { get; }
    public int AlreadySet { get; }
    public int Failed => Failures.Count;
    public IReadOnlyList<ResultFailure> Failures { get; }
    public bool HasFailures => Failures.Count > 0;
    public IAsyncRelayCommand? UndoAllCommand { get; }
    public bool HasUndoAll => UndoAllCommand is not null && Applied > 0;
    public RelayCommand CopyCommand { get; }
    public RelayCommand CloseCommand { get; }
    public bool IsAssertive => HasFailures;
    public string AppliedLabel => text["ReportApplied"];
    public string AlreadySetLabel => text["ReportAlreadySet"];
    public string FailedLabel => text["ReportFailed"];
    public string FailuresHeading => text["ReportFailuresHeading"];
    public string UndoAllText => text["ReportUndoAll"];
    public string UndoAllHelp => text["ReportUndoAllHelp"];
    public string CopyText => text["ReportCopy"];
    public string CopyHelp => text["ReportCopyHelp"];
    public string CloseText => text["Close"];
    public string AutomationName => $"{Headline}. {AppliedLabel} {Applied}, {AlreadySetLabel} {AlreadySet}, {FailedLabel} {Failed}";

    /// <summary>Plain text for the clipboard. It holds names and reasons only, no identifiers, paths or reference codes.</summary>
    public string PlainText()
    {
        var b = new StringBuilder();
        b.AppendLine(Headline);
        b.AppendLine($"{AppliedLabel}: {Applied}").AppendLine($"{AlreadySetLabel}: {AlreadySet}").AppendLine($"{FailedLabel}: {Failed}");
        foreach (var f in Failures) b.AppendLine($"- {f.Title}: {f.Reason}" + (f.NextStep is null ? "" : " " + f.NextStep));
        return b.ToString().TrimEnd();
    }
}

/// <summary>
/// The shell's place for feedback: one toast at a time (a queue of three behind it) and one result report. Pages talk to it through <see cref="PageViewModel.Feedback"/>.
/// The engine and journal flow never go through here: the page has already recorded and read back the change before it tells the person.
/// </summary>
public sealed class FeedbackCenter : ObservableObject
{
    public const int QueueLimit = 3;
    private readonly Queue<ToastItem> queue = new();
    private readonly LocalizationService text;
    private readonly IClipboardService? clipboard;
    private ToastItem? current;
    private ResultReportModel? report;

    public FeedbackCenter(LocalizationService text, IClipboardService? clipboard = null)
    {
        this.text = text; this.clipboard = clipboard;
    }

    public ToastItem? Current { get => current; private set { if (SetProperty(ref current, value)) OnPropertyChanged(nameof(HasToast)); } }
    public bool HasToast => current is not null;
    public int Queued => queue.Count;
    public ResultReportModel? Report { get => report; private set { if (SetProperty(ref report, value)) OnPropertyChanged(nameof(HasReport)); } }
    public bool HasReport => report is not null;

    /// <summary>Raised for each toast that also becomes a notification (see <see cref="ToastItem.BecomesNotification"/>). The shell copies it into the activity list.</summary>
    public Action<ToastItem>? Notified { get; set; }

    public void Show(ToastItem item)
    {
        if (item.BecomesNotification) Notified?.Invoke(item);
        if (current is null) { Current = item; return; }
        if (queue.Count >= QueueLimit) queue.Dequeue();
        queue.Enqueue(item);
    }

    public void Success(string message, string? actionText = null, Func<Task>? action = null, string? actionHelp = null) => Show(new(ToastKind.Success, message, actionText, action, actionHelp));
    public void Info(string message) => Show(new(ToastKind.Info, message));
    public void Warning(string message) => Show(new(ToastKind.Warning, message));
    public void Error(string message) => Show(new(ToastKind.Error, message));
    public void RestartNeeded(string message) => Show(new(ToastKind.RestartNeeded, message));

    public void DismissToast() => Current = queue.Count > 0 ? queue.Dequeue() : null;

    /// <summary>Builds a report whose Copy button writes plain text to the clipboard and whose Close calls <paramref name="close"/>.</summary>
    public ResultReportModel CreateReport(string headline, int applied, int alreadySet, IReadOnlyList<ResultFailure> failures, Func<Task>? undoAll, Action close)
    {
        ResultReportModel? model = null;
        model = new(text, headline, applied, alreadySet, failures, undoAll,
            () => { if (clipboard?.TrySetText(model!.PlainText()) == true) Success(text["ReportCopied"]); }, close);
        return model;
    }

    /// <summary>Shows the report of a multi-item change at the top of the content area. A single applied item with no failure is a toast instead.</summary>
    public void ShowReport(string headline, int applied, int alreadySet, IReadOnlyList<ResultFailure> failures, Func<Task>? undoAll = null) =>
        Report = CreateReport(headline, applied, alreadySet, failures, undoAll is null ? null : async () => { DismissReport(); await undoAll(); }, DismissReport);

    public void DismissReport() => Report = null;

    /// <summary>Copies plain text and says so in a toast. A clipboard held by another program shows a warning instead.</summary>
    public void CopyText(string value, string copiedMessage)
    {
        if (clipboard?.TrySetText(value) == true) Success(copiedMessage);
        else if (clipboard is not null) Warning(text["ToolsCopyFailed"]);
    }

    /// <summary>For tests and hosts that need a feedback center that shows nothing.</summary>
    public static FeedbackCenter Silent(LocalizationService text) => new(text);
}
