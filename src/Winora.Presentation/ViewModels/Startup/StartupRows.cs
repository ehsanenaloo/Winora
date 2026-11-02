// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.Application;

namespace Winora.Desktop;

/// <summary>How much a startup entry is expected to slow the sign-in. An estimate, never a measurement (see <see cref="StartupImpactEstimator"/>).</summary>
public enum StartupImpact { Low, Medium, High }

/// <summary>
/// Estimates the impact of a startup entry from the size of the program file it starts: large programs load more code and data when they start.
/// Winora does not time the sign-in, so the page says "estimated". An entry whose file size is unknown counts as Low.
/// </summary>
public static class StartupImpactEstimator
{
    public const long MediumBytes = 5L * 1024 * 1024;
    public const long HighBytes = 50L * 1024 * 1024;

    public static StartupImpact Estimate(StartupFileFacts file) => file.Size switch
    {
        { } size when size >= HighBytes => StartupImpact.High,
        { } size when size >= MediumBytes => StartupImpact.Medium,
        _ => StartupImpact.Low,
    };
}

/// <summary>
/// One row of the Startup list: the shared row plus what the switch needs. The switch stages a change (nothing is written); the page applies all staged changes after one review.
/// </summary>
public sealed class StartupRow : DiscoveryRow
{
    private readonly Action<StartupRow> staged;
    private readonly string onText, offText;
    private bool isOn;

    public StartupRow(DiscoveryRow source, StartupEntry entry, StartupImpact impact, string impactText, string subLine, bool original, bool desired, bool canStage, string lockText,
        string onText, string offText, string willChangeText, string switchName, Action<StartupRow> staged, AppIconLoader? icon = null) : base(source)
    {
        Entry = entry; Impact = impact; ImpactText = impactText; SubLine = subLine; Original = original; isOn = desired; CanStage = canStage; LockText = lockText;
        this.onText = onText; this.offText = offText; WillChangeText = willChangeText; SwitchName = switchName; this.staged = staged; Icon = icon ?? new AppIconLoader(null, null);
    }


    private bool lastInGroup = true;
    /// <summary>The last row of its group closes the card whose top is the group header.</summary>
    public bool IsLastInGroup => lastInGroup;
    public override bool JoinsNext => !lastInGroup;
    public void SetLastInGroup(bool value)
    {
        if (lastInGroup == value) return;
        lastInGroup = value;
        RaisePropertyChanged(nameof(IsLastInGroup));
        RaisePropertyChanged(nameof(JoinsNext));
    }

    public StartupEntry Entry { get; }
    public StartupImpact Impact { get; }
    public string ImpactText { get; }
    /// <summary>High and Medium get a badge; Low is quiet text.</summary>
    public bool ShowImpactBadge => Impact != StartupImpact.Low;
    public bool ShowImpactQuiet => Impact == StartupImpact.Low;
    public bool IsHighImpact => Impact == StartupImpact.High;
    public bool IsMediumImpact => Impact == StartupImpact.Medium;
    public string SubLine { get; }
    /// <summary>Where the entry starts from (for example "Registry (Run)" and "All users"), as chips.</summary>
    public IReadOnlyList<BadgeModel> SourceChips { get; init; } = [];
    /// <summary>The publisher or author line, or a note that none is recorded.</summary>
    public string PublisherText { get; init; } = "";
    /// <summary>The full path of the program file, or the command when the path is not known. Shown left to right.</summary>
    public string PathText { get; init; } = "";
    public bool HasPath => PathText.Length > 0;
    public string SwitchHelp { get; init; } = "";
    /// <summary>The open-location button of this row.</summary>
    public System.Windows.Input.ICommand? OpenLocationCommand { get; init; }
    public bool CanOpenLocation { get; init; }
    public string OpenLocationText { get; init; } = "";
    public string OpenLocationName { get; init; } = "";
    /// <summary>The real icon of the program (the letter badge shows while it is null).</summary>
    public AppIconLoader Icon { get; }
    /// <summary>True when the entry starts with Windows now (before any staged change).</summary>
    public bool Original { get; }
    public bool CanStage { get; }
    public bool IsLocked => !CanStage;
    public string LockText { get; }
    public string WillChangeText { get; }
    public string SwitchName { get; }
    /// <summary>The switch. Setting it stages the change; a locked row ignores it.</summary>
    public bool IsOn
    {
        get => isOn;
        set
        {
            if (value == isOn) return;
            if (!CanStage) { Raise(nameof(IsOn)); return; }
            isOn = value;
            Raise(nameof(IsOn)); Raise(nameof(IsStaged)); Raise(nameof(StatusText)); Raise(nameof(RowAutomationName));
            staged(this);
        }
    }
    public bool IsStaged => isOn != Original;
    public string StatusText => isOn ? onText : offText;
    public string RowAutomationName => IsStaged ? $"{Title}, {StatusText}, {WillChangeText}" : $"{Title}, {StatusText}";
    public StartupAction DesiredAction => isOn ? StartupAction.Enable : StartupAction.Disable;

    /// <summary>Puts the switch back to the state the entry has now.</summary>
    public void Reset() { if (isOn == Original) return; isOn = Original; Raise(nameof(IsOn)); Raise(nameof(IsStaged)); Raise(nameof(StatusText)); Raise(nameof(RowAutomationName)); }

    private void Raise([CallerMemberName] string? name = null) => RaisePropertyChanged(name);
}

/// <summary>
/// The card that heads one group of the Startup list: icon, title, count chip, one line of text and a chevron. It is not a selectable row; the list skips it and the whole card toggles the group.
/// </summary>
public sealed class StartupCategoryRow(StartupCategory category, Action<StartupCategory, bool> changed) : ObservableObject
{
    private bool expanded = true;
    private string title = "", description = "", countText = "", stateText = "", help = "";

    public StartupCategory Category { get; } = category;
    public string Key => "category|" + Category;
    public IconKey Icon { get; init; }
    public string Title => title;
    public string Description => description;
    public string CountText => countText;
    public string ToggleHelp => help;
    public string AutomationName => $"{title}, {countText}, {stateText}";
    /// <summary>The open group has entries under its header, which is the top of their card.</summary>
    public bool JoinsNext => expanded && hasRows;
    private bool hasRows;

    /// <summary>True while the group shows its entries. Setting it from the card asks the page to remember the choice.</summary>
    public bool IsExpanded
    {
        get => expanded;
        set { if (expanded == value) return; expanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(AutomationName)); OnPropertyChanged(nameof(JoinsNext)); changed(Category, value); }
    }

    /// <summary>Sets the texts and the state from the page without telling the page about a user choice.</summary>
    public void Update(string title, string description, string countText, string stateText, string help, bool expanded, int membersCount = 1)
    {
        this.title = title; this.description = description; this.countText = countText; this.stateText = stateText; this.help = help; this.expanded = expanded; hasRows = membersCount > 0;
        OnPropertyChanged(string.Empty);
    }
}
