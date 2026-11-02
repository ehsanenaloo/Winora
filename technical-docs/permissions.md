# Permissions page

Status: implemented and tested with fakes, a test area of the registry and read-only checks on a development PC. Not yet run on a test machine or virtual machine. See [verification.md](verification.md) and the sources in [sources.md](sources.md).

## What the page shows

The page is the shared settings page: a category rail (all, the three sections and Apps), the search and filter toolbar, one row per value with a switch, the pending bar and the change history below the list.

For every app permission that the Windows consent store knows (camera, microphone, location, contacts, calendar, phone calls, call history, email, messaging, tasks, account info, notifications, radios, other devices, eye tracker, documents, pictures, videos, music, file system, app diagnostics, screenshots and screen recording, screenshot borders):

- The value for the signed-in account: allowed, blocked, or not set (Windows decides).
- A second value for desktop programs, where Windows keeps one (the `NonPackaged` key).
- The apps that have a record, in the Apps category: for each package a row with a switch of its own and the last use that Windows recorded. The row of a capability names how many apps have a record. A desktop program has no switch of its own; it follows the desktop value.
- A note when a limit applies: a device-wide value of Deny (read only) or a Group Policy `LetAppsAccess*` value of 1 or 2 (read only). In both cases the account value has no effect while the limit stays.

Capabilities whose key does not exist on this Windows are not listed.

## What it can change

Only the `Value` text of keys that already exist below `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\<capability>`: the capability key, its `NonPackaged` key, or the key of one package. The text is `Allow` or `Deny`, or the value is removed again by an undo. No key is created. The device-wide values under `HKLM` and the policy values are never written by this page.

A switch does not write at once. Each change waits in the pending bar; Review and apply shows every staged value with its value before and after and asks once. One review applies all of them as one change with one undo. Discard drops them.

Quick switches, above the list. Each stages the values of its group and opens the same review:

- Camera and microphone: one switch for both, including their desktop values. It turns them off when any is not blocked, and on when all are blocked.
- Privacy lockdown: camera, microphone and location are blocked together, including their desktop values.

## Safety rules

- Every change shows the value before and after for each value and asks once, for all staged values together.
- A line with the values before and after is written to the change history and flushed to disk before the first value is written.
- Each value is changed only while it still holds what the review showed. A value that someone else changed meanwhile is not overwritten.
- Each value is read back after the write. When a later value of a group cannot be written, the earlier ones are put back.
- Undo puts the old text back (or removes the value again) for the whole group, only while every value is still what the change left.
- A value that is not text, or text other than Allow or Deny, is shown as unusual and is not changed.
- If Windows or a security product refuses the write, the page says so and nothing is left changed.
- Changes need no administrator rights. They apply to the account that runs the app (the current token profile). Apps that are open must be restarted to see a change.

## Limits

- The consent store is what the Settings app writes, but Microsoft does not document it. The names and the effect of the desktop value come from the Settings app and community scripts.
- The page does not set the Group Policy values or the device-wide values. Those need administrator rights and belong to the machine definitions.
- The global "let apps run in the background" switch is not offered. Windows 11 no longer shows it in Settings and its effect is unverified.
