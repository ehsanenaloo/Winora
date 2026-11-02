// SPDX-License-Identifier: GPL-3.0-only
using Winora.Application;

namespace Winora.Desktop;

/// <summary>
/// The text of a failed removal or restore: one sentence that names the app, says in plain words why it failed and what to do (resource keys <c>V2BlFail_*</c>). The Windows error number is
/// added only for an error that has no plainer reason.
/// </summary>
public static class BloatFailureText
{
    public static string StepName(LocalizationService text, string step) => text.Has("V2BlStep_" + step) ? text["V2BlStep_" + step] : text["BloatStep_" + step];

    public static string Format(LocalizationService text, string app, string code, int hresult = 0, IEnumerable<string>? dependents = null, bool partial = false)
    {
        var key = "V2BlFail_" + code.Replace('.', '_');
        var needs = dependents is null ? "" : string.Join(", ", dependents);
        var result = text.Has(key) ? text.Format(key, app, needs) : text.Format("V2BlFail_bloat_failed", app, needs);
        if (partial) result += " " + text.Format("V2BlPartial", app);
        if (hresult != 0 && code is BloatCode.Failed or BloatCode.Unsupported or BloatCode.SystemForbids) result += " " + text.Format("V2BlWindowsCode", $"0x{hresult:X8}");
        return result;
    }
}
