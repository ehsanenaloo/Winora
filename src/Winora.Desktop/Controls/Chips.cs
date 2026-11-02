// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Winora.Domain;
using Winora.Domain.Elevation;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>The colour family of a chip. Every tone has an icon and a label; colour is never the only cue.</summary>
public enum ChipTone { Neutral, RiskLow, RiskMedium, RiskHigh, SourceA, SourceB, SourceC, Success, Warning, Error, Info, Restart, Managed, Changed }

/// <summary>What a <see cref="StatusBadge"/> says. Each kind has a fixed tone and icon.</summary>
public enum StatusKind { Neutral, Success, Warning, Error, Info, Restart, Managed, Changed, Pending }

/// <summary>Shared base of the three chips: a pill with an icon and a label. Not focusable; its text is part of the owning row or card.</summary>
public abstract class ChipBase : Control
{
    static ChipBase()
    {
        FocusableProperty.OverrideMetadata(typeof(ChipBase), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ChipBase), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ChipBase), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(ChipBase), new FrameworkPropertyMetadata(SymbolRegular.Empty));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(nameof(Tone), typeof(ChipTone), typeof(ChipBase), new FrameworkPropertyMetadata(ChipTone.Neutral));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public SymbolRegular Symbol { get => (SymbolRegular)GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public ChipTone Tone { get => (ChipTone)GetValue(ToneProperty); protected set => SetValue(ToneProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChipPeer(this);

    private sealed class ChipPeer(ChipBase owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => owner.GetType().Name;
        protected override string GetNameCore() => System.Windows.Automation.AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.Text;
        protected override bool IsControlElementCore() => true;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
        protected override bool IsContentElementCore() => true;
    }
}

/// <summary>Low, Medium or High risk. High is the only chip with the error-circle icon.</summary>
public sealed class RiskChip : ChipBase
{
    static RiskChip() => DefaultStyleKeyProperty.OverrideMetadata(typeof(RiskChip), new FrameworkPropertyMetadata(typeof(RiskChip)));
    public RiskChip() => Apply(RiskLevel.Low);

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(RiskLevel), typeof(RiskChip),
        new FrameworkPropertyMetadata(RiskLevel.Low, (d, e) => ((RiskChip)d).Apply((RiskLevel)e.NewValue)));
    public RiskLevel Level { get => (RiskLevel)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    /// <summary>On a row, a badge says something only when there is a risk to weigh: with this set the chip is collapsed at Low risk.</summary>
    public static readonly DependencyProperty HideWhenLowProperty = DependencyProperty.Register(nameof(HideWhenLow), typeof(bool), typeof(RiskChip),
        new FrameworkPropertyMetadata(false, (d, _) => ((RiskChip)d).UpdateVisibility()));
    public bool HideWhenLow { get => (bool)GetValue(HideWhenLowProperty); set => SetValue(HideWhenLowProperty, value); }

    private void UpdateVisibility()
    {
        if (HideWhenLow) Visibility = Level == RiskLevel.Low ? Visibility.Collapsed : Visibility.Visible;
    }

    public static (ChipTone Tone, SymbolRegular Symbol) Look(RiskLevel level) => level switch
    {
        RiskLevel.Low => (ChipTone.RiskLow, SymbolRegular.CheckmarkCircle16),
        RiskLevel.Medium => (ChipTone.RiskMedium, SymbolRegular.Warning16),
        _ => (ChipTone.RiskHigh, SymbolRegular.ErrorCircle16),
    };

    private void Apply(RiskLevel level) { (Tone, Symbol) = Look(level); UpdateVisibility(); }
}

/// <summary>Source class A (Microsoft), B (vendor or expert) or C (community).</summary>
public sealed class SourceChip : ChipBase
{
    static SourceChip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SourceChip), new FrameworkPropertyMetadata(typeof(SourceChip)));
        // A row without a known source (Power and Network rows) has no text: the chip hides instead of showing a bare icon.
        TextProperty.OverrideMetadata(typeof(SourceChip), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, e) => d.SetCurrentValue(VisibilityProperty, string.IsNullOrWhiteSpace(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible)));   // keeps a Visibility binding of the user
    }
    public SourceChip() { Apply(SourceClass.A); SetCurrentValue(VisibilityProperty, Visibility.Collapsed); }

    public static readonly DependencyProperty SourceClassProperty = DependencyProperty.Register(nameof(SourceClass), typeof(SourceClass), typeof(SourceChip),
        new FrameworkPropertyMetadata(SourceClass.A, (d, e) => ((SourceChip)d).Apply((SourceClass)e.NewValue)));
    public SourceClass SourceClass { get => (SourceClass)GetValue(SourceClassProperty); set => SetValue(SourceClassProperty, value); }

    /// <summary>Compact density: an 18 px circle with the class letter. The full name stays in the accessible name and the tooltip.</summary>
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(SourceChip), new FrameworkPropertyMetadata(false));
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public string Letter => SourceClass.ToString();

    public static (ChipTone Tone, SymbolRegular Symbol) Look(SourceClass source) => source switch
    {
        SourceClass.A => (ChipTone.SourceA, SymbolRegular.ShieldCheckmark16),
        SourceClass.B => (ChipTone.SourceB, SymbolRegular.PersonStar16),
        _ => (ChipTone.SourceC, SymbolRegular.People16),
    };

    private void Apply(SourceClass source) { (Tone, Symbol) = Look(source); }
}

/// <summary>A fact about a row or card: restart needed, managed by policy, changed by Winora, pending, or a plain status.</summary>
public sealed class StatusBadge : ChipBase
{
    static StatusBadge() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(typeof(StatusBadge)));
    public StatusBadge() => Apply(StatusKind.Neutral);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(StatusBadge),
        new FrameworkPropertyMetadata(StatusKind.Neutral, (d, e) => ((StatusBadge)d).Apply((StatusKind)e.NewValue)));
    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>Binding shortcut for view models that describe a badge with a meaning (<see cref="Winora.Desktop.Tone"/>); the look follows from the status kind.</summary>
    public static readonly DependencyProperty StatusToneProperty = DependencyProperty.Register(nameof(StatusTone), typeof(Winora.Desktop.Tone), typeof(StatusBadge),
        new FrameworkPropertyMetadata(Winora.Desktop.Tone.Neutral, (d, e) => ((StatusBadge)d).Kind = KindFor((Winora.Desktop.Tone)e.NewValue)));
    public Winora.Desktop.Tone StatusTone { get => (Winora.Desktop.Tone)GetValue(StatusToneProperty); set => SetValue(StatusToneProperty, value); }

    public static StatusKind KindFor(Winora.Desktop.Tone tone) => tone switch
    {
        Winora.Desktop.Tone.Success => StatusKind.Success,
        Winora.Desktop.Tone.Caution => StatusKind.Warning,
        Winora.Desktop.Tone.Danger => StatusKind.Error,
        Winora.Desktop.Tone.Info => StatusKind.Info,
        _ => StatusKind.Neutral,
    };

    public static (ChipTone Tone, SymbolRegular Symbol) Look(StatusKind kind) => kind switch
    {
        StatusKind.Success => (ChipTone.Success, SymbolRegular.CheckmarkCircle16),
        StatusKind.Warning => (ChipTone.Warning, SymbolRegular.Warning16),
        StatusKind.Error => (ChipTone.Error, SymbolRegular.DismissCircle16),
        StatusKind.Info => (ChipTone.Info, SymbolRegular.Info16),
        StatusKind.Restart => (ChipTone.Restart, SymbolRegular.ArrowClockwise16),
        StatusKind.Managed => (ChipTone.Managed, SymbolRegular.LockClosed16),
        StatusKind.Changed => (ChipTone.Changed, SymbolRegular.CheckmarkCircle16),
        StatusKind.Pending => (ChipTone.Warning, SymbolRegular.Clock16),
        _ => (ChipTone.Neutral, SymbolRegular.Empty),   // a plain fact: no icon (a tag glyph read as "label" and looked odd)
    };

    private void Apply(StatusKind kind) { (Tone, Symbol) = Look(kind); }
}
