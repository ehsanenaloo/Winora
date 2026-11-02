# Changelog

All notable changes to this project are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

Winora has not had a public release yet, so everything below is unreleased. The builds are not code-signed and have not been tested on real Windows devices. See the [release limits](technical-docs/release-limits.md) before you run them.

### Added
- 23 new machine settings: Microsoft Edge and Google Chrome policies that turn off feedback, usage reports, tips, shopping and background running, and network hardening for name lookups, file sharing, the password reveal button and printer drivers. Each one shows its source and can be undone.
- The taskbar button shows the progress of a long task and marks a failed task or a needed restart. A notice tells you when a long task ends while Winora is not in front.
- Closing the window during a long task keeps the task running, with an icon next to the clock (Open, Stop, Exit). A new setting, off by default, keeps Winora there whenever the window closes.
- Settings has an Advanced choice, Wait for slow tools (Normal, Long or Very long), for uninstallers, DISM, SFC and the other repair tools. Each wait stays inside a fixed range. When a repair runs longer than usual, the Tools page shows Still running with the time so far and a Stop button. When a wait ends, Winora stops waiting but never ends the program, and the result is recorded as unknown, not as failed.
- The Privacy page now works like Customize and Optimize: each switch waits in the pending changes, and one review applies them all with one undo. The quick switches for camera and microphone and for the privacy lockdown open the same review. The Tools page uses the same rows and search bar as the other pages.
- Right-click the Winora button on the taskbar to open Overview, Customize, Cleanup, Startup, Apps or History. Winora now runs once: starting it again, from the jump list or by hand, brings the open window to the front on the page you chose. A jump list entry does not show a second administrator prompt while Winora is open. The names appear in English and Persian.
- An Action center on the right of the window (button in the title bar or Ctrl+Shift+A). It lists every change that waits for review with its before and after values, shows running tasks with Stop, and keeps recent events for 30 days. A status strip shows the same while it is closed, and the bar at the bottom of each page is gone. Ctrl+Enter starts the review of the waiting changes from any page.
- One toolbar on every list page (search with a caption and a clear button, sort with an Ascending or Descending button, and a Filters button that shows how many filters are on). The History page shows 25 changes per page with Newer and Older buttons, and the administrator notice is one line with a link that opens a short how-to.
- The text under each page title is now one sentence. An About this page button shows the rest, so the top of a page stays short.
- Every page keeps its header, tabs and toolbar in place while only the content below them scrolls. The tabs sit in one row that never wraps: when the row is too wide it scrolls sideways with previous and next buttons, and the arrow keys, Home and End move between the tabs. A group title is now the top of its card, and the Customize page no longer shows the "Real changes" bar.
- After a program is removed on the Apps page, Find leftovers lists the folders it left behind. The folders you confirm move to Quarantine, where you can restore or delete them; they are deleted for good after 30 days.
- The details of an item open in one dialog on every page (Customize, Optimize, Startup, Services, Apps, Preinstalled apps and Scheduled Tasks). Open it from the row menu, with Enter or with a double click. The dialog has the item name, tags, the full description, technical details and one OK button. The lists now use the full width of the page.
- The Startup page groups entries into apps from the registry, startup folders and scheduled tasks that run at sign-in. Each group opens and closes, and each row has an On/Off switch, its publisher and path, and a button to open its folder. Scheduled tasks at sign-in can be turned on or off there too.
- Two new built-in profiles: Speed (less background work and fewer small effects) and Security (safe settings that close common attack paths). Both use only low-risk settings and show every change before it is made.
- The Scheduled Tasks page shows each task as a card with its program icon, state, triggers (for example "Daily at 09:00") and command, an On/Off switch, a Details button, Run or Stop, and Delete. Microsoft tasks are hidden by default and a switch brings them back. Details opens a dialog you can copy. Tasks that Winora does not change show a disabled control and the reason.
- The Apps page shows the real icon of each installed program and Store app.
- The Apps page removes installed programs and Store apps. You review the list once, and each program shows a result with the reason if it could not be removed. Windows components, shared runtimes, hardware drivers and Winora itself are locked. A removal cannot be undone, and leftover files are not removed.

- The Preinstalled apps page can restore a removed app from a new Removed apps tab. It registers the app again from its files, or opens its Microsoft Store page and waits for Windows to install it. Removal now copes with an app that is in use, and an app that Windows will not remove stays listed with the reason.
- The Startup and Preinstalled apps pages show the real icon of each app or program, with a letter badge when no icon can be read.
- A first-run guide checks administrator rights, offers a restore point, lets you choose a profile (Balanced, Speed, Security, Privacy, Gaming or Minimal), shows the plan and applies it after one confirmation. The Overview page has a Quick profile card, the Cleanup page shows a risk for each group and nothing is ticked until you choose, and the disk map uses a line pattern for each file type as well as a color.
- A search box in the title bar (Ctrl+K) finds settings, pages, profiles and tools in English or Persian and opens the page.
- The Overview page lists recommended next steps based on facts read from your PC, and your recent changes with an undo button.
- The History page shows every change from all pages in one timeline, with filters, one row for a profile run and an undo review (Ctrl+Z on that page).
- The navigation sections can be opened and closed, and Ctrl+1 to Ctrl+5 jump to a section.
- Desktop app and command-line tool (`winora`) for Windows 11 (x64). They need the .NET 10 Desktop Runtime.
- 14 per-user settings for File Explorer, the taskbar, privacy, search, themes and window snapping. No administrator rights are needed.
- Every setting change is reviewed first, recorded before it is made, checked afterwards, and can be reverted. Winora does not overwrite a newer change made outside Winora.
- A history page and `winora settings` commands to list, preview, apply, revert and review changes. Interrupted changes are recovered when the app starts or with `winora settings recover`.
- For settings that need it, Winora asks before it restarts Explorer or signs you out.
- Read-only lists of startup items, services and installed applications in the app, and of these three plus scheduled tasks with `winora discover` (JSON output is available). Microsoft Store apps are not listed yet.
- A Scheduled Tasks page that shows every task with its triggers, actions, account and last result, and can run, stop, turn on, turn off and delete tasks. It needs administrator rights.
- Tasks that keep Windows updated and safe are protected, and Windows tasks that are on no reviewed list are left alone. Telemetry and feedback tasks show a recommendation, and some show a warning.
- Every task change is shown first, recorded, checked afterwards and can be undone. A deleted task is saved first and can be restored from the history.
- The Services page can start, stop and restart a service and change its startup type (Automatic, Automatic delayed, Manual or Disabled). It needs administrator rights. Each change is shown first, recorded, checked afterwards, and a startup type change can be undone.
- Services that Windows or Winora need, drivers and services that start with the system are locked and show why. A service is not stopped while another running service depends on it, and the message names those services. Services that Winora suggests changing show a risk badge. The page no longer says "View only".
- Read-only lists of startup items, scheduled tasks, services and installed applications, in the app and with `winora discover` (JSON output is available).
- Read-only lists of startup items, scheduled tasks, services and installed applications, in the app and with `winora discover` (JSON output is available). Microsoft Store apps are not listed yet.
- A Startup manager: turn Run key and Startup folder entries on or off with the flag Task Manager uses, so nothing is deleted. It shows the file facts and can open the file location. Each change is shown first, recorded, checked afterwards and can be undone. Entries of Windows are protected.
- The Startup page checks the signature of each program file in the background without using the network. It shows Signed, Not signed or Signature not valid, with the reason and the signer name. Scheduled tasks that start at logon or boot are listed to read, with a button that opens them on the Scheduled Tasks page.
- A Power page to switch the active power plan, change when the display, disks and PC turn off, set the lid and power button actions and the processor speed limits, and make or delete a copy of a plan. It needs administrator rights.
- Every power change is shown first, recorded, checked afterwards and can be undone from the history while nothing else has changed it.
- The Power page can turn hibernation on or off, add the Ultimate Performance plan of Windows, and set boost mode, PCI Express and USB power saving, wake timers and unattended sleep.
- Power profiles (Balanced, Performance, Battery saver) show every change first and are undone in one step. On a PC with a battery, costly settings show a warning.
- A Fast Startup setting on the Optimize page.
- A Network page that lists your network adapters with their addresses, DNS servers and DNS over HTTPS state, and switches DNS to Cloudflare, Google, Quad9, AdGuard DNS or OpenDNS, to your own servers, or back to automatic. It needs administrator rights.
- Every DNS change shows the servers before and after, is recorded, checked afterwards and can be undone. VPN and virtual adapters are left alone unless you add them. Winora tests name lookup after a change and offers to undo it if it fails. DNS changes have not been tested on a real PC yet.
- A Permissions page that shows what apps may use on your account (camera, microphone, location, contacts, files and more), with the apps that used each one, a switch per permission and per app, a one-click camera and microphone switch, and a privacy lockdown.
- Every permission change shows the value before and after, is recorded, checked afterwards and can be undone as one group. Device-wide values and policies are shown as limits and are not changed. Not tested on a real Windows 11 PC yet.
- An Updates page with Windows Update modes: default, no driver updates, security-focused delays, stay on the current release, notify before download and install, and automatic updates off (high risk, second confirmation). A mode is shown first, applied as one group and undone in one step. It needs administrator rights.
- The Updates page also shows the update policy values and who set them, and notices for managed settings, paused updates and a pending restart. It does not stop update services or delete the update cache. Not tested on a real PC yet.
- An Optimize page with Windows settings that Microsoft documents as policies, such as activity history, advertising ID, diagnostic data limits, location and search highlights. Each shows its risk and its source. It needs administrator rights.
- Optimize changes apply to all users of the PC. Each change is shown first, recorded, checked afterwards and can be undone. Settings that Group Policy or device management controls are left alone.
- Before the first change to the machine in a session, the Optimize and Power pages offer to create a Windows restore point. You can continue without one. Winora does not turn System Protection on for you. This has not been tested on a real PC yet.
- The Overview page shows a system dashboard: Windows, processor, memory, graphics, drives with usage bars, device and firmware, Secure Boot, TPM and the active power plan. It only reads, shows "Unknown" for anything it cannot read, and can copy a plain text summary.
- Free up space (on the Cleanup page) scans cache folders in your profile: temporary files, thumbnail cache, temporary internet files, shader cache, error reports and crash dumps. It shows the size of each group, asks once, and removes the groups you tick. Removed files do not go to the Recycle Bin.
- Free up space never removes files that a program has locked, links, or files made, changed or opened in the last 7 days where an age limit applies. It records each run in a local journal.
- The Desktop app now starts with administrator rights, so Windows asks for permission at each start. With them, Free up space also covers Windows temporary files, the Windows Update download cache, the Delivery Optimization cache and system crash dumps, plus the Recycle Bin and browser caches.
- A Bloatware page lists the Microsoft Store apps of all users and the copies that Windows installs for new users. Each app is marked Safe, Caution, Protected or Unknown with a short reason. It needs administrator rights.
- Bloatware removes the apps you tick for every user and removes the copy for new users as a second step. It shows the plan first, asks once, records each step and checks the result. Winora never removes Windows parts, frameworks, the Microsoft Store or apps that other apps need.
- Winora cannot undo a removal. The history offers to register an app again only while its files are still on the disk, and otherwise opens its Microsoft Store page.
- Three privacy settings stop Windows from adding suggested and promoted apps to your account. Windows updates can switch them back.
- 48 more per-user settings on the Customize page, for example Start, the lock screen and Windows suggestions, search, the taskbar, File Explorer, keyboard and mouse. Settings that change several values are applied and undone together. Windows may reset the settings that stop suggestions after an update, and the page says so. They have not been tested on a real PC yet.
- Disk usage (on the Cleanup page) scans a drive or folder and shows a nested treemap (folders hold their content, files are colored by type) and a sortable list of the largest items, with a right-click menu to show an item in File Explorer or copy its path. It only reads sizes.
- A Density setting (Comfortable or Compact) on the Settings page. Compact makes controls smaller (32 pixels high instead of 40). Parts of the interface follow the choice after a restart.
- Set aside (on the Cleanup page) lists files Winora has moved out of the way. Nothing moves files there yet.
- English and Persian interface with right-to-left layout, light, dark and system themes, and keyboard shortcuts. Screen reader names and high-contrast support are included but not yet checked on real devices.
- A local log and a diagnostics export that you create on demand. Winora removes personal details it recognizes, such as the user name and profile paths. Nothing is sent anywhere.
- `winora simulate` lets you try the review and revert steps on a fake setting.
- `winora machine selftest` checks groundwork for future administrator-level changes. It changes nothing and refuses to run on normal installs.
- A portable zip and a per-user installer (MSI), both unsigned, with SHA-256 checksums and a package list. The zip is identical when it is built again from the same source. The installer has not been tested on a clean machine yet.
- `winora machine plan`, `apply`, `revert`, `recover` and `history` are the commands for future administrator-level changes. No such change is available yet, so they only report that. Apply and revert need an interactive console and a typed confirmation.
- An all-users installer (MSI), unsigned. Administrator-level changes stay unavailable until the build is signed and a change is published.
- 81 more all-users settings on the Optimize page for privacy, the lock screen and Start menu, File Explorer menus and AutoPlay, Microsoft Edge policies, error reporting, disk behavior and Windows AI features. Each one shows its source and risk label.
- Graphics settings on the Optimize page: GPU scheduling, Multiplane Overlay off, the GPU timeout, AMD ULPS and the NVIDIA fixed performance state. The AMD and NVIDIA values are set on the card you pick and can be undone for that card. The vendors do not document them, and they have not been tested on real graphics cards yet.
- More all-users settings on the Optimize page, each with its source, a risk label and an Advanced section for the ones that lower protection (they need a second confirmation). Some settings only apply to certain Windows editions, and some change the start type of a service.
- A Profiles page with four ready-made profiles (Balanced, Privacy, Gaming, Minimal) and profiles you save yourself. A profile shows every change first, asks once, and can be undone in one step. Your profiles can be exported to a file and imported again. Profiles have not been tested on a real PC yet.
- A Tools page, named Repair and features. It runs the Windows tools for system file check, disk scan, component store check and repair, Store cache reset and network reset. Each shows its exact command, asks once, can be cancelled and gives a log you can copy.
- Optional Windows features can be listed and turned on or off from the Tools page. Only reviewed features can change, and each change can be undone. The page needs administrator rights and has not been tested on a real PC yet.
- 49 more all-users settings on the Optimize page, such as services, Windows Update policies, User Account Control prompts and old TLS protocols. Settings that lower protection are High risk, show a warning and need a second confirmation. Four more diagnostics tasks can be turned off on the Scheduled Tasks page.

### Changed
- Disk cards show at most two per row, and a single disk uses the full width.
- Customize and Optimize have an All tab that shows every category at once. The refresh button and other header buttons stay beside the page title.
- On Disk usage, the up arrow and the parts of the folder path are plain text you can click, with no button frame.
- The pages look and behave more alike: one style for the chosen card and the selected row, filter chips in the same place, one main button per tab, settings with a value show a choice instead of a switch, setting descriptions show more whole lines, Windows Update modes are picked from the cards, and corners use the same sizes.
- Persian text has more line height everywhere, and the title bar hides the version and the Administrator word in a narrow window.
- Free up space has a choice for how recent a file may be and still be kept: 7 days (the default), 1 day or any age. Each group also shows how many files and how much space it kept for their age.
- When you close the Action center, it stays as a narrow strip at the side of the window with its icon, its name, the count of waiting changes and a mark for running tasks, a failure or new events. Click it, or press Enter or Space on it, to open it again. The Action center button in the title bar is gone; Ctrl+Shift+A still opens and closes the panel.
- The Winora window opens several times faster: the app is now precompiled for 64-bit Windows in release builds.
- Every long task shows in the Action center, with Stop. Pages no longer draw their own progress panel or Cancel button. A task that changes the system keeps running when you open another page, and only one such task runs at a time.
- Settings and list pages now use one row layout: icon, title, description, the control on the end side and a menu. A settings row no longer shows its source or whether it differs from the Windows default; those facts are in the details dialog and the Show filter.
- The title bar starts with Menu, Sidebar, Back and Forward buttons. Forward (Alt+Right) returns to the page you left with Back, and the sidebar button replaces the one above the page list.
- Network and Windows Update collect your choices in the same pending bar as the other pages, with the same review before anything changes.
- Every page that reads data shows the same loading, error and empty states, and the inventory lists share one row look.
- The change history on each page starts closed, with the number of changes and the newest one in its header. Open, it shows five changes at a time with Newer and Older buttons.
- Dialogs with long content show a single scroll bar and stay inside the window.

- The Persian interface shows numbers, sizes, percents and dates with Persian digits and the Persian calendar, keeps names, paths, drive letters and addresses in their own order, and uses one Persian term for each idea.
- In Persian, names that start with a number or mix numbers and letters (processor, BIOS, device model, services, tasks, links) keep their own order, the power plan on This PC is translated, and Windows Update changes in a change history are tagged Windows Update.
- Every change history row uses the same status words (Applied, Undone, Failed, Cancelled, Removed, Restored), and counts on the Startup, History and Customize pages agree with the lists.
- The Apps page shows the publisher's organisation and a readable name for Store apps instead of certificate and package ids.
- Every confirmation shows the same dialog: what will change as before and after values, the risk, whether it can be undone, whether a restart is needed and the state of the restore point. A high risk change needs a box ticked in that dialog.
- Results show as short notices that go away by themselves, or as a report when several changes were made. Ctrl+Enter reviews the pending changes and F6 moves between the main areas.
- Customize and Optimize have a new settings list with search, filters by risk, source and state, a category list, groups you can fold, a details pane, a view of the changes Winora made, and a separate Advanced section for high risk settings. The list loads only what is on screen.
- The local journal is upgraded the first time the app starts, and a copy of the old journal is saved next to it. Older Winora builds cannot open the upgraded journal.
- The navigation is grouped into Home, Tune, Clean, Manage and Tools, and a dot shows which pages hold changes you have not applied yet. Risk, source and status labels are now calm tinted chips that always show an icon and text, and every page has the same header, notices and empty, loading and error states.
- Profiles, Startup, Preinstalled apps and Cleanup use a new list design. Startup changes are staged and applied after one review, with an estimated impact for each program. Cleanup has a Quarantine tab to restore or delete files.
- Optimize, Power, Network, Privacy and Windows Update use the same card design. Buttons line up in one size, the Advanced sections open with a warning inside, and a Low risk badge is no longer shown on rows.
- The Apps page no longer removes programs that are installed for one account only. It shows the reason and asks you to remove them in Windows Settings. Other programs run only an uninstaller inside their own install folder, or a signed Windows Installer bundle.
- On the Startup page only known parts of Windows are protected. Other programs in the Windows folder can now be turned off.
- On the Services page, security software that Windows runs as a protected service shows risk High.

### Fixed
- The Stay on this Windows release mode no longer shows a raw "{0}" when the release is not known, and Persian drive names with a Latin label no longer show their brackets in the wrong place.
- The setup score badge on Overview has its color again.
- With the keyboard, Tab now goes from the page toolbar straight to the page content, not first through the Action center, the title bar and the menu. Screen readers get clearer names for the link copy buttons in Settings and the next steps on Overview.
- In Persian, letter badges are no longer drawn mirrored, the tab scroll arrows point the right way, and English descriptions keep their full stop at the end. In the dark theme the empty part of a progress bar is no longer almost white.
- An interrupted delete of a scheduled task is now closed at the next start when its result is clear, so it can be undone from History.
- When Winora runs as administrator from a folder that normal programs can change, the notice title now says the folder is not safe instead of asking for administrator rights. The Network and Tools pages show this notice too.
- Several texts now say what Winora really does: open files that a program lets others delete, like browser caches, can be removed; the performance score is an average; space from removed preinstalled apps is freed when Windows deletes their files; repair tools say a restart may be needed; and the Recycle Bin result gives the size from the scan.
- A new accent color now reaches switches, check boxes and buttons at once; before, they kept the previous color or the Windows accent. In the dark theme the palette colors are no longer lightened to almost white.
- When the system file check ends without a result Winora can read (for example on Windows in another language), the Tools page now shows Finished with a neutral color and asks you to read the log, not a success.
- The Startup page now finds the right program for a command without quotes when a program extension appears inside a folder name or before more of the path. Before, it could show the file, signature and publisher of a different program.
- A change whose result Winora could not confirm (for example after a crash) shows as "Result unknown" on the History page. You can keep the current value, or put the earlier value back as a new change that you can undo. Records of preinstalled apps can only be closed. Neither choice turns the old record into an undoable change.
- A change that was interrupted is no longer marked as done just because the setting now has the expected value. It stays in History as interrupted and offers no Undo, so Undo cannot overwrite a value that another program set.
- A plan switch on the Power page is refused when another program switched plans in the meantime. A registry change group no longer treats a value that another program set during the change as its own.
- A Windows feature change is recorded once, after Winora has checked the result. If the history cannot be saved, the page now says so for Startup and Preinstalled apps too.
- Profile undo and the update modes find all their changes, however long the history is.
- A repair program whose state cannot be read is treated as still running instead of finished.
- A successful settings read is no longer listed as stopped in the Action center.
- Screen readers no longer read an internal type name for the notices above a long list and the change history below it.
- The History page lists every change, not only the newest 300. The count line reads "1 to 25 of 262 changes" (in Persian too), any filter or search change returns to page 1, and the list no longer scrolls inside the page.
- Keyboard and screen reader fixes: the search and pending popups can be reached by keyboard, dialog buttons have access keys, expandable sections report expanded or collapsed, progress indicators have names, and the Back button and chevrons point the right way in Persian.
- Programs installed for one account can be removed: their uninstaller runs as the signed-in user, never with administrator rights. A program whose uninstaller is in a protected Program Files folder can be removed when its install location is missing.
- Programs whose name starts with Winora are no longer locked as if they were Winora itself.
- The Disk usage map no longer makes the page slow or stop answering on a big drive. Moving the pointer over the map only redraws the highlight, big maps are drawn in the background, the file type totals are counted once per folder, and the list shows the first 1,000 items of a very large folder (the map still shows all of them).

- Screen readers now hear result messages, warnings and progress. On Startup, Scheduled Tasks and Apps, Space turns the row switch or tick box on or off, and the details dialog offers the row actions; buttons that repeat on every row say which item they act on.
- Pages look the same everywhere: one change history block, one page edge, one search box, one chip and tab style, one row menu icon, and delete buttons that turn grey when they cannot be used. Lists fill the window, Optimize cards keep their height, and the drive icon no longer looks like a spinner.
- An undo from a change history list now waits while its page is still writing, so it no longer stops a running profile or starts a second change. After a profile run or an undo on the History page, the Customize page, the History page and the other pages show the current values.
- A few undo results are clearer: Network no longer offers to undo an undo, the Profiles undo result lists settings by name, a failed or stopped undo is reported as failed or stopped, and the Overview card no longer names a Windows Update mode as the last profile.
- A change that Windows refused on the Power, Optimize, Updates and Bloatware pages is now shown as a warning instead of a green success message.
- The restore point question now shows its full button text.
- The choices of a setting that cannot be changed (no administrator rights) are readable in the dark theme.
- The optional feature list no longer stops after five minutes on a slow PC.

### Security
- Putting an earlier value back after an interrupted change now shows what is there now and what it will become before you confirm. If the value changes in the meantime, nothing is written. A restore waits while any adapter or item of the change cannot be read.
- A DNS change that wrote only one address family before failing is now recorded as partial and can be undone.
- Log files are opened as the file itself, so a link placed at the file name is refused instead of followed.
- The record of system changes is kept in a folder only administrators can change, and a Winora data folder made by another account is set aside and made again.

- Repair tools and uninstallers start with a fixed set of system values and their own temporary folder that only administrators can change. The checks before an uninstaller runs, the folders that keep Winora's records, and deleting files from the quarantine are stricter. Recorded paths no longer show your account folder.
