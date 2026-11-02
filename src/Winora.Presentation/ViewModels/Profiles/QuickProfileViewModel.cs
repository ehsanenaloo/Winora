// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;

namespace Winora.Desktop;

/// <summary>One built-in profile in the list of the Quick profile card.</summary>
public sealed class QuickProfileOption(Profile profile, LocalizationService text) : ObservableObject
{
    public Profile Profile { get; } = profile;
    public string Id => Profile.Id;
    public string Name => text[Profile.NameKey!];
    public void Refresh() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// The "Quick profile" card of Overview: pick one of the built-in profiles, see how many settings would change on this PC and open the same review as the Profiles page.
/// Nothing is applied here; the profile page asks once and applies through <see cref="ProfileService"/>.
/// </summary>
public sealed class QuickProfileViewModel : ObservableObject
{
    private readonly LocalizationService text;
    private readonly ProfileService service;
    private readonly Action<string> review;
    private QuickProfileOption? selected;
    private string summary = "";
    private string lastApplied = "";
    private bool loaded, busy;
    private Task work = Task.CompletedTask;
    private CancellationTokenSource? cancel;
    // The last plan counts and run, kept so the lines can be written again in the other language.
    private (int Change, int AlreadySet)? planCounts;
    private ProfileHistoryEntry? last;
    private bool historyRead;

    public QuickProfileViewModel(LocalizationService text, ProfileService service, Action<string> review)
    {
        this.text = text; this.service = service; this.review = review;
        ReviewCommand = new(() => { if (selected is not null) review(selected.Id); }, () => selected is not null);
        text.PropertyChanged += (_, _) => { OnPropertyChanged(string.Empty); Rebuild(); };
    }

    public ObservableCollection<QuickProfileOption> Options { get; } = [];
    public RelayCommand ReviewCommand { get; }
    public bool IsLoaded => loaded;
    public bool IsBusy { get => busy; private set => SetProperty(ref busy, value); }
    public string Title => text["QuickProfileTitle"];
    public string Help => text["QuickProfileHelp"];
    public string RiskNote => text["QuickProfileRiskNote"];
    public string ReviewLabel => text["QuickProfileReview"];
    public string ReviewHelp => text["QuickProfileReviewHelp"];
    public string ComboLabel => text["QuickProfileChoose"];
    public string ComboHelp => text["QuickProfileChooseHelp"];
    public string Summary { get => summary; private set => SetProperty(ref summary, value); }
    public string LastApplied { get => lastApplied; private set { if (SetProperty(ref lastApplied, value)) OnPropertyChanged(nameof(HasLastApplied)); } }
    public bool HasLastApplied => lastApplied.Length > 0;

    public QuickProfileOption? Selected
    {
        get => selected;
        set { if (SetProperty(ref selected, value)) { ReviewCommand.NotifyCanExecuteChanged(); work = PlanAsync(); } }
    }

    public Task WhenIdleAsync() => work;
    /// <summary>A profile run elsewhere makes the card read again when the page opens next (the shell sets the signal).</summary>
    public JournalReader Journal { get; } = new();

    /// <summary>Reads the profiles and the last run when the page opens the first time and after a change was written elsewhere (never on a timer).</summary>
    public void Activate()
    {
        if (!loaded || Journal.IsStale) work = LoadAsync();
    }

    public void Rebuild()
    {
        foreach (var option in Options) option.Refresh();
        if (planCounts is { } counts && !IsBusy) Summary = SummaryText(counts);
        if (historyRead) LastApplied = LastText();
    }

    private string SummaryText((int Change, int AlreadySet) counts) => text.Format("QuickProfileSummary", counts.Change, counts.AlreadySet);

    private string LastText()
    {
        if (last is null) return text["QuickProfileNoneYet"];
        var name = Options.FirstOrDefault(o => o.Id == last.Label)?.Name ?? last.Label;
        return text.Plural("QuickProfileLast", last.Changed, name, text.When(last.AtUtc, "d"), last.Changed);
    }

    private async Task LoadAsync()
    {
        Journal.MarkRead();
        try
        {
            var catalog = await Task.Run(service.Load).ConfigureAwait(true);
            Options.Clear();
            foreach (var profile in catalog.BuiltIn.OrderBy(p => p.Id == "balanced" ? 0 : 1).ThenBy(p => p.Id, StringComparer.Ordinal)) Options.Add(new(profile, text));
            loaded = true;
            OnPropertyChanged(nameof(IsLoaded));
            selected = Options.FirstOrDefault();
            OnPropertyChanged(nameof(Selected));
            ReviewCommand.NotifyCanExecuteChanged();
            await PlanAsync().ConfigureAwait(true);
            await ReadLastAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { Summary = text["QuickProfileUnreadable"]; }
    }

    private async Task PlanAsync()
    {
        cancel?.Cancel();
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        if (selected is null) { IsBusy = false; return; }
        var option = selected;
        IsBusy = true;
        planCounts = null;
        Summary = text["QuickProfileChecking"];
        try
        {
            var plan = await service.PlanAsync(option.Profile, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || selected != option) return;
            planCounts = (plan.ChangeCount, plan.AlreadySetCount);
            Summary = SummaryText(planCounts.Value);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException) { Summary = text["QuickProfileUnreadable"]; }
        finally { if (!token.IsCancellationRequested) IsBusy = false; }
    }

    private async Task ReadLastAsync()
    {
        try
        {
            // Windows Update modes are written as groups too; they are not profiles.
            var history = (await service.HistoryAsync(CancellationToken.None).ConfigureAwait(true)).Where(e => HistoryText.KindOf(e.Label) == HistoryBatchKind.Profile).ToList();
            last = history.Count == 0 ? null : history[0];
            historyRead = true;
            LastApplied = LastText();
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { LastApplied = ""; }
    }
}
