# Profiles

Status: implemented and tested with fakes and read-only checks. Not yet run on a test machine or virtual machine. See [verification.md](verification.md).

## What a profile is

A profile is a named list of settings that Winora changes together. Each entry names one setting that already exists in a catalog and one of its choices:

- a per-user setting of the settings catalog ([settings-catalog.md](settings-catalog.md)), changed through the settings engine and its journal, or
- a machine setting of the machine definition set, changed through the Optimize service (needs administrator rights).

An entry holds no path, no value and no command. Because it only names a catalog id and a choice, a profile cannot do more than the Customize and Optimize pages can do.

## Built-in profiles

They ship in the app as files in `definitions/profiles/`, one file per profile. Every entry has a one-line reason, in English and Persian.

| Profile | Purpose |
|---|---|
| Balanced | Everyday changes with low risk: fewer ads and suggestions, file extensions visible, no sharing of update parts with other PCs. |
| Speed | Less background work and fewer small effects: transparency off, no search highlights or web results, no silent app installs, no Widgets board, no update sharing, and two services that start only when needed. |
| Security | Safe settings that close common attack paths: file extensions visible, AutoPlay off, no third-party suggestions, no cloud clipboard upload, no notification text or camera on the lock screen, no name sharing with every network adapter, no remote app installs. |
| Privacy | Less data sent to Microsoft: diagnostic data, advertising, activity history, location, input collection and suggestions. |
| Gaming | Less background load and fewer interruptions: Game Mode on, background game capture off (also by policy), transparency off, quiet notifications, no Widgets board. |
| Minimal | Only the clearest annoyances: suggestions in Start, cloud offers in File Explorer, web results in Start and the Widgets board. |

There is no windowed-optimization or power plan entry in the Gaming profile, because no reviewed setting for them exists yet.

## Rules

- A profile can only name a setting that exists in the catalogs, with a choice that setting has. The loader refuses anything else.
- A profile never holds a setting with risk High. Machine entries are Low or Medium risk, and they are skipped unless the app has administrator rights.
- An entry that does not fit this PC is skipped with a reason when the plan is made. It is not an error. Reasons: other Windows build or edition, a policy manages the value, the current value is not one Winora knows, the app has no administrator rights.
- An entry that is already set is not written.

## Plan, apply and undo

1. **Review plan** reads this PC and lists, per area, each entry with the current value and the new value, its reason, its risk and whether it needs a restart. Nothing changes.
2. **Apply** asks once for the whole list. Before the first machine change the restore point question is asked once. Then each entry runs through the same service as the Customize or Optimize page: a journal line first, compare-and-set against the value the review saw, a read-back after the write. A value that changed after the review is not overwritten. All entries of one run carry one group id in the journals. Explorer is never restarted by a profile; entries that need a restart or a sign-out say so in the report.
3. **Cancel** stops after the entry that is running. What was applied stays and can be undone.
4. **Undo profile** puts back the entries of one run that still hold the value Winora set. An entry that something else changed since is left as it is and listed in the report.

The page shows the profiles that were applied, with how many changes each made and how many can still be undone.

## Your own profiles

- **Save my pending choices** saves the settings picked on the Customize and Optimize pages but not applied yet.
- **Save changes Winora made** saves the settings that Winora changed and that still hold the value Winora set.
- Profiles are stored as one JSON file each in the `profiles` folder of the application data folder (`%LocalAppData%\Winora\profiles`).
- **Export** writes a profile to a file you choose. **Import** reads a file you choose.

A profile file is never trusted. It must be at most 64 KiB and at most 300 entries, and have exactly this shape, with no other member:

```json
{ "version": 1, "kind": "winora-profile", "name": "My setup",
  "entries": [ { "kind": "user", "id": "explorer.show_file_extensions", "choice": "show" } ] }
```

The id of the stored profile comes from the name, never from the file. Every entry is checked against the catalogs on this PC. An unknown id, an unknown choice, a High risk entry, a repeated entry, an unknown or repeated member, a comment, a trailing comma, a name with control or direction characters, or a file over the limits refuses the whole file, and nothing is stored.

## Limits

- Profile entries were not applied to a real PC yet.
- No command-line verbs for profiles exist yet.
- Persian text is a draft that needs a native review.
