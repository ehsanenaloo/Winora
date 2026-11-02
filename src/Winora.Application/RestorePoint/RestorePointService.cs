// SPDX-License-Identifier: GPL-3.0-only
using System.Text;
namespace Winora.Application;

/// <summary>
/// Creates a Windows restore point before Winora changes the machine and reports what Windows allows right now. It never turns System Protection on, never changes the creation limit and
/// never starts another program. Every attempt is written to the journal before it is made.
/// </summary>
public sealed class RestorePointService(IRestorePointPort port, IRestorePointJournal journal, IElevationState elevation, TimeProvider? clock = null)
{
    /// <summary>Windows creates at most one restore point per 24 hours unless the SystemRestorePointCreationFrequency value says otherwise.</summary>
    public const int DefaultFrequencyMinutes = 1440;
    public const int MaxDescriptionLength = 255;
    public const string DescriptionPrefix = "Winora: ";

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);

    public RestorePointStatus Status()
    {
        var now = clock.GetUtcNow();
        var configured = Read(port.ReadCreationFrequencyMinutes, null);
        var minutes = configured is >= 0 ? configured.Value : DefaultFrequencyMinutes;
        var attempts = Read(() => journal.History(50), []);
        var own = attempts.Where(a => a is { Ok: true, MayBeReused: false, Sequence: > 0, EndedUtc: not null })
            .Select(a => new RestorePointInfo(a.Sequence, a.Description, a.EndedUtc!.Value)).DistinctBy(p => p.Sequence);
        var points = own.OrderByDescending(p => p.CreatedUtc).ToList();
        DateTimeOffset? last = points.Count > 0 ? points[0].CreatedUtc : null;
        var limited = minutes > 0 && last is { } at && now - at < TimeSpan.FromMinutes(minutes);
        return new(Read(port.ReadProtection, RestoreProtection.Unknown), minutes, configured is null, last, limited, elevation.IsElevated, points, attempts);
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception) { return fallback; }
    }

    public async Task<RestorePointResult> CreateAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (!elevation.IsElevated) return new(false, RestorePointCode.NeedsAdministrator, 0, false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => Create(Describe(reason)), CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>The text Windows shows in its restore point list: a fixed prefix, the reason without control characters, at most 255 characters.</summary>
    public static string Describe(string? reason)
    {
        var builder = new StringBuilder();
        foreach (var c in reason ?? "") builder.Append(char.IsControl(c) ? ' ' : c);
        var body = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var text = DescriptionPrefix + (body.Length == 0 ? "before changes" : body);
        if (text.Length > MaxDescriptionLength) text = text[..MaxDescriptionLength];
        if (char.IsHighSurrogate(text[^1])) text = text[..^1];
        return text;
    }

    private RestorePointResult Create(string description)
    {
        var status = Status();
        if (status.Protection == RestoreProtection.Disabled) return new(false, RestorePointCode.ProtectionOff, 0, false);
        var id = Guid.NewGuid();
        var journaled = true;
        try { journal.Begin(id, description, clock.GetUtcNow()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { journaled = false; }

        RestorePointNativeResult native;
        try { native = port.Create(description); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { native = new(false, 0, 0); }

        RestorePointResult result;
        if (!native.Called) result = new(false, RestorePointCode.ApiUnavailable, 0, false);
        else if (native.Status != 0) result = new(false, RestorePointCode.FromStatus(native.Status), 0, false);
        else if (native.Sequence <= 0) result = new(false, RestorePointCode.Failed, 0, false);
        else result = new(true, RestorePointCode.Ok, native.Sequence, status.ThrottleLikelyActive);

        if (journaled)
        {
            try { journal.Complete(id, result.Ok, result.Code, result.Sequence, result.MayBeReused, clock.GetUtcNow()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the attempt shows as interrupted */ }
        }
        return result;
    }
}
