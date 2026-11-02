// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.Application;
using Winora.Application.Activity;

namespace Winora.Desktop;

/// <summary>
/// The Cleanup destination: free up space (remove caches), the disk usage map, and the list of files Winora has set aside. Each part is a tab; leaving the page stops a running scan or removal.
/// </summary>
public sealed class CleanupViewModel : PageViewModel
{
    public const int FreeSpaceTab = 0, DiskUsageTab = 1, SetAsideTab = 2;
    private int selectedTab;

    public CleanupViewModel(LocalizationService text, SimulationModel model, FreeSpaceViewModel freeSpace, DiskUsageViewModel diskUsage, QuarantineViewModel setAside) : base("Cleanup", text, model)
    {
        FreeSpace = freeSpace; DiskUsage = diskUsage; SetAside = setAside;
        // The selected categories of Free space are the staged changes of this page.
        freeSpace.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FreeSpaceViewModel.SelectedCategories) or nameof(FreeSpaceViewModel.SelectionBarText) or nameof(FreeSpaceViewModel.SelectedTitles) or "") OnPropertyChanged(nameof(PendingCount)); };
    }

    public override int PendingCount => FreeSpace.SelectedCategories;
    public override StagingView? Staging => PendingCount == 0 ? null : new(Key,
        FreeSpace.SelectedRows.Select(r => new StagedChange(r.Category.Id.ToString(), r.Title, "", "", r.Risk, () => { if (FreeSpace.CanChangeSelection) r.IsSelected = false; })).ToList(),
        FreeSpace.SelectionBarText, Text["FreeSpaceClean"], Text["FreeSpaceCleanHelp"], Text["FreeSpaceClearSelection"], Text["FreeSpaceClearSelectionHelp"],
        () => FreeSpace.CleanCommand.CanExecute(null), () => FreeSpace.CleanCommand.Execute(null), () => FreeSpace.ClearSelectionCommand.CanExecute(null), () => FreeSpace.ClearSelectionCommand.Execute(null));

    public FreeSpaceViewModel FreeSpace { get; }
    public DiskUsageViewModel DiskUsage { get; }
    public QuarantineViewModel SetAside { get; }

    public int SelectedTab
    {
        get => selectedTab;
        set { if (SetProperty(ref selectedTab, value)) { if (value == SetAsideTab) SetAside.Activate(); OnPropertyChanged(nameof(IsFreeSpaceSelected)); OnPropertyChanged(nameof(IsDiskUsageSelected)); OnPropertyChanged(nameof(IsSetAsideSelected)); } }
    }

    public bool IsFreeSpaceSelected => selectedTab == FreeSpaceTab;
    public bool IsDiskUsageSelected => selectedTab == DiskUsageTab;
    public bool IsSetAsideSelected => selectedTab == SetAsideTab;
    public string FreeSpaceTabTitle => Text["FreeSpace"];
    public string DiskUsageTabTitle => Text["DiskUsage"];
    public string SetAsideTabTitle => Text["SetAside"];

    public override void Activate() { FreeSpace.RefreshDisks(); if (selectedTab == SetAsideTab) SetAside.Activate(); }
    public override void Deactivate() { FreeSpace.Stop(); DiskUsage.Stop(); SetAside.Deactivate(); }
    public override Task WhenIdleAsync() => Task.WhenAll(FreeSpace.WhenIdleAsync(), DiskUsage.WhenIdleAsync(), SetAside.WhenIdleAsync());

    /// <summary>F5: scan again on the map tab, read the list again on the other tab.</summary>
    public override System.Windows.Input.ICommand? ShortcutRefresh() => selectedTab == FreeSpaceTab ? FreeSpace.ScanCommand : selectedTab == DiskUsageTab ? DiskUsage.ScanCommand : SetAside.ShortcutRefresh();
}
