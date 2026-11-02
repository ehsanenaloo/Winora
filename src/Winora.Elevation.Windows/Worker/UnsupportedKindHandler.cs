// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
using Winora.Domain.Elevation;
using Winora.Elevation.Protocol;

namespace Winora.Elevation.Windows.Worker;

/// <summary>
/// Placeholder for a definition whose kind the worker does not execute (a display driver value, whose key only the administrator Desktop app resolves). It makes the session end
/// the batch with <c>target.kind_unsupported</c> before any Prepare. It reads and writes nothing.
/// </summary>
public sealed class UnsupportedKindHandler : IWorkerHandler
{
    private readonly MachineSettingDefinition definition;

    public UnsupportedKindHandler(MachineSettingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        this.definition = definition;
    }

    public string DefinitionId => definition.Id;
    public uint Version => (uint)definition.Version;
    public bool SupportsRestore => false;
    public bool AcceptsChoice(string choiceId) => definition.Choices.Any(c => c.Id == choiceId);
    public string? UnsupportedCode => ErrorCode.TargetKindUnsupported;

    public HandlerObservation Observe(BatchItem item) => throw new InvalidOperationException("the session never prepares an unsupported kind");
    public HandlerPlan Decide(BatchItem item, HandlerObservation observed) => throw new InvalidOperationException("the session never prepares an unsupported kind");
    public HandlerApplied Apply(BatchItem item, HandlerObservation prepared) => throw new InvalidOperationException("the session never applies an unsupported kind");
}
