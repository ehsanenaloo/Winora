// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Winora.Desktop;

public partial class FirstRunView : UserControl
{
    private FirstRunViewModel? current;

    public FirstRunView()
    {
        InitializeComponent();
        UpdateBackSymbol();
        DataContextChanged += (_, e) =>
        {
            if (current is not null) current.PropertyChanged -= OnChanged;
            current = e.NewValue as FirstRunViewModel;
            if (current is not null) current.PropertyChanged += OnChanged;
        };
        Unloaded += (_, _) => { if (current is not null) current.PropertyChanged -= OnChanged; };
    }

    /// <summary>After a step change the keyboard focus moves to the title, so a screen reader announces the new step.</summary>
    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FirstRunViewModel.Step) or nameof(FirstRunViewModel.IsActive)) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (IsVisible) FocusTitle(); });
    }

    public bool FocusTitle() => TitleText.IsVisible && TitleText.Focus();

    /// <summary>The arrow of the Back button points to where "back" is: left in a left-to-right page, right in a right-to-left one (a glyph is not mirrored by the flow direction).</summary>
    public static Wpf.Ui.Controls.SymbolRegular BackSymbol(FlowDirection direction) =>
        direction == FlowDirection.RightToLeft ? Wpf.Ui.Controls.SymbolRegular.ArrowRight24 : Wpf.Ui.Controls.SymbolRegular.ArrowLeft24;

    private void UpdateBackSymbol() => BackButton.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = BackSymbol(FlowDirection) };

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FlowDirectionProperty && IsInitialized) UpdateBackSymbol();
    }
}
