<!-- SPDX-License-Identifier: GPL-3.0-only -->
# Scheduled tasks

The Scheduled Tasks page lists every task in Task Scheduler and can run, stop, turn on, turn off and delete tasks. It needs the app to run as administrator. The read-only task list of `winora discover` is separate and unchanged ([discovery.md](discovery.md)).

## What the page shows

Each task is one card: its icon (the icon of the program it starts when that can be read), name and state, path, one chip for each different trigger ("At sign-in", "Daily at 09:00", at most three and then "+N"), the command line cut to one line with the full text in a tooltip, an On/Off switch, a Details button, a Run or Stop button and a Delete button. Details opens a dialog with the name, path and state, the author and account, the triggers in plain words, the action, the last and next run, the last result and the safety verdict, with a Copy details button. The page has search (name, path, command, author, description, account), a sort list, a Hide Microsoft tasks switch (on by default and remembered), quick filters, more filters (state, safety, advice, runs at logon or boot) and a summary of counts. Hidden tasks are included.

A control that the task or the safety policy does not allow stays visible and disabled; its tooltip gives the reason. Without administrator rights every control that changes something is disabled. The page has no button that opens Task Scheduler itself.

## Changes

| Action | Rule |
|---|---|
| Turn off, turn on | Reads the state back. Undo is offered while the task definition is still the one Winora left. |
| Run now | Starts the task through Task Scheduler with its own account. Needs a turned-on task that is not running. No undo. |
| Stop | Needs a running task. Checks that it stopped. No undo. |
| Delete | Only for tasks outside `\Microsoft\` whose author does not start with "Microsoft". A copy of the definition is stored and read back first. The task is deleted and the deletion is read back. |
| Restore | Registers the task again from the stored copy, only when the path is free and the copy still has the hash recorded in the journal. It never replaces a task. |

Every change shows what will change, asks once, and is recorded before it is made (a journal line is written to disk first). If the journal or the copy cannot be written, nothing is changed. A change is made only while the task definition is the one the review showed. Task Scheduler is changed through its COM API only; no command line is built and no program such as `schtasks.exe` is started.

The journal is `scheduled-task-journal.jsonl` and the copies are `task-backups\<random id>.xml`, both in the folder that only administrators can change (`%ProgramData%\Winora\journals`). Winora never removes a copy.

## Protected tasks

The reviewed data file `definitions/tasks/ScheduledTaskPolicy.v1.json` is part of the app. Each entry carries a source note.

- Protected folders: Windows Update and servicing, Microsoft Defender and exploit protection, BitLocker, TPM, license validation, device registration, time and time zone, registry backup, system restore, device setup, Shell, Task Manager, drive optimization, data integrity scan, disk check, maintenance, memory diagnostic, base filtering engine, app deployment and the app state repository. These tasks cannot be turned off, stopped or deleted. They can be turned on and run.
- Fail-closed rule: a task under `\Microsoft\Windows\` that is on neither list cannot be turned off, stopped or deleted.
- Known optional tasks (Windows telemetry, feedback and consent tasks, 13 in this version) can be turned off and stopped, and show "Recommended to disable" or "Reduces diagnostics". Tasks that feed compatibility or error data carry a plain warning, for example that turning off the compatibility appraiser can slow feature upgrades.
- Tasks of other Microsoft products (outside `\Microsoft\Windows\`) can be turned off and stopped, never deleted.

The entries are observed behavior (task folders and descriptions on a default Windows 11 install), not quotes of Microsoft documentation; see [sources.md](sources.md).

## Limits

- A task whose definition needs a stored password cannot be restored.
- Task Scheduler normalizes a definition when it registers it, so a restored task can differ in white space and defaults from the original.
- Evidence and what was not checked: [verification.md](verification.md).
