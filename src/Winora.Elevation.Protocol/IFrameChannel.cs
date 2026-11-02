// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Elevation.Protocol;

/// <summary>
/// The transport seam (technical-docs/protocol.md 1, D-01): a duplex, ordered byte stream. The named pipe, an in-memory pair and a future
/// service all implement it; nothing above it knows which. Frames are written with exactly one <see cref="WriteAsync"/> call each.
/// </summary>
public interface IFrameChannel
{
    /// <summary>Writes all bytes or throws (IO errors and a closed channel surface as exceptions; the protocol layer converts them).</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    /// <summary>Reads at least one byte, up to the buffer length; returns 0 at end of stream (peer closed).</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    /// <summary>Closes both directions. Idempotent.</summary>
    void Close();
}

/// <summary>Source of the ephemeral key pair and nonces of one handshake. The system implementation uses the platform CSPRNG; tests inject fixed values.</summary>
public interface IHandshakeEntropy
{
    EphemeralKey NewKeyPair();
    Domain.Elevation.Bytes32 NewNonce();
}
