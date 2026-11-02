// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Winora.Application;

namespace Winora.Desktop;

/// <summary>Everything a task row shows besides the shared row data. Built by the page; the row only displays it and forwards the user's actions.</summary>
public sealed record TaskRowParts
{
    public required ScheduledTaskItem Item { get; init; }
    public required AppIconLoader Icon { get; init; }
    public required string StateText { get; init; }
    public required Tone StateTone { get; init; }
    public required IReadOnlyList<string> TriggerChips { get; init; }
    public required string CommandLine { get; init; }
    public required string CommandTip { get; init; }
    /// <summary>True when the command line is a program path and arguments; a described action (a Windows component) follows the page direction.</summary>
    public bool CommandIsLtr { get; init; } = true;
    public required string PolicyChip { get; init; }
    public required bool IsOn { get; init; }
    public required string OnText { get; init; }
    public required string OffText { get; init; }
    public required string SwitchName { get; init; }
    public required bool CanSwitch { get; init; }
    public required string SwitchTip { get; init; }
    public required IconKey RunStopSymbol { get; init; }
    public required string RunStopLabel { get; init; }
    public required string RunStopTip { get; init; }
    public required string DeleteLabel { get; init; }
    public required string DeleteTip { get; init; }
    public required string InfoLabel { get; init; }
    public required Func<bool> CanAct { get; init; }
    public required Func<bool> CanRunStop { get; init; }
    public required Func<bool> CanDelete { get; init; }
    public required Func<TaskRow, bool, Task> Switched { get; init; }
    public required Action<TaskRow> RunStop { get; init; }
    public required Action<TaskRow> Delete { get; init; }
    public required Action<TaskRow> Info { get; init; }
}

/// <summary>
/// One task card: icon, name with its state, path, trigger chips, the command line and, on the right, the On/Off switch with its state word, Details, Run or Stop, and Delete.
/// A control that the task, the safety policy or a missing administrator right does not allow stays visible but disabled, and its tooltip says why.
/// </summary>
public sealed class TaskRow : DiscoveryRow
{
    private readonly TaskRowParts parts;
    private bool isOn;
    private readonly bool canSwitch;

    public TaskRow(DiscoveryRow source, TaskRowParts parts) : base(source)
    {
        this.parts = parts; isOn = parts.IsOn; canSwitch = parts.CanSwitch;
        RunStopCommand = new(() => parts.RunStop(this), () => parts.CanRunStop());
        DeleteCommand = new(() => parts.Delete(this), () => parts.CanDelete());
        InfoCommand = new(() => parts.Info(this));
    }


    public ScheduledTaskItem Item => parts.Item;
    public AppIconLoader Icon => parts.Icon;
    public string StateText => parts.StateText;
    public Tone StateTone => parts.StateTone;
    public IReadOnlyList<string> TriggerChips => parts.TriggerChips;
    public bool HasTriggers => parts.TriggerChips.Count > 0;
    public string CommandLine => parts.CommandLine;
    public string CommandTip => parts.CommandTip;
    public bool CommandIsLtr => parts.CommandIsLtr;
    public bool HasCommand => parts.CommandLine.Length > 0;
    public string PolicyChip => parts.PolicyChip;
    public bool HasPolicyChip => parts.PolicyChip.Length > 0;

    public bool CanSwitch => canSwitch && parts.CanAct();
    public string SwitchTip => parts.SwitchTip;
    public string SwitchName => parts.SwitchName;
    public string StatusText => isOn ? parts.OnText : parts.OffText;

    /// <summary>The switch. A change asks the page to review and apply it; when the change is not made the page puts the switch back.</summary>
    public bool IsOn
    {
        get => isOn;
        set
        {
            if (value == isOn) return;
            isOn = value;
            Raise(nameof(IsOn)); Raise(nameof(StatusText));
            _ = parts.Switched(this, value);
        }
    }

    public IconKey RunStopSymbol => parts.RunStopSymbol;
    public string RunStopLabel => parts.RunStopLabel;
    public string RunStopTip => parts.RunStopTip;
    public string DeleteLabel => parts.DeleteLabel;
    public string DeleteTip => parts.DeleteTip;
    public string InfoLabel => parts.InfoLabel;
    public string RunStopName => $"{RunStopLabel}, {Title}";
    public string DeleteName => $"{DeleteLabel}, {Title}";
    public string InfoName => $"{InfoLabel}, {Title}";

    public RelayCommand RunStopCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand InfoCommand { get; }

    public string RowAutomationName => $"{Title}, {parts.StateText}, {StatusText}";

    /// <summary>Puts the switch back to what the task has now.</summary>
    public void Reset()
    {
        if (isOn == parts.IsOn) return;
        isOn = parts.IsOn;
        Raise(nameof(IsOn)); Raise(nameof(StatusText));
    }

    /// <summary>Called when the page starts or ends a change: the buttons and the switch follow.</summary>
    public void Notify()
    {
        RunStopCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged();
        Raise(nameof(CanSwitch));
    }

    private void Raise([CallerMemberName] string? name = null) => RaisePropertyChanged(name);
}
