// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>Future read-only installation discovery. No foundation implementation or mutation API.</summary>
public interface IUninstallDiscovery
{
    IReadOnlyList<InstalledApplication> Discover(CancellationToken cancellationToken);
}
