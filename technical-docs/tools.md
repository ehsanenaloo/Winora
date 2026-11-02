# Tools page: repair tools and optional Windows features

Status: implemented and tested with fakes, text checks and a harmless program. No repair tool and no feature change was run on a test machine or virtual machine. See [verification.md](verification.md).

## What the page does

The page is named "Repair and features". It has two parts.

1. Repair tools. Each tool starts one Windows program with fixed settings.
2. Optional Windows features. The page lists the features, shows their state and turns reviewed features on or off.

Everything on the page needs administrator rights. Without them the page explains what each tool does and turns the buttons off.

## Repair tools

| Tool | Command | Changes | Restart |
|---|---|---|---|
| Check system files | `sfc.exe /scannow` | May replace damaged Windows system files | no |
| Scan a disk for errors | `chkdsk.exe <drive> /scan /perf` | Reads the drive | no |
| Quick check of the component store | `dism.exe /Online /English /Cleanup-Image /CheckHealth` | Nothing | no |
| Full scan of the component store | `dism.exe /Online /English /Cleanup-Image /ScanHealth` | Nothing | no |
| Repair the component store | `dism.exe /Online /English /Cleanup-Image /RestoreHealth` | Replaces damaged component store files, may use the network | maybe |
| Reset the Microsoft Store cache | `wsreset.exe` | Removes the Store cache | no |
| Reset Winsock | `netsh.exe winsock reset` | Removes Winsock add-ins | yes |
| Reset the IPv4 stack | `netsh.exe int ip reset` | Removes hand-set IPv4 settings | yes |
| Reset the IPv6 stack | `netsh.exe int ipv6 reset` | Removes hand-set IPv6 settings | yes |
| Reset the WinHTTP proxy | `netsh.exe winhttp reset proxy` | Removes the system-wide WinHTTP proxy | no |

Each program is a file in the System32 folder of the running Windows. The page shows the exact command, in plain text, and asks once.

### Safety rules

- Only the entries above can be started. The list is a closed enum in the code. There is no free text. The only values that are not fixed are the drive letter of the disk scan, which must be a fixed drive read from the system, and a feature name, which must be a plain name taken from the list that was just read.
- One reviewed source file starts programs. It uses no shell, no script host, the System32 folder as working directory and a closed standard input. It reads the output up to a size cap and applies a time limit to each tool (see "Wait for slow tools" below). When the person stops a run, it ends the whole process tree. When a wait reaches its limit, Winora only stops waiting: the program keeps running and is never ended because of the time. The program does not inherit the environment of the app, which holds the user's own variables. It gets a fixed set: machine values only, a search path inside the Windows folder, the profile folders of the account, and TEMP and TMP in a new folder under `%ProgramData%\Winora\temp` that only administrators can change. Nothing starts if that folder cannot be made.
- Only one run exists at a time. The run, its progress and Stop are in the Action center. A program that Winora stopped waiting for counts as running: no new tool starts until it has ended.
- When a run takes longer than usual (the longest time the page names for the tool), the page shows "Still running" with the time so far, the limit and a Stop button.
- A run that reaches its limit is recorded as "Still running" and shown as "Result unknown" in the history. It is never recorded as failed, because the program may still finish its work.
- A line with the exact command is written to the repair record before the program starts. The result (exit code, short summary, restart needed) is written after it ends. The record is a JSON Lines file in the folder that only administrators can change.
- Repairs have no undo. The page says so before the run.
- The result log (command, result, exit code, program output) can be copied. When a restart is needed, the page shows a notice. Winora never restarts the PC.
- The text of the result decides for `sfc.exe` and for the component store check. These programs print English text only when the output language is English, so Winora asks DISM for English (`/English`). If `sfc.exe` prints another language, exit 0 shows as Finished with a neutral tone and asks the person to read the log (never as a proven clean or repaired result), and another exit code shows as "found problems".

### Wait for slow tools

Settings, Advanced group, "Wait for slow tools": Normal (default), Long (two times) or Very long (four times). The choice changes only the waits for programs that end on their own and that are safe to wait for longer: uninstallers, DISM, SFC and the other repair tools. Each wait keeps a fixed lower limit (its Normal value) and a fixed upper limit. Short waits, such as the DNS probe or a service start and stop, keep their fixed limit. There is no free number in milliseconds.

| Wait | Usual time | Normal | Long | Very long |
|---|---|---|---|---|
| Quiet uninstaller | 5 min | 20 min | 40 min | 60 min |
| Uninstaller with its own window | 15 min | 60 min | 120 min | 180 min |
| Check system files (sfc) | 30 min | 90 min | 180 min | 240 min |
| Scan a disk (chkdsk) | 60 min | 90 min | 180 min | 240 min |
| Quick check of the component store | 2 min | 15 min | 30 min | 60 min |
| Full scan of the component store | 30 min | 60 min | 120 min | 180 min |
| Repair the component store | 60 min | 120 min | 240 min | 360 min |
| Read the optional features | 3 min | 15 min | 30 min | 60 min |
| Turn a feature on or off | 10 min | 60 min | 120 min | 180 min |
| Network resets (netsh) | 1 min | 3 min | 6 min | 10 min |
| Reset the Microsoft Store cache | 3 min | 5 min | 10 min | 15 min |

### What is not offered

- The three-level Windows Update reset (stop the update services, rename the SoftwareDistribution folder, wipe policy keys). Stopping and starting the update services is not allowed by the service rules, and Winora does not delete or rename the update data or touch policy keys. No documented interface exists for the same effect.
- Re-register Start and Search packages, and the reset of app data of shell packages. The only documented route is a PowerShell cmdlet, and PowerShell is not allowed.
- DISM through its programming interface (`dismapi.dll`). The page uses `dism.exe` with fixed arguments instead, see below.

## Optional Windows features

The list comes from `dism.exe /Online /English /Get-Features /Format:Table`. It is loaded when you ask, because DISM takes a while and the page must cost nothing while it sits idle.

Each feature shows its state (on, off, turns on or off after a restart), the reviewed description, its class and the source note. The class comes from `definitions/features/FeatureClasses.v1.json`, which ships in the app.

| Class | Meaning |
|---|---|
| safe | Can be turned on or off after one confirmation |
| caution | Same, with a stronger warning (for example Hyper-V, SMB 1.0, PowerShell 2.0, IIS) |
| protected | Winora does not change it (Windows Search, Microsoft Defender parts, Windows client base packages) |
| not in the list | Shown only |

A feature can list other features that must be off before it may be turned off. Virtual Machine Platform stays on while Windows Subsystem for Linux or Windows Sandbox is on.

A change runs `dism.exe /Online /English /Enable-Feature /FeatureName:<name> /NoRestart` or the matching `/Disable-Feature` command. It does not use `/All`, so it changes exactly one feature. DISM refuses to turn on a feature whose parent is off, and the page shows that result. The state before the change is stored in the record. After the change the list is read again and compared. Undo puts the feature back in its earlier state, and only while the feature still has the state Winora left.

Features on Demand (Windows capabilities) are not part of the page.

## Limits

- The English result lines of `sfc.exe` and the DISM health check are the only way to read their outcome beyond the exit code. Other languages get a general result.
- `chkdsk.exe` exit codes follow the documented values: 0 no errors, 1 errors fixed, 2 clean-up, 3 problems not fixed.
- DISM needs a working Windows Update or a repair source for "Repair the component store".
- If the app is closed while a program runs, the program keeps running and the record shows the run as not finished.
