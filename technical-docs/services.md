<!-- SPDX-License-Identifier: GPL-3.0-only -->
# Services

The Services page lists every service and driver of the PC. For a service that is not locked it can start, stop and restart the service and change its startup type. It needs the app to run as administrator. The read-only service list of `winora discover` is separate and unchanged ([discovery.md](discovery.md)).

## What the page shows

Display name, service name, state, startup type, kind (own process, shared process, kernel driver, file system driver), account, program path and description. Filters: state, startup type, kind, safety class, Recommended. Quick chips: Running, Stopped, Automatic, Disabled, Recommended, Protected.

## Changes

| Action | Rule |
|---|---|
| Start | Needs a stopped service that is not disabled. Waits until the service reports Running. A service that falls back to Stopped is reported as refused. No undo. |
| Stop | Needs a running service that accepts a stop request. Refused while another running service depends on it; the message names those services. Dependent services are never stopped automatically. Waits until the service is Stopped. No undo. |
| Restart | Stop, then start. If the start fails the message says that the service was stopped. No undo. |
| Change startup type | Automatic, Automatic (delayed), Manual or Disabled. Disabling is refused while an enabled service depends on the service; the message names them. The new type is read back. Undo is offered while the startup type is still the one Winora left. A running service that is disabled keeps running until it is stopped. |

Every change shows what will change, asks once, and is recorded before it is made (a journal line is written to disk first). If the journal cannot be written, nothing is changed. A change is made only while the service is in the state the review showed; otherwise nothing is touched. Risk in the review: High for a service that Windows starts as a protected process (security software, whoever made it), Medium for a Windows service, Low for a service of another vendor; for a service of the Recommended group the risk is the higher of the two.

The journal is `service-journal.jsonl` in the folder that only administrators can change (`%ProgramData%\Winora\journals`).

## Locked services

A locked service shows the reason and none of its actions work.

- Drivers (kernel and file system), services that start at boot or with the system, and per-user or special services (Windows creates the per-user ones again at every sign-in) are always locked.
- The reviewed data file `definitions/services/ServicePolicy.v1.json` lists the services that Windows, security, updates, networking, storage, sign-in, licensing, logging, remote access, virtual machine guests or Winora itself need. Each group has a reason and a source note. A name that ends in `*` is a prefix. The list contains every service that the machine definitions never change.
- A Windows service that is on no list can be changed, with risk Medium and a warning.

## Recommended group

The services of the machine definitions (the same ones the Optimize page can change) form the Recommended group, with the risk badge of their definition. The startup type picker starts on the suggested type. Applying it is an ordinary startup type change with its own review and journal; the Optimize page keeps its own.

## Errors

Access denied, a dependent service that runs (named), a dependent service that is not disabled (named), a service that did not stop or start in time, a service that refused to start or stopped again, a service that a start needs and could not start, a service that changed after the review, and a startup type that Windows did not keep each have their own plain message in English and Persian.

## How it is built

The calls to the Service Control Manager are in one reviewed file (`WindowsServiceControl.cs`); a source scan keeps start and stop calls out of every other file. The startup type is written by the same reviewed code that the machine definitions use. The elevated worker has no start or stop request.

## Limits

- Evidence and what was not checked: [verification.md](verification.md).
- A change of a service that Windows or another program controls can be undone by that program.
