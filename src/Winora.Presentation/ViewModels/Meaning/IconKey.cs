// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Desktop;

/// <summary>
/// The name of a pictogram, not a font glyph. A view model says which picture it means; Winora.Desktop maps the key to the symbol font of the UI toolkit
/// (<c>IconKeyConverter</c>). A member is named like the symbol it maps to, with the pixel size, so the mapping is by name and a test checks every member.
/// </summary>
public enum IconKey
{
    None, Home24, Settings24, ChevronDown12, ChevronLeft12, ChevronRight12,
    AppGeneric24, Apps24, ArrowClockwise24, ArrowDownload24, ArrowSortDown24, ArrowSortUp24,
    ArrowSync24, ArrowUndo20, Broom24, Bug24, CalendarClock24, CheckmarkCircle16,
    CheckmarkCircle20, ChevronDown16, ChevronLeft16, ChevronLeft20, ChevronRight16, ChevronRight20,
    Code24, Database24, Delete24, DeveloperBoard24, DismissCircle20, Document24,
    DocumentMultiple24, ErrorCircle16, Flash24, Folder24, FolderProhibited24, FolderZip24,
    Games24, Globe24, HardDrive20, History24, Image24, Info16,
    Key24, Laptop24, MusicNote224, PaintBrush24, People16, PersonStar16,
    Play24, PlugConnected24, Prohibited20, Ram20, Rocket24, Search24,
    Shield24, ShieldCheckmark16, ShieldCheckmark24, ShieldLock24, Sparkle24, Stop24,
    Toolbox24, Video24, Warning16, Warning20, WindowDevTools24, Wrench24,
}
