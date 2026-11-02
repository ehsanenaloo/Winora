// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text;
namespace Winora.Application;

/// <summary>
/// One resource of a recovery restore: what it holds now and what the restore would put there. The texts are neutral (a setting id, a service or task name, a value as the journal keeps it);
/// the window turns them into its own words.
/// </summary>
public sealed record RecoveryLine(string Resource, string Current, string RestoreTo);

/// <summary>
/// What a restore of the value from before would do, planned from the live state. <see cref="Token"/> names that state: the restore is made only while the live state still gives the same token,
/// so what the person confirmed is what is written. A change in between ends the restore as stale and needs a new review.
/// </summary>
public sealed record RecoveryPlan(IReadOnlyList<RecoveryLine> Lines, string Token, bool NeedsRestart = false);

internal static class RecoveryToken
{
    /// <summary>A SHA-256 over the parts, each written as text with a separator that cannot occur inside a part.</summary>
    public static string Of(params object?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => p?.ToString() ?? "\u0000")))));
}
