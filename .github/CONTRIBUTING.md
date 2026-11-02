# Contributing to Winora

Thank you for wanting to help. Winora is a free, offline settings manager for Windows 11. It changes settings from reviewed lists, shows a review before each change and keeps an undo. It gets better when people who use it report problems, fix text and send patches. You do not need to be an expert. A clear bug report or a corrected Persian string is a real contribution.

By taking part, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Contents

- [Ways to contribute](#ways-to-contribute)
- [Picking something to work on](#picking-something-to-work-on)
- [Project principles](#project-principles)
- [Workflow](#workflow)
- [Project map](#project-map)
- [Set up on Windows](#set-up-on-windows)
- [Checks](#checks)
- [Never touch real settings](#never-touch-real-settings)
- [Code style](#code-style)
- [Adding a Windows setting](#adding-a-windows-setting)
- [Adding or changing UI text](#adding-or-changing-ui-text)
- [Translations](#translations)
- [Safety and privacy changes](#safety-and-privacy-changes)
- [Pull request checklist](#pull-request-checklist)
- [Reporting bugs well](#reporting-bugs-well)
- [Security](#security)
- [Recognition](#recognition)
- [Maintainers and decisions](#maintainers-and-decisions)
- [Releases](#releases)
- [License](#license)

## Ways to contribute

- **Report a bug.** Use the [bug report form](https://github.com/ehsanenaloo/Winora/issues/new?template=bug_report.yml). Search [existing issues](https://github.com/ehsanenaloo/Winora/issues) first.
- **Ask for a Windows setting.** Use the [setting request form](https://github.com/ehsanenaloo/Winora/issues/new?template=setting_request.yml). Include a link to an official source.
- **Suggest a feature.** Use the [feature request form](https://github.com/ehsanenaloo/Winora/issues/new?template=feature_request.yml). Describe the problem you have before the solution you want.
- **Improve the Persian text.** Fix a wrong or awkward string. See [Translations](#translations). If you do not want to edit files, use the [translation fix form](https://github.com/ehsanenaloo/Winora/issues/new?template=translation_fix.yml).
- **Improve the docs.** Open an issue for errors, unclear steps or outdated screenshots in this repository.
- **Review pull requests.** Try a change in a virtual machine and say what you saw. Anyone can do this, and it helps a lot.
- **Fix an issue.** Pick one, say that you are on it, and open a pull request.

Not sure where to start? Open an issue and ask. It is fine to start small.

## Picking something to work on

These labels help you find work. The full list is in [labels.yml](labels.yml).

| Label | What it means |
| --- | --- |
| `good first issue` | Small, well described, and a good first change. The issue says where to look. |
| `help wanted` | The maintainer would like help and has agreed the change fits the project. |
| `setting-request` | A request for a new Windows setting. It needs an official source before work starts. |
| `persian` | Persian text or the right-to-left layout. Many of these need no coding. |
| `needs triage` | Not looked at yet. Please wait for a label before you start a large change. |

Before you start on an issue, leave a short comment such as "I would like to work on this." This avoids two people doing the same work. If you do not send a pull request within a few weeks, the maintainer may offer the issue to someone else. You can come back later.

If you want to change something that has no issue, open one first. For a typo or a small fix, you can send a pull request directly.

## Project principles

These guide every review.

1. **Offline.** No telemetry, no accounts, no ads, no auto-update and no network access of Winora's own. A change that adds network traffic will not be accepted.
2. **Closed lists only.** Winora writes only targets from its authored catalog and reviewed lists. There is no generic "write this registry value" or "run this command" path, and there must never be one.
3. **Review, journal, read-back, undo.** Every change shows a review first, is written to the journal before it happens and is read back after it. Undo never overwrites a newer change made outside Winora. A change that cannot be undone says so in the review.
4. **A source for every setting.** Each setting links to an official page that describes it. Its risk level is chosen with care. See [Adding a Windows setting](#adding-a-windows-setting).
5. **Never touch real settings by accident.** See [the rule below](#never-touch-real-settings).
6. **Original work.** Write your own code, text and data. Do not copy code, text, lists or descriptions from other tools, even when their license seems to allow it. Behavior that is documented by Microsoft is fine to implement.
7. **All text in resource files.** Every string the user sees is in `Strings.resx` and `Strings.fa.resx`. Stored outcomes use stable codes, never translated text.

A change that breaks one of these needs a discussion in an issue before any code.

## Workflow

1. **Fork** the repository on GitHub and clone your fork.

   ```powershell
   git clone https://github.com/<your-username>/Winora.git
   cd Winora
   git remote add upstream https://github.com/ehsanenaloo/Winora.git
   ```

2. **Create a branch** from the latest `main`. Use a short name with a prefix, such as `fix/history-undo-label`, `feat/taskbar-setting`, `docs/release-limits` or `l10n/fa-cleanup-page`.

3. **Make small commits.** One idea per commit. Each commit should build without warnings.

4. **Write the commit message as one plain sentence** in the imperative mood, starting with a capital letter and with no type prefix. Keep it under about 72 characters when you can. Examples:

   ```text
   Fix the right-to-left order of letter badges
   Add the taskbar end task setting with its source
   Keep the page header before the page content in the tab order
   Correct the Persian text of the History undo button
   ```

5. **Keep your branch up to date by rebasing.**

   ```powershell
   git fetch upstream
   git rebase upstream/main
   ```

   If you have already pushed the branch, update it with `git push --force-with-lease`. The maintainer may squash your commits when merging.

6. **Open a pull request** against `main`. Fill in the template: what changed, why, the related issue, and how you tested it. Mark it as a draft if it is not ready. Keep one topic per pull request. A small pull request is reviewed faster.

7. **Respond to review.** Push follow-up commits to the same branch. When a comment is fixed, say so in a short reply.

### What review looks like

The maintainer checks that the change fits the [principles](#project-principles), builds without warnings, passes the test suite, and is easy to read. You may get questions, requests for changes, or a suggestion to split the pull request. This is normal and is about the code, not about you.

Winora has one maintainer, who works on it in spare time. Replies are best effort. A first response often comes within a week or two, and sometimes it takes longer. If you have heard nothing after two weeks, a polite comment on the pull request is welcome.

## Project map

| Path | What it holds |
| --- | --- |
| `src/Winora.Domain` | Platform-neutral types and rules. No Windows code. |
| `src/Winora.Application` | Services and use cases. References the domain only. |
| `src/Winora.Infrastructure.Windows` | Windows adapters: registry, services, tasks, power, network, apps. The per-user setting catalog is `SettingCatalog.cs` here. |
| `src/Winora.Infrastructure.Persistence` | The SQLite journal and preferences. |
| `src/Winora.Presentation` | View models and the interface text (`Resources/Strings.resx`, `Resources/Strings.fa.resx`). |
| `src/Winora.Desktop` | The WPF desktop app. |
| `src/Winora.Cli` | The `winora` command-line tool, with its own resource files. |
| `src/Winora.Elevation.*`, `src/Winora.Worker` | The elevation channel and worker. See [protocol.md](../technical-docs/protocol.md). |
| `definitions/` | JSON definitions: machine settings, profiles, service and task policies, preinstalled app rules, DNS providers, optional features. |
| `installer/` | The MSI projects. |
| `tools/` | Packaging and verification scripts: `publish.ps1` and `verify_dist.ps1`. |
| `technical-docs/` | Design, test and release documents. Start at the [index](../technical-docs/README.md). |

Read [architecture.md](../technical-docs/architecture.md) for the layers and [operations.md](../technical-docs/operations.md) for the journal rules before a large change.

## Set up on Windows

You need:

- Windows 11 x64 (Windows 10 works for building, but Winora runs only on Windows 11),
- the .NET SDK 10.0.401, pinned in `global.json` (later patches are allowed),
- PowerShell 7 for the scripts in `tools/`,
- an internet connection for the first NuGet restore. Later work is offline.

Visual Studio is optional. Any editor works.

```powershell
dotnet restore Winora.slnx --locked-mode
dotnet build Winora.slnx -c Release --no-restore
```

Package versions are pinned in `Directory.Packages.props` and locked in `packages.lock.json` files. `--locked-mode` fails if a lock file does not match. If you change a package, update the lock files in the same pull request and say why.

To run the app you built:

```powershell
dotnet run --project src/Winora.Desktop -c Release --no-build
```

> **Warning:** the desktop app asks for administrator rights and changes your real Windows settings when you confirm a change. Try your changes in a virtual machine, not on the PC you work on.

## Checks

Run this before you push:

```powershell
dotnet build Winora.slnx -c Release --no-restore
```

Warnings are treated as errors, so a build with a warning fails.

The automated tests are kept in the maintainer's development repository and are not part of this repository. The maintainer runs the full test suite on every pull request before it is merged, and adds or changes tests where the change needs them. If a test fails, the maintainer tells you what failed and why.

Building is not enough on its own. In the pull request, say where you tried it (a virtual machine and its Windows build), what you clicked and what you saw. Include a failure case, such as a cancelled review or a value changed outside Winora before an undo.

## Never touch real settings

The app you build changes the real settings of the PC it runs on when you confirm a change. Try your changes in a disposable virtual machine, never on the PC you work on. Any script you add must never change real settings by itself. If you could not try a change safely, say so in the pull request, and the maintainer will try it on a virtual machine.

How the maintainer's tests keep away from real settings is described in [testing.md](../technical-docs/testing.md).

## Code style

- Every source file starts with `// SPDX-License-Identifier: GPL-3.0-only`. Keep it.
- Nullable reference types are on and warnings are errors. Fix a warning; do not hide it.
- Keep the layers: the domain has no Windows code, the application layer references the domain only, and adapters reference the application and the domain.
- Keep names meaningful and consistent with nearby code. Prefer a clear small change over a large rewrite.
- Comments explain why, not what.

### Accessibility

Every interface change should work for people who use a keyboard, a screen reader, high contrast, a high scale setting, or the Persian right-to-left layout.

- All actions work with the keyboard. Tab order follows the visual order.
- Focus is visible. When a dialog opens, the safe choice has focus, never the destructive one.
- Buttons and fields have a name that a screen reader can read. Icons that carry meaning have a text alternative.
- Paths, registry locations and identifiers stay left-to-right inside Persian text.

See [accessibility.md](../technical-docs/accessibility.md) and [localization.md](../technical-docs/localization.md).

## Adding a Windows setting

Open a [setting request](https://github.com/ehsanenaloo/Winora/issues/new?template=setting_request.yml) first and agree on it with the maintainer. A setting is added only with an official source, such as a Microsoft Learn page, a Group Policy reference or a Policy CSP page.

At a high level, a new setting needs:

1. **A definition.** A per-user setting goes into `src/Winora.Infrastructure.Windows/SettingCatalog.cs`. A machine-wide setting goes into a file in `definitions/machine/`. The definition has the target, the choices and their values, the default, the risk level, the recovery kind and the Windows builds it works on.
2. **A source.** Add an entry to [sources.md](../technical-docs/sources.md) and refer to it from the setting. Say what the source documents and what was only observed.
3. **The catalog document.** Add the row to [settings-catalog.md](../technical-docs/settings-catalog.md) for a per-user setting.
4. **Text in both languages.** Add the title, the description and a label for each choice to `Strings.resx` and `Strings.fa.resx`. The maintainer adds the tests that apply and undo the setting.

Choose the risk level honestly. A setting that can weaken security, stop a feature or is hard to undo is High, and its description says why. A setting that a Group Policy or MDM can manage must be detected as managed and not offered for change.

## Adding or changing UI text

All interface text lives in resource files, and no sentence is built by joining pieces.

- **Desktop app:** `src/Winora.Presentation/Resources/Strings.resx` (English) and `Strings.fa.resx` (Persian).
- **Command-line tool:** `src/Winora.Cli/Resources/Strings.resx` and `Strings.fa.resx`.

Rules:

- Add every new key to both files. If you cannot write the Persian text, copy the English text and say so in the pull request.
- Keep placeholders such as `{0}` and `{1}` the same in both languages. The maintainer's tests check this.
- A text that depends on a number has a second key ending in `One` for the singular.
- Error and outcome codes are stored untranslated. Their text uses the key `Code_<code with dots as underscores>`.

## Translations

The interface is in English and Persian. The Persian text was machine-drafted and has not been reviewed by a native speaker, so careful human fixes are very valuable.

### Fix a Persian string

1. Find the English text in `Strings.resx` to get the key, then find the same key in `Strings.fa.resx`.
2. Change only the value. Keep the key and the placeholders unchanged.
3. Use the half-space (ZWNJ, نیم‌فاصله) where Persian needs it, Persian punctuation (، ؛ ؟) and «گیومه» for quotes. Keep product names, keyboard keys and paths in Latin letters.
4. Build the app and look at the page in Persian.
5. Open a pull request. Name the page where you saw the string and say whether you are a native speaker.

If you would rather not edit files, open a [translation fix issue](https://github.com/ehsanenaloo/Winora/issues/new?template=translation_fix.yml) with the current text and your suggestion.

### Review many strings at once

`technical-docs/persian-review.csv` is a sheet with every English and Persian string side by side. You can review it in Excel or LibreOffice and send back the result. The steps are in [persian-review.md](../technical-docs/persian-review.md).

### Quality bar

- Use the words Windows itself uses in Persian where they exist.
- Keep the tone short, plain and friendly, like the English text.
- Keep the same meaning. Do not add or drop warnings, especially in reviews of risky changes.
- Use one word for one idea across the whole interface.
- Check long text in narrow places, such as buttons and the Action center.

### Native-speaker review

Say in the pull request which of these applies:

- "I am a native speaker of Persian."
- "I am fluent but not a native speaker."
- "I used a translation tool and checked the result myself."

All three are welcome. If a second Persian speaker can read the change, ask for their review in the pull request.

## Safety and privacy changes

Discuss these in an issue **before** you write code:

- anything that writes a new kind of target, such as a new registry area, a new service rule or a new program to start,
- any change to the review, the journal, the read-back or undo,
- any change to what is locked or protected,
- anything that adds network access, a new data file or a new place where data is stored,
- any change to what the diagnostics export contains.

If the change is accepted, the same pull request must update the documents in `technical-docs/`, such as [security.md](../technical-docs/security.md) and [operations.md](../technical-docs/operations.md).

## Pull request checklist

The pull request template has the same list.

- [ ] The change is focused, and the description says why it is needed.
- [ ] The pull request links the related issue, for example "Fixes #123".
- [ ] `dotnet build` passes with no warnings.
- [ ] Tests use fakes or the sandbox key and never touch real settings.
- [ ] I added or updated a test, or I explained why it cannot be tested.
- [ ] Every new or changed string is in both `Strings.resx` and `Strings.fa.resx`.
- [ ] The change works with the keyboard and in the Persian right-to-left layout.
- [ ] A new or changed setting has an official source and a risk level.
- [ ] User-visible changes are noted in [CHANGELOG.md](../CHANGELOG.md) under `[Unreleased]`.
- [ ] The documents in `technical-docs/` are updated if behavior changed.
- [ ] Screenshots, logs and test files contain no personal data.
- [ ] I wrote this change or I have the right to submit it (see [License](#license)).

## Reporting bugs well

A good report lets someone else see the problem in a few minutes. Include:

- the Winora version (shown in **About Winora**) and your Windows edition and build (run `winver`),
- whether you use the installer, the portable zip or your own build,
- the page and the action that failed,
- what you expected and what happened, including the result text on the **History** page,
- the smallest set of steps that shows the problem,
- the diagnostics archive from **Settings > Export diagnostics**. Personal data is removed from it, but read it before you attach it.

Never post product keys, user names, computer names or network addresses in a public issue.

## Security

Do not open a public issue for a security problem. Follow [SECURITY.md](SECURITY.md) and use GitHub's private vulnerability report. For general help, see [SUPPORT.md](SUPPORT.md).

## Recognition

Every person whose commits are merged into `main` is credited by GitHub in the contributors list. That list needs your commits to be linked to your GitHub account, so check that your Git email address is added to your GitHub profile.

Work that does not show up as commits is credited in the release notes. This includes Persian reviews, documentation, bug reports with good steps, and reviews. You are named by your name or GitHub username, unless you prefer not to be. Tell the maintainer in the issue or pull request.

## Maintainers and decisions

Winora is maintained by [Ehsan Enaloo](https://github.com/ehsanenaloo). The maintainer makes the final decision on what is merged, using the [project principles](#project-principles) and the test results.

If you disagree with a decision, say so in the issue. Give your reasons and your use case, and listen to the reply. Please keep the discussion respectful and about the work, as the [Code of Conduct](CODE_OF_CONDUCT.md) asks. A "no" to a feature is not a judgment about you. Often it means the idea needs network access or a generic write path that this project does not want. You are always free to fork the project under the GPL-3.0-only license.

## Releases

Releases are made by the maintainer. `tools/publish.ps1` builds the portable zip, the MSI, a `SHA256SUMS` file and a package list, and `tools/verify_dist.ps1` checks them. Releases are not code-signed yet. The plan is in [signing.md](../technical-docs/signing.md), and the release steps are in [release.md](../technical-docs/release.md).

## License

Winora is released under the [GPL-3.0-only](../LICENSE) license. When you contribute, your contribution is licensed under the same terms.

By opening a pull request, you confirm that you wrote the change yourself, or that you have the right to submit it under GPL-3.0-only. Do not copy code, text, lists or images from sources that do not allow this. No `Signed-off-by` line is required.
