// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

/// <summary>Where the self-test stopped. Gates means nothing was started and no administrator prompt was shown.</summary>
public enum ElevationSelfTestStage { Gates, Confirmation, Launch, Attestation, Connect, Session, Completed }

/// <summary>
/// Typed result of the channel self-test. <see cref="Code"/> is a stable catalog or outcome code and <see cref="Detail"/> its
/// closed detail token (empty when none). Nothing here is persisted: the self-test records nothing in any journal.
/// </summary>
/// <param name="Succeeded">True only when the helper answered the harmless no-op and the client verified the answer.</param>
/// <param name="LaunchAttempted">True once the administrator launcher was called. False for every gate refusal.</param>
/// <param name="ChannelEstablished">True once the encrypted channel was confirmed (key confirmation in both directions).</param>
public sealed record ElevationSelfTestResult(
    bool Succeeded, string Code, string Detail, ElevationSelfTestStage Stage, bool LaunchAttempted, bool ChannelEstablished, string? GateId = null)
{
    /// <summary>Resource key of the user guidance for <see cref="Code"/> (the catalog rule).</summary>
    public string GuidanceKey => Winora.Domain.ErrorCatalog.ResourceKey(Code);
}

/// <summary>What the host shows before anything is launched.</summary>
/// <param name="BatchDigestShort">First 8 hex characters of the batch digest; the same value is in the visible command line.</param>
/// <param name="DefinitionSetShort">First 8 hex characters of the definition-set hash.</param>
/// <param name="NoConsentPrompt">Windows is configured to elevate without a prompt: the host must say so and ask a second explicit confirmation.</param>
public sealed record ElevationConfirmationRequest(string BatchDigestShort, string DefinitionSetShort, bool NoConsentPrompt);

public enum ElevationConfirmationAnswer { Confirmed, Declined, NonInteractive }

/// <summary>
/// Implemented by hosts. A non-interactive host must answer <see cref="ElevationConfirmationAnswer.NonInteractive"/>.
/// <see cref="ElevationConfirmationAnswer.Confirmed"/> means every confirmation the request needs was given, including the second explicit
/// confirmation when <see cref="ElevationConfirmationRequest.NoConsentPrompt"/> is set.
/// </summary>
public interface IElevationConfirmation
{
    Task<ElevationConfirmationAnswer> ConfirmAsync(ElevationConfirmationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Exercises the elevation channel with a harmless typed no-op and nothing else. It needs the per-machine signed
/// install, evaluates every availability gate before any launch, and writes nothing to the machine, the registry or the journal.
/// </summary>
public interface IElevationSelfTest
{
    Task<ElevationSelfTestResult> RunNoopAsync(CancellationToken cancellationToken);
}
