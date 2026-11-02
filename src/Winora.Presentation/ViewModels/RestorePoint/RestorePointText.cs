// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;

namespace Winora.Desktop;

/// <summary>What the restore point question shows. A null <see cref="Primary"/> means Winora cannot create a restore point now, so only "continue" and "cancel" are offered.</summary>
public sealed record RestorePointDialog(string Title, string Body, IReadOnlyList<string> Notes, string? Primary, string Secondary, string Cancel);

/// <summary>The words of the restore point policy, shared by every page that asks.</summary>
public static class RestorePointText
{
    public static RestorePointDialog Dialog(LocalizationService text, RestorePointAsk ask)
    {
        var notes = new List<string>();
        var status = ask.Status;
        string body;
        if (ask.CanCreate)
        {
            body = text["RpBody"];
            if (status.ThrottleLikelyActive && status.LastCreatedUtc is { } last)
                notes.Add(text.Format("RpThrottle", text.Number(status.ThrottleMinutes), text.When(last.ToLocalTime(), "g")));
        }
        else
        {
            body = text[!status.CanCreate ? "RpCannotAdmin" : "RpCannotPolicy"];
        }
        notes.Add(text["RpWhereToEnable"]);
        return new(text["RpTitle"], body, notes, ask.CanCreate ? text["RpPrimary"] : null, text["RpSecondary"], text["RpCancel"]);
    }

    /// <summary>The restore point line of a confirmation: what exists, and whether Winora asks again in this session.</summary>
    public static string StatusLine(LocalizationService text, RestorePointStatus status, bool alreadyDecided)
    {
        if (status.Protection == RestoreProtection.Disabled) return text["RpLineOff"];
        var last = status.LastCreatedUtc is { } at ? text.Format("RpLineLast", text.When(at.ToLocalTime(), "g")) : text["RpLineNone"];
        return last + " " + text[alreadyDecided ? "RpLineDecided" : "RpLineAsk"];
    }

    /// <summary>
    /// The restore point line of a confirmation, read off the calling thread: it reads Windows and the dialog never waits for it. Null when there is no restore point service or the
    /// read failed; the dialog then shows no line.
    /// </summary>
    public static async Task<string?> LoadStatusLineAsync(LocalizationService text, RestorePointService? service, IRestorePointGate? gate)
    {
        if (service is null) return null;
        RestorePointStatus status;
        try { status = await Task.Run(service.Status); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        return StatusLine(text, status, gate is SessionRestorePointGate { HasDecided: true });
    }

    public static string Reason(LocalizationService text, string code)
    {
        var key = "RpCode_" + code.Replace('.', '_');
        return text.Has(key) ? text[key] : text["RpCode_restore_failed"];
    }

    /// <summary>The line that goes with the outcome of the change: what the restore point step did. Null when it did nothing.</summary>
    public static string? Notice(LocalizationService text, RestorePointGateResult result) => result.Created switch
    {
        null => null,
        { Ok: true, MayBeReused: true } c => text.Format("RpMaybeReused", c.Sequence),
        { Ok: true } c => text.Format("RpCreated", c.Sequence),
        var c => text.Format("RpFailed", Reason(text, c.Code)),
    };
}
