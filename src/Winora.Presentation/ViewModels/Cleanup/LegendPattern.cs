// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;

namespace Winora.Desktop;

/// <summary>The line pattern that goes with a file type, so the map and its legend never rely on colour alone.</summary>
public enum LegendPattern { Solid, Horizontal, Vertical, DiagonalUp, DiagonalDown, Cross, DiagonalCross, Dots, SparseDots, Checker, DenseCross }

/// <summary>Which line pattern goes with which file type.</summary>
public static class LegendPatterns
{
    public static LegendPattern For(DiskFileKind kind) => kind switch
    {
        DiskFileKind.Documents => LegendPattern.Horizontal,
        DiskFileKind.Images => LegendPattern.DiagonalUp,
        DiskFileKind.Video => LegendPattern.DiagonalDown,
        DiskFileKind.Audio => LegendPattern.Dots,
        DiskFileKind.Archives => LegendPattern.Cross,
        DiskFileKind.Code => LegendPattern.Vertical,
        DiskFileKind.Programs => LegendPattern.Checker,
        DiskFileKind.SystemData => LegendPattern.DiagonalCross,
        DiskFileKind.Unlisted => LegendPattern.SparseDots,
        DiskFileKind.Unreadable => LegendPattern.DenseCross,
        _ => LegendPattern.Solid,
    };
}
