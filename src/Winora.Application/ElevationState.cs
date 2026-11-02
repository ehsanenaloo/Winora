// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>Whether this process runs with administrator rights (an elevated token). The Desktop app asks for them at start (DEC-IW-021); the command-line tool and tests normally do not have them.</summary>
public interface IElevationState
{
    /// <summary>True when this process may use its administrator features: it has an elevated token and runs from a folder that only administrators can change.</summary>
    bool IsElevated { get; }
    /// <summary>True when the token is elevated but the app runs from a folder that other programs can change, so the administrator features are switched off.</summary>
    bool UnsafeLocation => false;
}
