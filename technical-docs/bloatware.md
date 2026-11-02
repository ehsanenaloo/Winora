# Bloatware: Microsoft Store (MSIX) packages

Scope: list, classify and remove Microsoft Store (MSIX) packages for all users, and the provisioned copies that Windows installs for new users. Code: `src/Winora.Application/Bloatware`, `src/Winora.Infrastructure.Windows/Bloatware`, the Bloatware page in `src/Winora.Desktop`. Rule data: `definitions/bloatware/BloatwareRules.v1.json`. Evidence: [verification.md](verification.md). Sources: [sources.md](sources.md).

## Package API

- Packages are read and changed through the documented `Windows.Management.Deployment.PackageManager` runtime class. The SDK projection package is not part of this build, so one reviewed file (`WinRtAppPackages.cs`) calls its COM interfaces directly. Every interface id and method position comes from the Windows metadata file; a test compares each entry with that file on the machine that runs it.
- No script host and no command line is used. A source scan allows the package manager calls in that one file only.
- Reading all users and every change need administrator rights. Without them the page lists the packages of the current user and removes nothing.
- Listed types: main packages, frameworks and optional packages. Resource packages are removed by Windows together with their app and are not listed.

## Classes

Every package gets one class from a reviewed list of anchored rules. A rule matches the whole package name, or the start of it when the rule ends in a dot, and always the publisher id for Safe and Caution rules and for prefix rules. Substring and pattern matching do not exist; the loader rejects pattern characters.

| Class | Meaning | Winora removes it |
|---|---|---|
| Protected | Store and app installer, Windows shell and security parts, runtimes and frameworks | never |
| Safe | consumer apps Windows works without | yes |
| Caution | apps that give a feature (camera, codecs, Game Bar, widgets, OEM tools) | yes, with a warning |
| Unknown | not in the list | yes, with a warning, never pre-selected |

Protected rules are checked first and win over every other rule. The loader refuses a Safe or Caution rule that a Protected rule also covers. Each rule has a reason group (a plain sentence in both languages), a basis (Microsoft documentation or observed behavior) and a reference.

Independent of the class, Winora never removes: a framework package, a package signed by the system or installed in the Windows folder, a package that an app it does not know depends on, and a package that Windows refused to remove on this PC (see Removal). These apps are listed under Keep with the reason.

## Removal

1. Preview: for each selected package the class, the accounts that have it, the size on disk and the steps. Refused packages are listed with the reason. A package that other apps need is not blocked when every one of those apps is Safe or Caution too: the preview adds them, marked "needed by a chosen app", and they are removed first. A package that an unknown app needs stays blocked and the text names that app.
2. One confirmation. It says that the app files and data are deleted and that the app can be restored later (see below).
3. One package at a time: a journal line (full name, family name, version, publisher, install location, accounts, provisioned or not, class) is written and flushed first. The steps are:
   - Remove for all users, and read back that it is gone.
   - When Windows answers that the package is in use: close the processes of that package (the documented package debug interface, one call with the package full name, in the same reviewed file) and remove again. Only the processes of the session of the user who runs Winora are closed.
   - When Windows refuses (or fails) and the package has a copy for new users: remove that copy first, then remove again.
   - Remove the copy for new users, and read back. Windows can answer "not found" when removing for all users already took it away; that counts as done.
   An answer that stays "refused by Windows" is recorded as `bloat.system_forbids`. The next listing shows that package (same version) under Keep with the reason, not as an error. A new version is tried again.
4. Cancel stops after the package that is being removed; packages not started are reported as not started. A journal that cannot be written stops the run before anything is changed.
5. The report names each app and, for a failure, says in one sentence why and what to do (resource keys `V2BlFail_*`). The Windows error number is added only when there is no plainer reason.

## Getting an app back

The page has a Removed apps tab with one row per removed app that is not installed now, and a Restore button on the history rows. Both use the same service call, which writes its own journal line (kind Reinstall, linked to the removal):

- Files still on the disk (for example when only a later step failed): register the package again for the current user (administrator rights). If Windows refuses and the app is a Store app, the Store route follows in the same journal line.
- Otherwise, for an app signed by the Store (and Microsoft apps signed outside it): Winora opens the Store page (`ms-windows-store://pdp/?PFN=<package family name>`, only for a name in the Windows family name format) and waits until Windows reports the family installed. The user presses Install in the Store. It polls every 2 seconds for up to 10 minutes; a stop button ends the wait. The result is Applied, Cancelled, or Failed with `bloat.store_not_finished` or `bloat.store_failed`. Winora downloads nothing itself.
- Other apps (third party sideloaded): no restore; the row says so.
- Data and settings of the app do not come back. Winora keeps no copy of the package files (ISS-2562).
- The History page offers Undo for a removal only in the register case; the Store case points to this page because the wait takes minutes.

## Reinstall blocking

Three per-user settings (Customize page) stop Windows from adding suggested and promoted apps to the account: `PreInstalledAppsEnabled`, `OemPreInstalledAppsEnabled` and `SilentInstalledAppsEnabled` under `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager`. Microsoft does not document these values. They are marked as observed behavior, and feature updates can set them back.

## Limits

- Windows does not expose its own non-removable flag through this API. Winora treats system-signed packages and packages in the Windows folder as non-removable and relies on the Protected list and on Windows refusing the rest.
- The list of rules covers well-known packages. A wrong publisher id on a Safe or Caution rule makes the package show as Unknown. Protected rules use only the two Microsoft publisher ids.
- On the test VM (Windows 11 build 26200) 26 Safe or Optional apps were removed through this API without a refusal; the "in use", "refused" and "forbidden" paths are tested with fakes only. Removal on the page and restore through the Store were run end to end on the VM (see verification.md).
