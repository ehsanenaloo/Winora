// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;
using Winora.Application.Activity;
using Winora.Domain;

namespace Winora.Desktop;

/// <summary>
/// The restore point buttons of Overview and first run: create one now (one confirmation, one journal line through <see cref="RestorePointService"/>) and open the Windows
/// System Protection settings. Winora never turns System Protection on by itself.
/// </summary>
public sealed class RestorePointActions(LocalizationService text, RestorePointService? service, IReviewPrompt prompt, IWindowsSettingsLauncher launcher, Func<FeedbackCenter?> feedback, RunSource? runs = null)
{
    private int running;

    public bool CanCreate => service is not null;

    /// <summary>Asks once, creates the restore point and tells the person what happened. Returns the result, or null when nothing was created (cancelled, busy or no service).</summary>
    public async Task<RestorePointResult?> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (service is null || Interlocked.Exchange(ref running, 1) == 1) return null;
        try
        {
            var confirmation = new ActionConfirmation(text["RestorePointCreateTitle"], text["RestorePointCreateBody"], [], text["RestorePointCreateNote"], text["RestorePointCreatePrimary"])
            {
                Risk = RiskLevel.Low, Restart = false,
            };
            if (!await prompt.ConfirmActionAsync(confirmation).ConfigureAwait(true)) return null;
            using var run = runs?.Begin(text["ACRunRestorePoint"], true) ?? ActivityRun.None;
            if (!run.Started) return null;
            try
            {
                var result = await service.CreateAsync("started by the user", cancellationToken).ConfigureAwait(true);
                Report(result);
                run.Finish(result.Ok ? RunResult.Ok : RunResult.Warning, text["ACRunRestorePoint"]);
                return result;
            }
            catch (Exception) { run.Finish(RunResult.Error, text["ACRunRestorePoint"]); throw; }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception e) when (e is not OutOfMemoryException) { feedback()?.Error(text["ActionFailedUnexpected"]); return null; }
        finally { Interlocked.Exchange(ref running, 0); }
    }

    private void Report(RestorePointResult result)
    {
        var center = feedback();
        if (center is null) return;
        var line = RestorePointText.Notice(text, new RestorePointGateResult(true, result, true)) ?? text["ActionFailedUnexpected"];
        if (result.Ok) center.Success(line); else center.Warning(line);
    }

    public void OpenProtection()
    {
        if (!launcher.OpenSystemProtection()) feedback()?.Warning(text["SystemProtectionOpenFailed"]);
    }

    /// <summary>Winora asks for administrator rights in its manifest and has no way to restart itself with them. This says where to start it.</summary>
    public void ShowAdminHelp() => feedback()?.Warning(text["AdminHowToStart"]);
}
