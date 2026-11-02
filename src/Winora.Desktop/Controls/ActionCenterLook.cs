// SPDX-License-Identifier: GPL-3.0-only
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Winora.Desktop.Controls;

/// <summary>The one mapping from the meaning of a notification to its icon and color. The view models only know the meaning.</summary>
public static class ActionCenterLook
{
    public static SymbolRegular SymbolOf(NoteLevel level) => level switch
    {
        NoteLevel.Ok => SymbolRegular.CheckmarkCircle20,
        NoteLevel.Warning => SymbolRegular.Warning20,
        NoteLevel.Error => SymbolRegular.DismissCircle20,
        NoteLevel.Restart => SymbolRegular.ArrowClockwise20,
        _ => SymbolRegular.Info20,
    };

    public static string BrushKeyOf(NoteLevel level) => level switch
    {
        NoteLevel.Ok => "WinoraStatusSuccessForeground",
        NoteLevel.Warning => "WinoraStatusWarningForeground",
        NoteLevel.Error => "WinoraStatusErrorForeground",
        NoteLevel.Restart => "WinoraRestartBadgeForeground",
        _ => "WinoraStatusInfoForeground",
    };

    public static IValueConverter Symbol { get; } = new LevelConverter(false);
    public static IValueConverter Brush { get; } = new LevelConverter(true);

    private sealed class LevelConverter(bool brush) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var level = value is NoteLevel l ? l : NoteLevel.Info;
            if (!brush) return SymbolOf(level);
            return System.Windows.Application.Current?.TryFindResource(BrushKeyOf(level)) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary>
/// A bar for a run. It animates only while the run cannot tell its share, the bar is on screen and Windows animations are on: a collapsed bar keeps no clock running.
/// </summary>
public sealed class ActivityBar : System.Windows.Controls.ProgressBar
{
    public static readonly DependencyProperty IsWorkingProperty = DependencyProperty.Register(nameof(IsWorking), typeof(bool), typeof(ActivityBar), new FrameworkPropertyMetadata(false, (d, _) => ((ActivityBar)d).Update()));

    public ActivityBar() { IsVisibleChanged += (_, _) => Update(); }

    /// <summary>True while the run has no share to show.</summary>
    public bool IsWorking { get => (bool)GetValue(IsWorkingProperty); set => SetValue(IsWorkingProperty, value); }

    private void Update() => IsIndeterminate = IsWorking && IsVisible && Motion.Enabled;
}
