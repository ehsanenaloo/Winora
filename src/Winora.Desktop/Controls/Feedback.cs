// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>
/// The report after a multi-item change: a headline, three count tiles (applied, already set, failed), the failures with a reason and a next step, Undo all, Copy report and Close.
/// An assertive live region when anything failed, polite otherwise. A report with a failure stays until the person closes it.
/// </summary>
public sealed class ResultReport : Control
{
    static ResultReport()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ResultReport), new FrameworkPropertyMetadata(typeof(ResultReport)));
        FocusableProperty.OverrideMetadata(typeof(ResultReport), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ResultReport), new FrameworkPropertyMetadata(false));
    }

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(ResultReportModel), typeof(ResultReport),
        new FrameworkPropertyMetadata(null, (d, e) => ((ResultReport)d).OnModelChanged((ResultReportModel?)e.NewValue)));
    public ResultReportModel? Model { get => (ResultReportModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ApplyParts(Model);
    }

    private void ApplyParts(ResultReportModel? model)
    {
        if (GetTemplateChild("UndoAll") is UIElement undo) undo.Visibility = model?.HasUndoAll == true ? Visibility.Visible : Visibility.Collapsed;
        if (GetTemplateChild("FailureBlock") is UIElement failures) failures.Visibility = model?.HasFailures == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnModelChanged(ResultReportModel? model)
    {
        ApplyParts(model);
        SetCurrentValue(AutomationProperties.LiveSettingProperty, model?.IsAssertive == true ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (model is not null) { Motion.FadeIn(this); Motion.SlideIn(this, 8); }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ReportPeer(this);

    private sealed class ReportPeer(ResultReport owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(ResultReport);
        protected override string GetNameCore() => owner.Model?.AutomationName ?? AutomationProperties.GetName(owner);
        protected override bool IsControlElementCore() => true;
    }
}

/// <summary>
/// Shows the shell's current toast at the bottom centre of the content area. Success and info go after six seconds; the timer pauses while the pointer or the keyboard focus is
/// on the toast, and it exists only while a toast that goes by itself is showing. Warning, error and "restart needed" stay until closed. A toast never takes keyboard focus.
/// </summary>
public sealed class ToastHost : Control
{
    public static readonly TimeSpan AutoDismiss = TimeSpan.FromSeconds(6);
    private DispatcherTimer? timer;
    private ToastItem? timed;

    static ToastHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToastHost), new FrameworkPropertyMetadata(typeof(ToastHost)));
        FocusableProperty.OverrideMetadata(typeof(ToastHost), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ToastHost), new FrameworkPropertyMetadata(false));
        VisibilityProperty.OverrideMetadata(typeof(ToastHost), new FrameworkPropertyMetadata(Visibility.Collapsed));
    }

    public ToastHost()
    {
        MouseEnter += (_, _) => Pause();
        MouseLeave += (_, _) => Resume();
        GotKeyboardFocus += (_, _) => Pause();
        LostKeyboardFocus += (_, _) => Resume();
        Unloaded += (_, _) => StopTimer();
    }

    public static readonly DependencyProperty CenterProperty = DependencyProperty.Register(nameof(Center), typeof(FeedbackCenter), typeof(ToastHost),
        new FrameworkPropertyMetadata(null, (d, e) => ((ToastHost)d).OnCenterChanged((FeedbackCenter?)e.OldValue, (FeedbackCenter?)e.NewValue)));
    public static readonly DependencyProperty ToastProperty = DependencyProperty.Register(nameof(Toast), typeof(ToastItem), typeof(ToastHost), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty CloseTextProperty = DependencyProperty.Register(nameof(CloseText), typeof(string), typeof(ToastHost), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty RegionNameProperty = DependencyProperty.Register(nameof(RegionName), typeof(string), typeof(ToastHost), new FrameworkPropertyMetadata(""));
    private static readonly DependencyPropertyKey SymbolPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Symbol), typeof(SymbolRegular), typeof(ToastHost), new FrameworkPropertyMetadata(SymbolRegular.Info20));
    private static readonly DependencyPropertyKey TonePropertyKey = DependencyProperty.RegisterReadOnly(nameof(Tone), typeof(ChipTone), typeof(ToastHost), new FrameworkPropertyMetadata(ChipTone.Info));
    public static readonly DependencyProperty SymbolProperty = SymbolPropertyKey.DependencyProperty;
    public static readonly DependencyProperty ToneProperty = TonePropertyKey.DependencyProperty;

    public FeedbackCenter? Center { get => (FeedbackCenter?)GetValue(CenterProperty); set => SetValue(CenterProperty, value); }
    public ToastItem? Toast { get => (ToastItem?)GetValue(ToastProperty); private set => SetValue(ToastProperty, value); }
    public string CloseText { get => (string)GetValue(CloseTextProperty); set => SetValue(CloseTextProperty, value); }
    public string RegionName { get => (string)GetValue(RegionNameProperty); set => SetValue(RegionNameProperty, value); }
    public SymbolRegular Symbol => (SymbolRegular)GetValue(SymbolProperty);
    public ChipTone Tone => (ChipTone)GetValue(ToneProperty);
    public bool TimerRunning => timer is { IsEnabled: true };

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("CloseButton") is System.Windows.Controls.Primitives.ButtonBase close) close.Click += (_, _) => Close();
        actionButton = GetTemplateChild("ActionButton") as System.Windows.Controls.Primitives.ButtonBase;
        if (actionButton is not null) actionButton.Click += async (_, _) => await RunActionAsync();
        ShowAction();
    }

    private System.Windows.Controls.Primitives.ButtonBase? actionButton;

    private void ShowAction()
    {
        if (actionButton is not null) actionButton.Visibility = Toast?.HasAction == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The action link of the toast ("Undo"), or null.</summary>
    public async Task RunActionAsync()
    {
        if (Toast is not { HasAction: true } toast) return;
        Center?.DismissToast();
        await toast.RunActionAsync();
    }

    public void Close() => Center?.DismissToast();

    private void OnCenterChanged(FeedbackCenter? old, FeedbackCenter? center)
    {
        if (old is not null) old.PropertyChanged -= OnCenterProperty;
        if (center is not null) center.PropertyChanged += OnCenterProperty;
        Show(center?.Current);
    }

    private void OnCenterProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FeedbackCenter.Current)) Show(Center?.Current);
    }

    private void Show(ToastItem? item)
    {
        StopTimer();
        Toast = item;
        ShowAction();
        if (item is null) { Visibility = Visibility.Collapsed; return; }
        ToneAndSymbol = item.Kind switch
        {
            ToastKind.Success => (ChipTone.Success, SymbolRegular.CheckmarkCircle20),
            ToastKind.Warning => (ChipTone.Warning, SymbolRegular.Warning20),
            ToastKind.Error => (ChipTone.Error, SymbolRegular.DismissCircle20),
            ToastKind.RestartNeeded => (ChipTone.Warning, SymbolRegular.ArrowClockwise20),
            _ => (ChipTone.Info, SymbolRegular.Info20),
        };
        SetCurrentValue(AutomationProperties.LiveSettingProperty, item.Kind is ToastKind.Warning or ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        var wasHidden = Visibility != Visibility.Visible;
        Visibility = Visibility.Visible;
        if (wasHidden) { Motion.FadeIn(this); Motion.SlideIn(this, 8); }
        if (!item.IsSticky) StartTimer(item);
    }

    private (ChipTone, SymbolRegular) ToneAndSymbol { set { SetValue(TonePropertyKey, value.Item1); SetValue(SymbolPropertyKey, value.Item2); } }

    private void StartTimer(ToastItem item)
    {
        timed = item;
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = AutoDismiss };
        timer.Tick += (_, _) => { StopTimer(); if (Center?.Current == item) Center.DismissToast(); };
        if (!IsMouseOver && !IsKeyboardFocusWithin) timer.Start();
    }

    private void Pause() => timer?.Stop();

    private void Resume()
    {
        if (timer is { IsEnabled: false } && timed is not null && !IsMouseOver && !IsKeyboardFocusWithin) { timer.Stop(); timer.Start(); }
    }

    private void StopTimer() { timer?.Stop(); timer = null; timed = null; }

    protected override AutomationPeer OnCreateAutomationPeer() => new ToastPeer(this);

    private sealed class ToastPeer(ToastHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(ToastHost);
        protected override string GetNameCore() => owner.Toast?.Text ?? owner.RegionName;
        protected override bool IsControlElementCore() => owner.Toast is not null;
    }
}
