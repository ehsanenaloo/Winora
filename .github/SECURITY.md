# Security Policy

Winora is a Windows 11 settings manager. The desktop app runs as administrator and changes per-user and machine-wide settings, services, scheduled tasks and installed programs. Security reports are welcome.

## Supported versions

There is no public release yet. Once releases exist, only the latest 0.x release is supported. Older versions are not patched; please update. See [release limits](../technical-docs/release-limits.md) for what 0.x does and does not do.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Report it privately with GitHub's private vulnerability reporting:

1. Go to the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Describe the problem and how to reproduce it.

Direct link: <https://github.com/ehsanenaloo/Winora/security/advisories/new>

Helpful details:

- the Winora version (shown in **About Winora**) and the Windows build,
- the part that is affected (the desktop app, the command-line tool, the installer, a definition file),
- clear steps to reproduce, ideally on a virtual machine and not on your own PC,
- the impact you expect.

Do not include personal data. Winora's diagnostics export removes personal data, but read it before you attach it.

## What to expect

This is a volunteer project, so there is no promise of a response time. The maintainer will confirm the report, assess it and work on a fix for a confirmed problem. Fixes are released as a new version with a changelog entry. You are credited if you want to be. Please allow a reasonable time for a fix before you disclose the problem in public.

## Scope

In scope:

- ways to make Winora write anything outside its authored setting catalog and its reviewed lists,
- ways to bypass the review step, or to make a change happen without your confirmation,
- an undo that overwrites a newer change made outside Winora,
- tampering with the journal or a definition file to redirect a write,
- ways for a less privileged user or process to make the elevated app act for them, for example through its data folder,
- data leaks through the diagnostics export or the log,
- ways to make Winora start a program that is not on its fixed list, or to pass it other arguments.

Out of scope:

- an attacker who already runs as the same user and changes that user's own settings directly,
- an attacker who already has administrator rights,
- SmartScreen or Smart App Control warnings on unsigned builds,
- the behavior of Windows itself, or of the programs that Winora starts (such as an app's own uninstaller),
- findings that need a modified copy of Winora.

## Design notes for security researchers

- The threat model is in [security design](../technical-docs/security.md).
- Changes come only from closed lists in the source and in `definitions/`. There is no generic write endpoint.
- Every change is written to a journal before it happens and is read back after it.
- The command-line tool does not elevate and changes only per-user settings.
- Winora has no telemetry, no accounts and no network access of its own.

## Verifying downloads

Compare the SHA-256 of the file you downloaded with `SHA256SUMS` from the same release. Signing is described in [signing.md](../technical-docs/signing.md). Until a release says it is signed, treat it as unsigned.
