// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
namespace Winora.Cli;

/// <summary>Used only with the hidden --sandbox-root test option: reports success without broadcasting or restarting anything.</summary>
internal sealed class SandboxShellRefresh : IShellRefresh
{
    public Task<ShellRefreshResult> RefreshAsync(ShellRefreshKind kind, CancellationToken cancellationToken) => Task.FromResult(new ShellRefreshResult(true, "operation.applied"));
}
