// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop.Controls;

/// <summary>
/// The overview: tabs of facts, what needs attention and recent changes. Slots: header, tab rail, banners, body. The body may keep a maximum width.
/// A page that only hosts tabs whose content is a template of its own (Cleanup) is built on it with <see cref="PageWorkspace.PadBody"/> off.
/// </summary>
public sealed class DashboardPage : PageWorkspace
{
    public override PageSlots Slots => PageSlots.Header | PageSlots.Tabs | PageSlots.Banners | PageSlots.Body;
}

/// <summary>
/// Settings: category tab rail, the toolbar (search, filter chips), one banner slot, grouped sections of rows or cards, and the history of changes below.
/// The choice-group variant (Profiles, Windows Update) fills the body with choice cards instead of switch rows.
/// </summary>
public sealed class SettingsPage : PageWorkspace
{
    public override PageSlots Slots => PageSlots.Header | PageSlots.Tabs | PageSlots.Toolbar | PageSlots.Banners | PageSlots.Body | PageSlots.Footer;
}

/// <summary>
/// List: stat strip and states at the head of the list, the one toolbar, grouped sections of inventory rows that are the one scroll area, and the history below.
/// The selection variant (Preinstalled apps, Free space, Quarantine) adds checkboxes to the rows. A page whose rows are one virtualized list sets BodyScrolls to false: the list is then the scroll area.
/// </summary>
public sealed class ListPage : PageWorkspace
{
    public override PageSlots Slots => PageSlots.Header | PageSlots.Tabs | PageSlots.Toolbar | PageSlots.Banners | PageSlots.Body | PageSlots.Footer;
}

/// <summary>Tool: rows with the exact command of each tool, the one toolbar for the list of a tab (search and count), a result card for the run, one banner slot, and the history of changes below.</summary>
public sealed class ToolPage : PageWorkspace
{
    public override PageSlots Slots => PageSlots.Header | PageSlots.Tabs | PageSlots.Toolbar | PageSlots.Banners | PageSlots.Body | PageSlots.Footer;
}

/// <summary>Report: filter chips and count line in the toolbar, rows grouped by day or by size, and a pager (25 rows a page) at the end of the body. Nothing is changed from a report.</summary>
public sealed class ReportPage : PageWorkspace
{
    public override PageSlots Slots => PageSlots.Header | PageSlots.Toolbar | PageSlots.Banners | PageSlots.Body;
}
