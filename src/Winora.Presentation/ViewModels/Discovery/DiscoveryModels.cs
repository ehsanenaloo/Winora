// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.Application;
using Winora.Domain;
using Winora.Domain.Elevation;

namespace Winora.Desktop;

/// <summary>Which chip draws a <see cref="BadgeModel"/>: a plain status badge, a risk chip or a source chip.</summary>
public enum ChipRole { Status, Risk, Source }

/// <summary>A chip on a row or in the details pane. Neutral chips use the transparent appearance; text always carries the meaning.</summary>
public sealed record BadgeModel(string Label, Tone Tone, ChipRole Role = ChipRole.Status, RiskLevel Risk = RiskLevel.Low, SourceClass Source = SourceClass.A)
{
    public bool IsNeutral => Tone == Tone.Neutral;
    public static BadgeModel ForRisk(string label, RiskLevel risk) => new(label, SettingFormat.RiskTone(risk), ChipRole.Risk, risk);
    public static BadgeModel ForSource(string label, SourceClass source) => new(label, Tone.Info, ChipRole.Source, Source: source);
}

/// <summary>The card of the details dialog a value belongs to. Auto puts left-to-right values (ids, paths, commands) under Technical details and the rest under Other.</summary>
public enum DetailGroup { Auto, Description, Technical, Run, Other }

/// <summary>One labelled value in the details dialog. IsLtr marks identifiers, paths and commands as left-to-right islands.</summary>
public sealed record DetailField(string Label, string Value, bool IsLtr = false, DetailGroup Group = DetailGroup.Auto);

/// <summary>A button on a row. The page runs it through its row action command with the row key. The name tells a screen reader which item it acts on.</summary>
public sealed record RowAction(string Label, string AutomationName, string HelpText);

/// <summary>Display model for one discovered item. Cheap to build; the detail fields are produced only when the row is selected.</summary>
public class DiscoveryRow(string key, string title, string subtitle, bool subtitleIsLtr, string secondary,
    IReadOnlyList<BadgeModel> badges, Func<IReadOnlyList<DetailField>> fields, RowAction? action = null) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>A WPF binding to an object without this event holds the object for the life of the process, and with it the page and its window.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void RaisePropertyChanged(string? name) => PropertyChanged?.Invoke(this, new(name));
    public RowAction? Action { get; } = action;
    private readonly Func<IReadOnlyList<DetailField>> fieldBuilder = fields;
    public bool HasAction => Action is not null;
    /// <summary>The next item of the list continues the same card (a row inside a group whose header is the top of the card), so no gap is left under this one.</summary>
    public virtual bool JoinsNext => false;
    public string ActionLabel => Action?.Label ?? "";
    public string ActionAutomationName => Action?.AutomationName ?? "";
    public string ActionHelp => Action?.HelpText ?? "";
    public string Key { get; } = key;
    public string Title { get; } = title;
    /// <summary>The title as shown: a Latin name keeps its own order in a right-to-left row (the marks change nothing in a left-to-right one).</summary>
    public string DisplayTitle => Bidi.HasRightToLeft(Title) ? Title : Bidi.Ltr(Title, TextDirection.RightToLeft);
    public string Subtitle { get; } = subtitle;
    public bool SubtitleIsLtr { get; } = subtitleIsLtr;
    public string Secondary { get; } = secondary;
    public IReadOnlyList<BadgeModel> Badges { get; } = badges;
    public bool HasSubtitle => Subtitle.Length > 0;
    public bool HasSecondary => Secondary.Length > 0;
    public string AutomationName => Badges.Count == 0 ? Title : Title + ", " + string.Join(", ", Badges.Select(b => b.Label));

    /// <summary>The list draws a badge only for a state that needs a look (caution or danger); the other states are quiet text on one line.</summary>
    public static bool NeedsAttention(BadgeModel badge) => badge.Tone is Tone.Caution or Tone.Danger;
    private IReadOnlyList<BadgeModel>? attention;
    public IReadOnlyList<BadgeModel> AttentionBadges => attention ??= [.. Badges.Where(NeedsAttention)];
    public bool HasAttention => AttentionBadges.Count > 0;
    public string AvatarName => Title;
    /// <summary>The accessible name of the row's "more" button: "More options for X". Set by the page.</summary>
    public string MoreName { get; set; } = "";
    /// <summary>The quiet states and the secondary text of the row, joined on one line.</summary>
    public string MetaText => string.Join(" · ", Badges.Where(b => !NeedsAttention(b)).Select(b => b.Label).Append(Secondary.Length == 0 || Bidi.HasRightToLeft(Secondary) ? Secondary : Bidi.Ltr(Secondary, TextDirection.RightToLeft)).Where(t => t.Length > 0));
    public bool HasMeta => MetaText.Length > 0;
    public IReadOnlyList<DetailField> BuildFields() => fieldBuilder();

    /// <summary>Copies a row so a page can derive a row with more parts (the Startup switch).</summary>
    protected DiscoveryRow(DiscoveryRow source) : this(source.Key, source.Title, source.Subtitle, source.SubtitleIsLtr, source.Secondary, source.Badges, source.fieldBuilder, source.Action) { }
}

/// <summary>A quick filter chip above a list. Selecting it sets the filters it stands for; it is selected while they match.</summary>
public sealed class QuickChip(string key, Func<string> label, Action select, Func<bool> isSelected) : ObservableObject
{
    public string Key { get; } = key;
    public string Label => label();
    public string AutomationName => Label;
    public bool IsSelected => isSelected();
    public System.Windows.Input.ICommand SelectCommand { get; } = new CommunityToolkit.Mvvm.Input.RelayCommand(select);
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One figure of the summary strip: a number with its label directly under it.</summary>
public sealed record SummaryStat(string Value, string Label)
{
    public string AutomationName => $"{Label}: {Value}";
}

/// <summary>One option of a filter. "All" has a null value and matches everything.</summary>
public sealed class FilterChoice(string? value, Func<string> label) : ObservableObject
{
    public string? Value { get; } = value;
    public string Label => label();
    public bool IsAll => Value is null;
    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>A dropdown filter that compares a string facet of the item with the chosen value.</summary>
public sealed class ChoiceFilter : ObservableObject
{
    private readonly Func<object, string?> facet;
    private readonly LocalizationService text;
    private readonly string labelKey;
    private FilterChoice selected;

    public ChoiceFilter(string labelKey, IReadOnlyList<FilterChoice> choices, Func<object, string?> facet, LocalizationService text)
    {
        this.labelKey = labelKey; this.facet = facet; this.text = text; Choices = choices; selected = choices[0];
    }
    public IReadOnlyList<FilterChoice> Choices { get; }
    public string Label => text[labelKey];
    public string HelpText => text["DiscFilterHelp"];
    public event EventHandler? Changed;
    public FilterChoice Selected
    {
        get => selected;
        set { if (value is not null && SetProperty(ref selected, value)) Changed?.Invoke(this, EventArgs.Empty); }
    }
    public bool IsActive => !selected.IsAll;
    public bool Matches(object item) => selected.IsAll || facet(item) == selected.Value;
    public void Reset() => Selected = Choices[0];
    public void RefreshLabels() { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(HelpText)); foreach (var c in Choices) c.Refresh(); }
}

/// <summary>A checkbox filter that hides a class of items while switched on.</summary>
public sealed class ToggleFilter(string labelKey, Func<object, bool> hides, LocalizationService text) : ObservableObject
{
    private bool isOn;
    public string Label => text[labelKey];
    public string HelpText => text["DiscFilterToggleHelp"];
    public event EventHandler? Changed;
    public bool IsOn { get => isOn; set { if (SetProperty(ref isOn, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public bool Matches(object item) => !isOn || !hides(item);
    public void Reset() => IsOn = false;
    public void RefreshLabels() { OnPropertyChanged(nameof(Label)); OnPropertyChanged(nameof(HelpText)); }
}

/// <summary>A sort key. Items whose key is null are always listed last.</summary>
public sealed class SortChoice(string labelKey, Func<object, IComparable?> key, LocalizationService text) : ObservableObject
{
    public string Label => text[labelKey];
    public Func<object, IComparable?> Key { get; } = key;
    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// Holds the latest read-only scan of one category and serializes scans so only one runs at a time. The scan itself always runs
/// on a worker thread, so an adapter that does synchronous work can never block the UI thread.
/// </summary>
public sealed class InventorySlot<T>(Func<CancellationToken, Task<DiscoveryResult<T>>> scan)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public DiscoveryResult<T>? Last { get; private set; }
    public event EventHandler? Changed;

    /// <summary>Sets the latest result without a scan (a page that merges other slots uses what they already read).</summary>
    public void Seed(DiscoveryResult<T> result) => Last = result;

    public async Task<DiscoveryResult<T>> ScanAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() => scan(cancellationToken), cancellationToken);
            // A scan that finished after it was cancelled is discarded so a stale answer never replaces a newer one.
            cancellationToken.ThrowIfCancellationRequested();
            Last = result;
            Changed?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally { gate.Release(); }
    }
}

/// <summary>The four read-only inventories shared by the discovery pages and the Overview summary.</summary>
public sealed class InventoryModel(DiscoveryService service)
{
    public InventorySlot<StartupItem> Startup { get; } = new(service.StartupAsync);
    public InventorySlot<ScheduledTaskInfo> Tasks { get; } = new(service.TasksAsync);
    public InventorySlot<ServiceInfo> Services { get; } = new(service.ServicesAsync);
    public InventorySlot<InstalledApplication> Applications { get; } = new(service.ApplicationsAsync);
    public InventorySlot<PowerPlanInfo> Power { get; } = new(service.PowerAsync);
    public InventorySlot<MachineSettingDefinition> MachineDefinitions { get; } = new(service.MachineDefinitionsAsync);
    public InventorySlot<QuarantineEntry> Quarantine { get; } = new(service.QuarantineAsync);
}
