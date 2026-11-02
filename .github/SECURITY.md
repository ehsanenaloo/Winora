# Security policy

Winora runs as administrator and changes Windows settings, so security reports matter.

## Supported versions

Only the latest release gets security fixes. Please update before you report.

## Report a problem

Do not open a public issue for a security problem. Use GitHub private vulnerability reporting:

<https://github.com/ehsanenaloo/Winora/security/advisories/new>

Please include:

- the Winora version (shown in About Winora) and your Windows build,
- the part that is affected, such as the desktop app, the command-line tool or the installer,
- the steps to reproduce it,
- the impact you expect.

Do not include personal data.

## What happens next

The maintainer confirms the report and works on a fix. The fix comes in a new release. You are credited if you want to be. Please give a fair amount of time for a fix before you talk about the problem in public.

## Scope

In scope:

- making Winora change something outside its own lists,
- making a change happen without the review or without your confirmation,
- making the app act for a less privileged user or process,
- leaking personal data through the diagnostics export or the log.

Out of scope:

- an attacker who already has administrator rights,
- an attacker who changes their own settings directly,
- the behavior of Windows or of programs Winora starts,
- problems that need a modified copy of Winora.

## Check your download

Compare the SHA-256 of the file you downloaded with the `SHA256SUMS` file from the same release.
