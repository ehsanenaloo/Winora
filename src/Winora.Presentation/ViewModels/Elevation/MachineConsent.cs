// SPDX-License-Identifier: GPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using Winora.Application;

namespace Winora.Desktop;

/// <summary>
/// Everything the consent window shows for ONE administrator-level change: what changes, for whom, from what to what,
/// what Winora writes back to undo it, whether a restart is needed, which trusted list it comes from and the batch code that is also on the Windows prompt.
/// Plain data: the host fills it from the executor's plan and the gate evaluation.
/// </summary>
/// <param name="Title">Already localized name of the setting.</param>
/// <param name="BeforeText">Current value as text.</param>
/// <param name="AfterText">New value as text.</param>
/// <param name="RestoreText">Value Winora writes back to undo the change.</param>
/// <param name="BatchDigestShort">First 8 hex characters of the batch digest; the same code is in the visible command line of the helper.</param>
/// <param name="PublisherText">Publisher the Windows prompt will show (or a short note when the build is unsigned).</param>
/// <param name="NoConsentPrompt">Windows is set to elevate without asking: a second explicit confirmation is required.</param>
public sealed record MachineConsentRequest(
    string DefinitionId, int DefinitionVersion, string Title, string BeforeText, string AfterText, string RestoreText, bool RequiresRestart,
    long SetSerial, string SetShort, string BatchDigestShort, string PublisherText, bool NoConsentPrompt);

/// <summary>
/// The consent window's model. Rules enforced here: the default answer is Cancel (the dialog host sets the default button to Close), there is no timer and no automatic
/// acceptance, and when Windows will not prompt the primary button stays disabled until the person ticks the second confirmation.
/// </summary>
public sealed class MachineConsentModel(MachineConsentRequest request, LocalizationService text) : ObservableObject
{
    private bool secondConfirmed;

    public MachineConsentRequest Request { get; } = request;
    public LocalizationService Text { get; } = text;

    public string Title => Text["MachineConsentTitle"];
    public string Intro => Text["MachineConsentIntro"];
    public string SettingName => Request.Title;
    public string ScopeText => Text["MachineConsentScope"];
    public string BeforeLabel => Text["BeforeLabel"];
    public string AfterLabel => Text["AfterLabel"];
    public string BeforeText => Request.BeforeText;
    public string AfterText => Request.AfterText;
    public string Arrow => Text["ArrowGlyph"];
    public string RestoreText => Text.Format("MachineConsentRestore", Request.RestoreText);
    public bool HasRestartNote => Request.RequiresRestart;
    public string RestartNote => Text["MachineConsentRestart"];
    public string SourceText => Text.Format("MachineConsentSource", Request.DefinitionId, Request.DefinitionVersion, Request.SetSerial, Request.SetShort);
    public string DigestLabel => Text["MachineConsentDigestLabel"];
    public string DigestText => Request.BatchDigestShort;
    public string DigestHelp => Text["MachineConsentDigestHelp"];
    public string PublisherText => Text.Format("MachineConsentPublisher", Request.PublisherText);
    public bool ShowNoPromptWarning => Request.NoConsentPrompt;
    public string NoPromptWarning => Text["MachineConsentNoPromptWarning"];
    public string SecondConfirmLabel => Text["MachineConsentSecondConfirm"];
    public string PrimaryText => Text["MachineConsentContinue"];
    public string CancelText => Text["MachineConsentCancel"];

    public bool SecondConfirmed
    {
        get => secondConfirmed;
        set { if (SetProperty(ref secondConfirmed, value)) OnPropertyChanged(nameof(CanConfirm)); }
    }

    /// <summary>The primary button is enabled only when no second confirmation is needed or it was given.</summary>
    public bool CanConfirm => !Request.NoConsentPrompt || secondConfirmed;

    /// <summary>The confirmation the executor takes: the digest the person saw, an interactive host, and the second confirmation when it was needed.</summary>
    public ElevationConfirmation ToConfirmation() => new(Request.BatchDigestShort, true, Request.NoConsentPrompt && secondConfirmed);
}

/// <summary>Implemented by the main window: shows the consent window and returns the confirmation, or null when the person cancelled.</summary>
public interface IMachineConsentPrompt
{
    Task<ElevationConfirmation?> ConfirmMachineChangeAsync(MachineConsentRequest request);
}
