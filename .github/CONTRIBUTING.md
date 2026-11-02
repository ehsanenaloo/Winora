# Contributing to Winora

Thank you for wanting to help. Winora is a free, offline settings manager for Windows 11. You do not need to be an expert. A clear bug report or a better Persian string helps a lot.

By taking part, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to help

- Report a bug with the [bug report form](https://github.com/ehsanenaloo/Winora/issues/new?template=bug_report.yml). Search [existing issues](https://github.com/ehsanenaloo/Winora/issues) first.
- Ask for a Windows setting with the [setting request form](https://github.com/ehsanenaloo/Winora/issues/new?template=setting_request.yml).
- Suggest a feature with the [feature request form](https://github.com/ehsanenaloo/Winora/issues/new?template=feature_request.yml). Describe your problem before your solution.
- Fix Persian text. See [Persian text](#persian-text).
- Fix an issue and send a pull request.

Before you start on an issue, leave a short comment so two people do not do the same work. For a change with no issue, open one first. A typo or a small fix can go straight to a pull request.

## Project rules

- Winora works offline. It has no telemetry, no accounts and no network access of its own. A change that adds network traffic will not be accepted.
- Winora changes only items from its own fixed lists. There is no way to write any registry value or run any command, and there must never be one.
- Every change shows a review first and keeps an undo where Windows allows it.
- Write your own code and text. Do not copy code, text or lists from other tools.
- All text the user sees lives in resource files.

A change that breaks one of these rules needs a talk in an issue before any code.

## Safety

The app changes the real settings of the PC it runs on when you confirm a change. It asks for administrator rights. Try your changes in a virtual machine, never on the PC you work on. A script you add must never change real settings by itself.

## Build

You need Windows and the .NET SDK version in `global.json`. The first restore needs the internet.

```powershell
dotnet restore Winora.slnx --locked-mode
dotnet build Winora.slnx -c Release --no-restore
```

Warnings are errors, so a build with a warning fails. If you change a package, update the lock files in the same pull request and say why.

To run the app you built (in a virtual machine):

```powershell
dotnet run --project src/Winora.Desktop -c Release --no-build
```

The tests are not in this repository. The maintainer runs them on every pull request and tells you if something fails.

## Project layout

| Path | What it holds |
| --- | --- |
| `src/Winora.Domain` | Types and rules. No Windows code. |
| `src/Winora.Application` | Services. Uses the domain only. |
| `src/Winora.Infrastructure.*` | Windows adapters and the local data store. |
| `src/Winora.Presentation` | View models and the interface text. |
| `src/Winora.Desktop` | The desktop app. |
| `src/Winora.Cli` | The `winora` command-line tool. |
| `definitions/` | JSON lists of settings, profiles and policies. |
| `installer/` | The MSI projects. |

## Code style

- Every source file starts with `// SPDX-License-Identifier: GPL-3.0-only`.
- Nullable reference types are on and warnings are errors. Fix a warning; do not hide it.
- Keep the layers apart. The domain has no Windows code. The application layer uses the domain only.
- Match the names and style of nearby code. Keep changes small.
- Write comments only when the code is not clear on its own.

## Accessibility

Every interface change must work with the keyboard, a screen reader, high contrast, large scaling and the Persian right-to-left layout.

- Every action works with the keyboard, and the tab order follows the screen.
- Focus is always visible. A dialog opens with focus on the safe choice.
- Buttons, fields and meaningful icons have a name a screen reader can read.
- Paths and identifiers stay left-to-right inside Persian text.

## Adding a Windows setting

Open a [setting request](https://github.com/ehsanenaloo/Winora/issues/new?template=setting_request.yml) first and agree on it with the maintainer. Each setting needs a link to an official Microsoft page that describes it.

- A per-user setting goes in `src/Winora.Infrastructure.Windows/SettingCatalog.cs`.
- A machine-wide setting goes in a file in `definitions/machine/`.
- Add the title, description and choice labels to both resource files.
- Choose the risk level with care. A setting that can weaken security, stop a feature or is hard to undo is High.

## Interface text

The text is in these files:

- Desktop app: `src/Winora.Presentation/Resources/Strings.resx` (English) and `Strings.fa.resx` (Persian).
- Command-line tool: `src/Winora.Cli/Resources/Strings.resx` and `Strings.fa.resx`.

Add every new key to both files. If you cannot write the Persian text, copy the English and say so in the pull request. Do not build sentences by joining pieces.

## Persian text

1. Find the English text in `Strings.resx` to get the key. Find the same key in `Strings.fa.resx`.
2. Change only the value. Keep the key and placeholders such as `{0}` unchanged.
3. Use the half-space (نیم‌فاصله) where Persian needs it, Persian punctuation (، ؛ ؟) and «گیومه» for quotes. Keep product names, keys and paths in Latin letters.
4. Use the words Windows uses in Persian. Keep the meaning and every warning.
5. Build the app and check the page in Persian.

If you would rather not edit files, use the [translation fix form](https://github.com/ehsanenaloo/Winora/issues/new?template=translation_fix.yml).

## Pull requests

1. Fork the repository and create a branch from `main`, for example `fix/history-undo-label`.
2. Make small commits, one idea each.
3. Write each commit message as one plain sentence, such as `Fix the order of letter badges in Persian`.
4. Keep your branch current with `git rebase upstream/main`.
5. Open a pull request against `main` and fill in the template. Keep one topic per pull request.

Winora has one maintainer who works on it in spare time, so replies can take a week or two. The maintainer decides what is merged.

## Security

Do not open a public issue for a security problem. See [SECURITY.md](SECURITY.md).

## License

Winora is licensed under [GPL-3.0-only](../LICENSE). By sending a pull request, you agree that your change is licensed under the same terms and that you have the right to submit it.
