// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application.Activity;

namespace Winora.Desktop;

/// <summary>
/// Where a page registers its long runs. The shell gives it the activity service; a page created alone has none and every handle is a no-op. A run that changes the system is refused
/// while another one runs: the handle then has <see cref="ActivityRun.Started"/> false and the caller must not start its work.
/// </summary>
public sealed class RunSource(string pageKey)
{
    public ActivityService? Service { get; set; }
    public string PageKey { get; set; } = pageKey;

    public ActivityRun Begin(string title, bool changesSystem, Action? stop = null, double? share = null) =>
        Service is null ? ActivityRun.None : Service.Begin(new RunRequest(title, PageKey, changesSystem, share, stop));
}
