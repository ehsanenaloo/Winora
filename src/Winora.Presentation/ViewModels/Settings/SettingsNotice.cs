// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;

namespace Winora.Desktop;

/// <summary>
/// The one status message the settings pages show (outcome, partial result or classified error). Text is held as functions so it follows
/// the selected language; the object itself is persistent so XAML can bind to it while nothing is shown.
/// </summary>
public sealed class SettingsNotice : ObservableObject
{
    private Func<string> title = static () => "";
    private Func<string> message = static () => "";
    private Func<string>? actionLabel;
    private Func<Task>? action;
    private Func<string>? secondaryLabel;
    private Func<Task>? secondaryAction;
    private bool isOpen;
    private BannerSeverity severity = BannerSeverity.Informational;

    public bool IsOpen
    {
        get => isOpen;
        set { if (!value && isOpen) Clear(); }
    }
    public string Title => title();
    public string Message => message();
    public BannerSeverity Severity => severity;
    /// <summary>Errors and warnings interrupt assistive technology; informational and success messages are announced politely.</summary>
    public bool IsUrgent => severity is BannerSeverity.Error or BannerSeverity.Warning;
    public string? ActionLabel => actionLabel?.Invoke();
    public bool HasAction => action is not null;
    public string? SecondaryLabel => secondaryLabel?.Invoke();
    public bool HasSecondary => secondaryAction is not null;
    /// <summary>Stable code of the outcome or error behind the message (never translated); empty when nothing is shown.</summary>
    public string Code { get; private set; } = "";

    public void Show(BannerSeverity severity, Func<string> title, Func<string> message, string code, Func<string>? actionLabel = null, Func<Task>? action = null,
        Func<string>? secondaryLabel = null, Func<Task>? secondaryAction = null)
    {
        this.secondaryLabel = secondaryLabel; this.secondaryAction = secondaryAction;
        this.severity = severity; this.title = title; this.message = message; this.actionLabel = actionLabel; this.action = action; Code = code; isOpen = true;
        Refresh();
    }

    public void Clear()
    {
        isOpen = false; action = null; secondaryAction = null; secondaryLabel = null; actionLabel = null; title = static () => ""; message = static () => ""; Code = "";
        Refresh();
    }

    public Task RunActionAsync() => action is { } a ? a() : Task.CompletedTask;

    public Task RunSecondaryAsync() => secondaryAction is { } a ? a() : Task.CompletedTask;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
