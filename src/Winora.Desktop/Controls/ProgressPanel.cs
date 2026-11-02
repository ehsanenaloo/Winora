// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Winora.Desktop.Controls;

/// <summary>
/// A running job: a title, a determinate bar when the count is known (otherwise indeterminate), the current step ("2 of 3: Show file extensions"), the elapsed time after five
/// seconds and Cancel. A step that cannot stop shows Cancel disabled with the reason in text. The clock runs only while the panel is visible and running. When Windows animations
/// are off the bar is never indeterminate: the text says "Working" and the bar stays still.
/// </summary>
public sealed class ProgressPanel : Control
{
    /// <summary>Elapsed time is shown only after this long.</summary>
    public static readonly TimeSpan ElapsedAfter = TimeSpan.FromSeconds(5);

    private DispatcherTimer? timer;
    private DateTime startedUtc;

    static ProgressPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ProgressPanel), new FrameworkPropertyMetadata(typeof(ProgressPanel)));
        FocusableProperty.OverrideMetadata(typeof(ProgressPanel), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(ProgressPanel), new FrameworkPropertyMetadata(false));
    }

    public ProgressPanel()
    {
        IsVisibleChanged += (_, _) => UpdateClock();
        Unloaded += (_, _) => StopClock();
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ProgressPanel), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty StepTextProperty = DependencyProperty.Register(nameof(StepText), typeof(string), typeof(ProgressPanel), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressPanel), new FrameworkPropertyMetadata(0.0));
    public static readonly DependencyProperty IsDeterminateProperty = DependencyProperty.Register(nameof(IsDeterminate), typeof(bool), typeof(ProgressPanel), new FrameworkPropertyMetadata(false, (d, _) => ((ProgressPanel)d).UpdateIndeterminate()));
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ProgressPanel), new FrameworkPropertyMetadata(true, (d, _) => ((ProgressPanel)d).UpdateClock()));
    public static readonly DependencyProperty CancelTextProperty = DependencyProperty.Register(nameof(CancelText), typeof(string), typeof(ProgressPanel), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty CancelHelpProperty = DependencyProperty.Register(nameof(CancelHelp), typeof(string), typeof(ProgressPanel), new FrameworkPropertyMetadata(""));
    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(ProgressPanel), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty CancelDisabledReasonProperty = DependencyProperty.Register(nameof(CancelDisabledReason), typeof(string), typeof(ProgressPanel), new FrameworkPropertyMetadata("", (d, e) => d.SetValue(HasCancelReasonPropertyKey, !string.IsNullOrEmpty((string?)e.NewValue))));
    public static readonly DependencyProperty ElapsedFormatProperty = DependencyProperty.Register(nameof(ElapsedFormat), typeof(string), typeof(ProgressPanel), new FrameworkPropertyMetadata("{0}"));
    private static readonly DependencyPropertyKey ElapsedTextPropertyKey = DependencyProperty.RegisterReadOnly(nameof(ElapsedText), typeof(string), typeof(ProgressPanel), new FrameworkPropertyMetadata(""));
    private static readonly DependencyPropertyKey HasElapsedPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasElapsed), typeof(bool), typeof(ProgressPanel), new FrameworkPropertyMetadata(false));
    private static readonly DependencyPropertyKey HasCancelReasonPropertyKey = DependencyProperty.RegisterReadOnly(nameof(HasCancelReason), typeof(bool), typeof(ProgressPanel), new FrameworkPropertyMetadata(false));
    private static readonly DependencyPropertyKey BarIndeterminatePropertyKey = DependencyProperty.RegisterReadOnly(nameof(BarIndeterminate), typeof(bool), typeof(ProgressPanel), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty ElapsedTextProperty = ElapsedTextPropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasElapsedProperty = HasElapsedPropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasCancelReasonProperty = HasCancelReasonPropertyKey.DependencyProperty;
    public static readonly DependencyProperty BarIndeterminateProperty = BarIndeterminatePropertyKey.DependencyProperty;

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    /// <summary>"2 of 3: Show file extensions". A polite live region.</summary>
    public string StepText { get => (string)GetValue(StepTextProperty); set => SetValue(StepTextProperty, value); }
    /// <summary>0 to 1 when <see cref="IsDeterminate"/>.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsDeterminate { get => (bool)GetValue(IsDeterminateProperty); set => SetValue(IsDeterminateProperty, value); }
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public string CancelText { get => (string)GetValue(CancelTextProperty); set => SetValue(CancelTextProperty, value); }
    public string CancelHelp { get => (string)GetValue(CancelHelpProperty); set => SetValue(CancelHelpProperty, value); }
    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }
    /// <summary>When set, Cancel is disabled and this text says why ("This step cannot be stopped").</summary>
    public string CancelDisabledReason { get => (string)GetValue(CancelDisabledReasonProperty); set => SetValue(CancelDisabledReasonProperty, value); }
    /// <summary>String format for the elapsed time, for example "{0} elapsed". The argument is "m:ss".</summary>
    public string ElapsedFormat { get => (string)GetValue(ElapsedFormatProperty); set => SetValue(ElapsedFormatProperty, value); }
    public string ElapsedText => (string)GetValue(ElapsedTextProperty);
    public bool HasElapsed => (bool)GetValue(HasElapsedProperty);
    public bool HasCancelReason => (bool)GetValue(HasCancelReasonProperty);
    /// <summary>True when the bar animates: the count is unknown and Windows animations are on.</summary>
    public bool BarIndeterminate => (bool)GetValue(BarIndeterminateProperty);
    public bool ClockRunning => timer is { IsEnabled: true };

    private void UpdateIndeterminate() => SetValue(BarIndeterminatePropertyKey, !IsDeterminate && Motion.Enabled && IsVisible && IsRunning);

    private void UpdateClock()
    {
        UpdateIndeterminate();
        if (IsVisible && IsRunning) StartClock(); else StopClock();
    }

    private void StartClock()
    {
        if (timer is { IsEnabled: true }) return;
        startedUtc = DateTime.UtcNow;
        SetValue(HasElapsedPropertyKey, false);
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
    }

    private void StopClock()
    {
        timer?.Stop(); timer = null;
        SetValue(HasElapsedPropertyKey, false);
        SetValue(BarIndeterminatePropertyKey, false);
    }

    private void Tick()
    {
        var elapsed = DateTime.UtcNow - startedUtc;
        if (elapsed < ElapsedAfter) return;
        var clock = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        // Persian pages show every number with Persian digits.
        if (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa") clock = DigitConverter.ToPersian(clock);
        SetValue(ElapsedTextPropertyKey, string.Format(ElapsedFormat, clock));
        SetValue(HasElapsedPropertyKey, true);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PanelPeer(this);

    private sealed class PanelPeer(ProgressPanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(ProgressPanel);
        protected override string GetNameCore() => AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : owner.Title;
        protected override bool IsControlElementCore() => true;
    }
}
