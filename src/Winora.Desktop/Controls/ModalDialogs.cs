// SPDX-License-Identifier: GPL-3.0-only
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace Winora.Desktop.Controls;

/// <summary>
/// Shows the dialogs of the window one at a time, tracks that a dialog is open (global shortcuts are ignored then, so the page behind a dialog cannot be driven) and gives keyboard focus
/// back to the control that opened it. When to ask again and how high a dialog may grow come from <see cref="DialogRules"/>.
/// </summary>
public sealed class ModalDialogs(FrameworkElement owner, ContentDialogHost host, Func<double> contentScale, Func<TextDirection> direction, Func<bool> focusPageHeading)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int depth;

    /// <summary>True while any dialog is open.</summary>
    public bool IsOpen => depth > 0;

    public UIElement? CurrentFocus() => (FocusManager.GetFocusedElement(owner) as UIElement) ?? Keyboard.FocusedElement as UIElement;

    public Task<ContentDialogResult> ShowAsync(SimpleContentDialogCreateOptions options) =>
        ShowAsync(() => new ContentDialog(host)
        {
            Title = options.Title, Content = options.Content, PrimaryButtonText = options.PrimaryButtonText ?? "",
            SecondaryButtonText = options.SecondaryButtonText ?? "", CloseButtonText = options.CloseButtonText ?? "",
        });

    public async Task<ContentDialogResult> ShowAsync(Func<ContentDialog> create)
    {
        // One dialog at a time. A dialog that is asked for right after another one closed (the restore point question after the confirmation) can find the host still closing and
        // come back at once without being shown: that answer is retried after a short wait, so it is never taken for a choice of the user.
        await gate.WaitAsync();
        var invoker = CurrentFocus();
        depth++;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var dialog = create();
                // The dialog scrolls its own content; it never grows past the window, so a long review or long details keep one scroll bar inside the window.
                dialog.DialogMaxHeight = DialogRules.MaxHeight(owner.ActualHeight, contentScale());
                var opened = false;
                dialog.Opened += (_, _) => { opened = true; AccessKeys.ForDialog(dialog, direction() == TextDirection.LeftToRight); };
                var result = await dialog.ShowAsync(CancellationToken.None);
                if (!DialogRules.ShouldAskAgain(result == ContentDialogResult.None, opened, attempt)) return result;
                await Task.Delay(250);
            }
        }
        finally { depth--; gate.Release(); RestoreFocus(invoker); }
    }

    private void RestoreFocus(UIElement? invoker)
    {
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (invoker is { IsVisible: true, IsEnabled: true, Focusable: true } && PresentationSource.FromVisual(invoker) is not null && invoker.Focus()) return;
            focusPageHeading();
        });
    }
}
