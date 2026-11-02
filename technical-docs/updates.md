# Updates page and Windows Update modes

Status: implemented and tested with fakes and read-only checks. Not yet run on a test machine or virtual machine; whether Windows Settings and the update client honour each value on every Windows build is unverified. See [verification.md](verification.md) and the sources in [sources.md](sources.md).

## What the page shows

- The current mode, or "custom" when the values on this PC match no mode.
- A banner "Managed by your organization" when an update policy value exists that Winora did not write: a value under the Windows Update policy keys that is not Winora's own, or a value that device management put in the policy store. Winora compares the live value with the last value it wrote (from its change history) to tell its own values from others.
- A banner "Security updates are paused until <date>" when Windows Settings holds a pause date in the future.
- A banner when Windows reports a restart that waits for an update.
- Facts, read without administrator rights and without WMI: the Windows build and update revision, the feature release name, the last update check and install time (only on Windows versions that still record them in the registry; newer builds do not, and the page says so), the pending restart flags, and the "get the latest updates as soon as available" switch.
- The update policy values on this PC, with who set each one: Winora, Group Policy or another tool, or device management.

## Modes

A mode is a full set of values. Every mode names a choice for every value that Winora can set for updates, so switching modes never leaves a value of the previous mode behind. Values are policy values under `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` and its `AU` subkey.

| Mode | Values | Risk |
|---|---|---|
| Windows default | Removes every update value Winora can set. | Low |
| No driver updates through Windows Update | `ExcludeWUDriversInQualityUpdate` 1. | Medium |
| Security-focused | `DeferFeatureUpdates` 1 with `DeferFeatureUpdatesPeriodInDays` (30, 90, 180 or 365; default 365), `DeferQualityUpdates` 1 with `DeferQualityUpdatesPeriodInDays` (4, 7, 14 or 30; default 4). | Medium |
| Stay on this Windows release | `TargetReleaseVersion` 1, `TargetReleaseVersionInfo` set to the running release (for example 24H2), `ProductVersion` "Windows 11". The release is read from the running Windows; the mode is unavailable when that release name is not in the list. | Medium |
| Notify before download and install | `AUOptions` 2. | Medium |
| Automatic updates off (Advanced) | `NoAutoUpdate` 1. | High |

Security-focused, Stay on this Windows release and Notify offer "Also leave drivers out of Windows Update". The days are fixed steps because a definition holds fixed values only.

The values are definitions of the machine catalog (the same catalog as the Optimize page), so they also show there. Policy values need Windows Pro, Enterprise or Education; on other editions the modes are unavailable and the page says so.

## Pause

Windows Settings pauses updates for a limited time. The pause is a date that Windows Settings keeps, and Winora does not write it. The page shows the banner when a pause exists and has a button that opens the Windows Update page of Windows Settings. The very long pause value that raises the maximum pause length is not used.

## Safety rules

- Preview: every value of the mode is listed with its value before and after. One confirmation; the Advanced mode asks a second time.
- A line is written to the change history and flushed to disk before each value is written. Each value is a compare-and-set against what the preview saw, and is read back after the write.
- Nothing is written when any value of the mode is refused (managed by policy, unsupported build or edition, a value Winora does not know). A value that policy manages and that already has the wanted data does not block the mode.
- If a write fails, the group stops. What was written stays in the history and the page offers one undo for it.
- One undo puts back every value of the group, newest first, while the value is still what Winora wrote. A value that someone else changed since is left alone and counted.
- Update services, scheduled tasks, the update cache folder (`SoftwareDistribution`) and other policy keys are never touched. Winora never deletes a policy tree.
- Before the first change of the machine in a session, the page offers a Windows restore point like the other pages.
- Needs administrator rights to change; reading works without them.

## Limits

- Group Policy or device management can override or lock these values. Settings then shows "managed by your organization".
- A delay of quality updates delays security fixes. Staying on a release stops security updates when Microsoft ends support for that release. Automatic updates off stops them until it is undone.
- Windows Home largely ignores the policy values.
- The last check and install times use registry values that newer Windows builds no longer write.
- The names of the pause dates and the effect of each value on every build are not verified on a real machine.
