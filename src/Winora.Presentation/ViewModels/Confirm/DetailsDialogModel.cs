// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Winora.Desktop;

/// <summary>A group of labelled values in the details dialog, shown as one card. An empty heading shows the values without one.</summary>
public sealed record DetailsSection(string Heading, IReadOnlyList<DetailField> Fields)
{
    public bool HasHeading => Heading.Length > 0;
}

/// <summary>A button in the details dialog. The dialog closes first, then the action runs, so an action that asks for a confirmation can show its own dialog.</summary>
public sealed record DetailsAction(string Label, string Name, string Help, Func<Task> Run, bool IsPrimary = false, bool IsEnabled = true, bool IsDanger = false);

/// <summary>
/// What the one details dialog shows: the item name as title, a header card with tag chips, cards of labelled values, an optional editor (the choice of a High risk setting), optional action buttons,
/// a Copy details button and one closing button. Every page opens its details with this.
/// </summary>
public sealed record DetailsDialog(string Title, IReadOnlyList<DetailsSection> Sections, string CopyText, string CopyLabel, string CopiedText, string CopyFailedText, string CloseLabel,
    Func<string, bool> Copy)
{
    /// <summary>Tags in the header card: risk, source, state.</summary>
    public IReadOnlyList<BadgeModel> Chips { get; init; } = [];
    /// <summary>One quiet line under the title in the header card (publisher, id).</summary>
    public string Subtitle { get; init; } = "";
    public bool SubtitleIsLtr { get; init; }
    /// <summary>A view model the dialog draws with its own template, between the cards and the buttons.</summary>
    public object? Editor { get; init; }
    public IReadOnlyList<DetailsAction> Actions { get; init; } = [];

    /// <summary>The text Copy details puts on the clipboard: every section, one value per line.</summary>
    public static string PlainText(string title, IReadOnlyList<DetailsSection> sections) =>
        string.Join(Environment.NewLine, new[] { title, "" }.Concat(sections.SelectMany(s => (s.HasHeading ? new[] { s.Heading } : [])
            .Concat(s.Fields.Select(f => f.Label + ": " + f.Value.Replace(Environment.NewLine, "; "))).Append(""))));
}

/// <summary>A button of the dialog with its command.</summary>
public sealed class DetailsActionItem(DetailsAction action, Action<DetailsAction> run) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Never raised. It lets WPF bindings listen through a weak event; on a plain object they hold the object, and with it the page of its action, for the life of the process.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public string Label => action.Label;
    public string Name => action.Name;
    public string Help => action.Help;
    public bool IsPrimary => action.IsPrimary;
    public bool IsDanger => action.IsDanger;
    public bool IsEnabled => action.IsEnabled;
    public RelayCommand Command { get; } = new(() => run(action), () => action.IsEnabled);
}

/// <summary>Binding model of the details dialog.</summary>
public sealed class DetailsDialogModel : ObservableObject
{
    private readonly DetailsDialog dialog;
    private string status = "";

    public DetailsDialogModel(DetailsDialog dialog)
    {
        this.dialog = dialog;
        CopyCommand = new(() => Status = this.dialog.Copy(this.dialog.CopyText) ? this.dialog.CopiedText : this.dialog.CopyFailedText);
        Actions = [.. dialog.Actions.Select(a => new DetailsActionItem(a, Run))];
    }

    /// <summary>Set by the window: closes the dialog. Without a host the action simply runs.</summary>
    public Action? RequestClose { get; set; }

    private void Run(DetailsAction action)
    {
        RequestClose?.Invoke();
        var task = action.Run();
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public string Title => dialog.Title;
    public IReadOnlyList<BadgeModel> Chips => dialog.Chips;
    public bool HasChips => dialog.Chips.Count > 0;
    public string Subtitle => dialog.Subtitle;
    public bool HasSubtitle => dialog.Subtitle.Length > 0;
    public bool SubtitleIsLtr => dialog.SubtitleIsLtr;
    public IReadOnlyList<DetailsSection> Sections => dialog.Sections;
    public object? Editor => dialog.Editor;
    public bool HasEditor => dialog.Editor is not null;
    public IReadOnlyList<DetailsActionItem> Actions { get; }
    public bool HasActions => Actions.Count > 0;
    public string CopyLabel => dialog.CopyLabel;
    public string CloseLabel => dialog.CloseLabel;
    public RelayCommand CopyCommand { get; }
    public string Status { get => status; private set => SetProperty(ref status, value); }
}

/// <summary>Builds the one details dialog from labelled values. The values are sorted into the cards Detailed description, Technical details, Last recorded run and Other.</summary>
public static class DetailsDialogBuilder
{
    public static IReadOnlyList<DetailsSection> Group(LocalizationService text, IEnumerable<DetailField> fields)
    {
        var list = fields.Where(f => f.Value.Length > 0).ToList();
        DetailGroup Resolve(DetailField f) => f.Group != DetailGroup.Auto ? f.Group : f.IsLtr ? DetailGroup.Technical : DetailGroup.Other;
        List<DetailsSection> sections = [];
        foreach (var (group, key) in new[] { (DetailGroup.Description, "DetailsSecDescription"), (DetailGroup.Technical, "DetailsSecTechnical"), (DetailGroup.Run, "DetailsSecRun"), (DetailGroup.Other, "DetailsSecOther") })
            if (list.Where(f => Resolve(f) == group).ToList() is { Count: > 0 } part) sections.Add(new(text[key], part));
        return sections;
    }

    public static DetailsDialog Build(LocalizationService text, Func<string, bool> copy, string title, IEnumerable<DetailField> fields, IReadOnlyList<BadgeModel>? chips = null, string subtitle = "",
        bool subtitleIsLtr = false, IReadOnlyList<DetailsAction>? actions = null, object? editor = null) =>
        Make(text, copy, title, Group(text, fields), chips, subtitle, subtitleIsLtr, actions, editor);

    public static DetailsDialog Make(LocalizationService text, Func<string, bool> copy, string title, IReadOnlyList<DetailsSection> sections, IReadOnlyList<BadgeModel>? chips = null, string subtitle = "",
        bool subtitleIsLtr = false, IReadOnlyList<DetailsAction>? actions = null, object? editor = null) =>
        new(title, sections, DetailsDialog.PlainText(title, sections), text["DiscCopyDetails"], text["DiscCopied"], text["DiscCopyFailed"], text["DetailsOk"], copy)
        {
            Chips = chips ?? [], Subtitle = subtitle, SubtitleIsLtr = subtitleIsLtr, Actions = actions ?? [], Editor = editor,
        };
}
