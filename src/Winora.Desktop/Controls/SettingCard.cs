// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// A setting as the one Winora row (<see cref="InventoryRow"/>): a leading icon, the title with its badges, one or two lines of description, the control that changes it at the end and the row menu.
/// A pending change tints the row. The rows of a group share one card, so <see cref="IsFirst"/> and <see cref="IsLast"/> say which edges and corners the row draws. The card itself is not a tab stop:
/// the list item that holds it is, and Enter on the item moves keyboard focus to the control. A High risk setting has a "Review change" button instead of an inline control.
/// </summary>
public sealed class SettingCard : Control
{
    static SettingCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingCard), new FrameworkPropertyMetadata(typeof(SettingCard)));
        FocusableProperty.OverrideMetadata(typeof(SettingCard), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingCard), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(ISettingRow), typeof(SettingCard),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((SettingCard)d).SetValue(LeadSymbolPropertyKey, CategoryIcons.For(e.NewValue as ISettingRow))));
    public static readonly DependencyProperty IsFirstProperty = DependencyProperty.Register(nameof(IsFirst), typeof(bool), typeof(SettingCard), new FrameworkPropertyMetadata(true));
    public static readonly DependencyProperty IsLastProperty = DependencyProperty.Register(nameof(IsLast), typeof(bool), typeof(SettingCard), new FrameworkPropertyMetadata(true));
    private static readonly DependencyPropertyKey LeadSymbolPropertyKey = DependencyProperty.RegisterReadOnly(nameof(LeadSymbol), typeof(Wpf.Ui.Controls.SymbolRegular), typeof(SettingCard), new FrameworkPropertyMetadata(Wpf.Ui.Controls.SymbolRegular.Settings20));
    public static readonly DependencyProperty LeadSymbolProperty = LeadSymbolPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ListProperty = DependencyProperty.Register(nameof(List), typeof(SettingsListModel), typeof(SettingCard), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(LocalizationService), typeof(SettingCard), new FrameworkPropertyMetadata(null));

    public ISettingRow? Row { get => (ISettingRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    /// <summary>The page's list model: the card opens the details pane through it.</summary>
    public SettingsListModel? List { get => (SettingsListModel?)GetValue(ListProperty); set => SetValue(ListProperty, value); }
    public LocalizationService? Text { get => (LocalizationService?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>The first row of its group card: draws the top edge and the top corners.</summary>
    public bool IsFirst { get => (bool)GetValue(IsFirstProperty); set => SetValue(IsFirstProperty, value); }
    /// <summary>The last row of its group card: draws the bottom edge and the bottom corners.</summary>
    public bool IsLast { get => (bool)GetValue(IsLastProperty); set => SetValue(IsLastProperty, value); }
    /// <summary>The icon at the start of the row, taken from the setting's category.</summary>
    public Wpf.Ui.Controls.SymbolRegular LeadSymbol => (Wpf.Ui.Controls.SymbolRegular)GetValue(LeadSymbolProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new CardPeer(this);

    private sealed class CardPeer(SettingCard owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(SettingCard);
        protected override bool IsControlElementCore() => false;
    }
}

/// <summary>
/// Two to three choices as a segmented control, more as a drop-down list. Choosing only stages: the page applies nothing until the review is confirmed. The inner control carries the
/// name and help text of the setting.
/// </summary>
public sealed class ChoiceRow : Control
{
    /// <summary>Up to this many choices are shown as segments.</summary>
    public const int MaxSegments = 3;
    /// <summary>Segments stay short: when the labels of the choices add up to more than this, the row uses a drop-down list so no label wraps over three lines.</summary>
    public const int MaxSegmentChars = 34;

    static ChoiceRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ChoiceRow), new FrameworkPropertyMetadata(typeof(ChoiceRow)));
        FocusableProperty.OverrideMetadata(typeof(ChoiceRow), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ChoiceRow), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty ChoicesProperty = DependencyProperty.Register(nameof(Choices), typeof(IEnumerable), typeof(ChoiceRow),
        new FrameworkPropertyMetadata(null, (d, e) => ((ChoiceRow)d).OnChoices((IEnumerable?)e.NewValue)));
    public static readonly DependencyProperty SelectedChoiceProperty = DependencyProperty.Register(nameof(SelectedChoice), typeof(object), typeof(ChoiceRow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ChoiceRow)d).UpdateSwitch()));
    private static readonly DependencyPropertyKey IsSwitchPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsSwitch), typeof(bool), typeof(ChoiceRow), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IsSwitchProperty = IsSwitchPropertyKey.DependencyProperty;
    public static readonly DependencyProperty SwitchOnProperty = DependencyProperty.Register(nameof(SwitchOn), typeof(bool), typeof(ChoiceRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ChoiceRow)d).OnSwitchChanged((bool)e.NewValue)));
    private static readonly DependencyPropertyKey SwitchLabelPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SwitchLabel), typeof(string), typeof(ChoiceRow), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty SwitchLabelProperty = SwitchLabelPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ControlNameProperty = DependencyProperty.Register(nameof(ControlName), typeof(string), typeof(ChoiceRow), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty LabeledByProperty = DependencyProperty.Register(nameof(LabeledBy), typeof(UIElement), typeof(ChoiceRow), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty ControlHelpProperty = DependencyProperty.Register(nameof(ControlHelp), typeof(string), typeof(ChoiceRow), new FrameworkPropertyMetadata(""));
    private static readonly DependencyPropertyKey IsSegmentedPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsSegmented), typeof(bool), typeof(ChoiceRow), new FrameworkPropertyMetadata(true));
    public static readonly DependencyProperty IsSegmentedProperty = IsSegmentedPropertyKey.DependencyProperty;

    public IEnumerable? Choices { get => (IEnumerable?)GetValue(ChoicesProperty); set => SetValue(ChoicesProperty, value); }
    public object? SelectedChoice { get => GetValue(SelectedChoiceProperty); set => SetValue(SelectedChoiceProperty, value); }
    public string ControlName { get => (string)GetValue(ControlNameProperty); set => SetValue(ControlNameProperty, value); }
    /// <summary>The caption that labels the control for assistive technology (the card title).</summary>
    public UIElement? LabeledBy { get => (UIElement?)GetValue(LabeledByProperty); set => SetValue(LabeledByProperty, value); }
    public string ControlHelp { get => (string)GetValue(ControlHelpProperty); set => SetValue(ControlHelpProperty, value); }
    public bool IsSegmented => (bool)GetValue(IsSegmentedProperty);
    /// <summary>The two choices are the two sides of one switch: the row shows a switch with the label of the chosen side beside it.</summary>
    public bool IsSwitch => (bool)GetValue(IsSwitchProperty);
    public bool SwitchOn { get => (bool)GetValue(SwitchOnProperty); set => SetValue(SwitchOnProperty, value); }
    public string SwitchLabel => (string)GetValue(SwitchLabelProperty);

    private RowChoice? onChoice, offChoice;
    private bool syncing;

    /// <summary>See <see cref="ChoiceSwitch.Pair"/>.</summary>
    public static (RowChoice? On, RowChoice? Off) SwitchPair(IReadOnlyList<RowChoice> list) => ChoiceSwitch.Pair(list);

    private void OnChoices(IEnumerable? choices)
    {
        var list = choices?.OfType<RowChoice>().ToList() ?? [];
        (onChoice, offChoice) = SwitchPair(list);
        SetValue(IsSwitchPropertyKey, onChoice is not null);
        SetValue(IsSegmentedPropertyKey, choices is null || (list.Count <= MaxSegments && list.Sum(c => c.Label.Length) <= MaxSegmentChars));
        UpdateSwitch();
    }

    private void UpdateSwitch()
    {
        syncing = true;
        try
        {
            var selected = SelectedChoice as RowChoice;
            SetValue(SwitchOnProperty, onChoice is not null && selected?.Id == onChoice.Id);
            SetValue(SwitchLabelPropertyKey, selected?.Label ?? "");
        }
        finally { syncing = false; }
    }

    private void OnSwitchChanged(bool on)
    {
        if (syncing || onChoice is null || offChoice is null) return;
        SelectedChoice = on ? onChoice : offChoice;
    }

    /// <summary>Moves keyboard focus into the control (Enter on the list item calls this).</summary>
    public bool FocusControl() => MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));

    protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);

    private sealed class RowPeer(ChoiceRow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(ChoiceRow);
        protected override string GetNameCore() => owner.ControlName;
        protected override bool IsControlElementCore() => false;
    }
}

/// <summary>The icon of a row: one for what the setting is about when its id says so, otherwise the one of its category.</summary>
public static class CategoryIcons
{
    private static readonly (string Key, Wpf.Ui.Controls.SymbolRegular Symbol)[] Words =
    [
        ("extension", Wpf.Ui.Controls.SymbolRegular.Document20), ("hidden", Wpf.Ui.Controls.SymbolRegular.Eye20), ("checkbox", Wpf.Ui.Controls.SymbolRegular.CheckboxChecked20),
        ("autoplay", Wpf.Ui.Controls.SymbolRegular.Play20), ("context_menu", Wpf.Ui.Controls.SymbolRegular.Cursor20), ("copy_dialog", Wpf.Ui.Controls.SymbolRegular.Copy20),
        ("onedrive", Wpf.Ui.Controls.SymbolRegular.Cloud20), ("gallery", Wpf.Ui.Controls.SymbolRegular.Image20), ("wallpaper", Wpf.Ui.Controls.SymbolRegular.Image20),
        ("open_to", Wpf.Ui.Controls.SymbolRegular.FolderOpen20), ("theme", Wpf.Ui.Controls.SymbolRegular.WeatherMoon20), ("clipboard", Wpf.Ui.Controls.SymbolRegular.ClipboardPaste20),
        ("advertising", Wpf.Ui.Controls.SymbolRegular.Megaphone20), ("telemetry", Wpf.Ui.Controls.SymbolRegular.DataUsage20), ("speech", Wpf.Ui.Controls.SymbolRegular.Mic20),
        ("toast", Wpf.Ui.Controls.SymbolRegular.Alert20), ("notification", Wpf.Ui.Controls.SymbolRegular.Alert20), ("keyboard", Wpf.Ui.Controls.SymbolRegular.Keyboard20),
        ("mouse", Wpf.Ui.Controls.SymbolRegular.Cursor20), ("clock", Wpf.Ui.Controls.SymbolRegular.Clock20), ("time_format", Wpf.Ui.Controls.SymbolRegular.Clock20),
        ("battery", Wpf.Ui.Controls.SymbolRegular.Battery620), ("game", Wpf.Ui.Controls.SymbolRegular.XboxController20), ("shutdown", Wpf.Ui.Controls.SymbolRegular.Power20),
        ("storage", Wpf.Ui.Controls.SymbolRegular.HardDrive20), ("drive", Wpf.Ui.Controls.SymbolRegular.HardDrive20), ("dns", Wpf.Ui.Controls.SymbolRegular.Globe20),
        ("sync", Wpf.Ui.Controls.SymbolRegular.ArrowSync20), ("recent", Wpf.Ui.Controls.SymbolRegular.History20), ("search", Wpf.Ui.Controls.SymbolRegular.Search20),
        ("transparency", Wpf.Ui.Controls.SymbolRegular.Sparkle20), ("snap", Wpf.Ui.Controls.SymbolRegular.WindowMultiple20), ("accessibility", Wpf.Ui.Controls.SymbolRegular.Accessibility20),
        ("click_to_do", Wpf.Ui.Controls.SymbolRegular.Bot20), ("copilot", Wpf.Ui.Controls.SymbolRegular.Bot20), ("recall", Wpf.Ui.Controls.SymbolRegular.Bot20), ("ai.", Wpf.Ui.Controls.SymbolRegular.Bot20), ("paint", Wpf.Ui.Controls.SymbolRegular.PaintBrush20), ("uac", Wpf.Ui.Controls.SymbolRegular.ShieldLock20), ("smartscreen", Wpf.Ui.Controls.SymbolRegular.ShieldCheckmark20), ("memory_integrity", Wpf.Ui.Controls.SymbolRegular.Shield20), ("cipher", Wpf.Ui.Controls.SymbolRegular.LockClosed20), ("tls", Wpf.Ui.Controls.SymbolRegular.LockClosed20), ("ssl", Wpf.Ui.Controls.SymbolRegular.LockClosed20), ("dtls", Wpf.Ui.Controls.SymbolRegular.LockClosed20), ("hash", Wpf.Ui.Controls.SymbolRegular.LockClosed20), ("cpu", Wpf.Ui.Controls.SymbolRegular.DeveloperBoard20), ("camera", Wpf.Ui.Controls.SymbolRegular.Camera20), ("location", Wpf.Ui.Controls.SymbolRegular.Location20), ("diagnostic", Wpf.Ui.Controls.SymbolRegular.Bug20), ("crash", Wpf.Ui.Controls.SymbolRegular.Bug20), ("wer_", Wpf.Ui.Controls.SymbolRegular.Bug20), ("activity", Wpf.Ui.Controls.SymbolRegular.History20), ("bluetooth", Wpf.Ui.Controls.SymbolRegular.Bluetooth20), ("print", Wpf.Ui.Controls.SymbolRegular.Print20), ("lock_screen", Wpf.Ui.Controls.SymbolRegular.LockClosed20), ("sign_in", Wpf.Ui.Controls.SymbolRegular.Person20), ("fast_startup", Wpf.Ui.Controls.SymbolRegular.Flash20), ("developer", Wpf.Ui.Controls.SymbolRegular.Code20), ("pagefile", Wpf.Ui.Controls.SymbolRegular.HardDrive20), ("ntfs", Wpf.Ui.Controls.SymbolRegular.HardDrive20), ("feedback", Wpf.Ui.Controls.SymbolRegular.Chat20), ("maps", Wpf.Ui.Controls.SymbolRegular.Map20), ("font", Wpf.Ui.Controls.SymbolRegular.TextFont20), ("tips", Wpf.Ui.Controls.SymbolRegular.Lightbulb20), ("recommend", Wpf.Ui.Controls.SymbolRegular.Sparkle20), ("standby", Wpf.Ui.Controls.SymbolRegular.Power20), ("insider", Wpf.Ui.Controls.SymbolRegular.Beaker20), ("update", Wpf.Ui.Controls.SymbolRegular.ArrowSync20), ("ncsi", Wpf.Ui.Controls.SymbolRegular.Globe20), ("ipv4", Wpf.Ui.Controls.SymbolRegular.Globe20), ("network", Wpf.Ui.Controls.SymbolRegular.Globe20), ("edge.", Wpf.Ui.Controls.SymbolRegular.Globe20), ("widgets", Wpf.Ui.Controls.SymbolRegular.Apps20), ("remote", Wpf.Ui.Controls.SymbolRegular.Desktop20), ("xbl", Wpf.Ui.Controls.SymbolRegular.XboxController20), ("services.", Wpf.Ui.Controls.SymbolRegular.Wrench20), ("sideload", Wpf.Ui.Controls.SymbolRegular.Apps20), ("encryption", Wpf.Ui.Controls.SymbolRegular.LockClosed20),
    ];

    public static Wpf.Ui.Controls.SymbolRegular For(ISettingRow? row)
    {
        if (row is null) return Wpf.Ui.Controls.SymbolRegular.Settings20;
        var id = row.Id.ToLowerInvariant();
        foreach (var (key, symbol) in Words) if (id.Contains(key, StringComparison.Ordinal)) return symbol;
        return ForCategory(row.CategoryKey);
    }

    public static Wpf.Ui.Controls.SymbolRegular ForCategory(string? categoryKey) => categoryKey switch
    {
        "Explorer" => Wpf.Ui.Controls.SymbolRegular.Folder20,
        "Taskbar" => Wpf.Ui.Controls.SymbolRegular.Navigation20,
        "Privacy" => Wpf.Ui.Controls.SymbolRegular.LockClosed20,
        "Search" => Wpf.Ui.Controls.SymbolRegular.Search20,
        "Appearance" => Wpf.Ui.Controls.SymbolRegular.PaintBrush20,
        "Desktop" => Wpf.Ui.Controls.SymbolRegular.Desktop20,
        "Start" => Wpf.Ui.Controls.SymbolRegular.Home20,
        "Input" => Wpf.Ui.Controls.SymbolRegular.Keyboard20,
        "Apps" => Wpf.Ui.Controls.SymbolRegular.Apps20,
        "Performance" => Wpf.Ui.Controls.SymbolRegular.TopSpeed20,
        "System" => Wpf.Ui.Controls.SymbolRegular.Settings20,
        "Network" => Wpf.Ui.Controls.SymbolRegular.Globe20,
        "Updates" => Wpf.Ui.Controls.SymbolRegular.ArrowSync20,
        "Services" => Wpf.Ui.Controls.SymbolRegular.Wrench20,
        _ => Wpf.Ui.Controls.SymbolRegular.Settings20,
    };
}
