// SPDX-License-Identifier: GPL-3.0-only
using Winora.Domain;
namespace Winora.Application;

/// <summary>Read-only list of the files Winora has set aside. It reads the manifests only; it moves, restores and deletes nothing.</summary>
public sealed class QuarantineDiscovery(IQuarantine quarantine) : IDiscoverySource<QuarantineEntry>
{
    public Task<DiscoveryResult<QuarantineEntry>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            var now = DateTimeOffset.UtcNow;
            try { return new DiscoveryResult<QuarantineEntry>(quarantine.List(), [], now); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var error = ErrorMapper.Classify(e) ?? WinoraError.From(ErrorCode.Unexpected);
                return new DiscoveryResult<QuarantineEntry>([], [new(nameof(QuarantineEntry), error)], now);
            }
        }, cancellationToken);
    }
}
