// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application;

internal static class JournalGuard
{
    /// <summary>Writes the end line of a change that is already made. One more try after a failed write; true when the line is lost, so the caller can tell the person that the history shows the change as interrupted.</summary>
    public static bool CompleteLost(Action write)
    {
        try { write(); return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { write(); return false; }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { return true; }
        }
    }
}
