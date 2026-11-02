// SPDX-License-Identifier: GPL-3.0-only
namespace Winora.Application.Activity;

public enum NotificationSeverity { Ok, Info, Warning, Error, Restart }

public enum RunResult { Ok, Warning, Error, Stopped }

public enum RunState { Running, Stopping, Finished }

/// <summary>An action that exists only in this process. It is never stored, so a notification read from disk has none.</summary>
public sealed record ActivityAction(string Key, Action Invoke);

public sealed record ActivityNotification(
    Guid Id, NotificationSeverity Severity, string Text, string PageKey, DateTimeOffset At, bool IsRead, ActivityAction? Action = null);

/// <summary>What starts a run. A null <see cref="Share"/> means the run cannot tell its progress (indeterminate).</summary>
public sealed record RunRequest(string Title, string PageKey, bool ChangesSystem, double? Share = null, Action? Stop = null);

public sealed record RunSnapshot(
    Guid Id, string Title, string PageKey, bool ChangesSystem, double? Share, bool CanStop, RunState State,
    DateTimeOffset StartedAt, RunResult? Result = null, DateTimeOffset? FinishedAt = null, bool Notified = false);

/// <summary>The answer to <see cref="ActivityService.TryStart"/>: the new run, or the system-changing run that is in the way.</summary>
public sealed record RunStartResult(RunSnapshot? Run, RunSnapshot? Blocking)
{
    public bool Started => Run is not null;
}

/// <summary>Where the notification list is kept between starts: one text. The store never trusts what it reads and reports a failed read as null.</summary>
public interface INotificationStore
{
    string? Read();
    /// <summary>Replaces the stored text. Throws IOException when it cannot be written.</summary>
    void Write(string json);
}
