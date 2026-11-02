## Summary

<!-- What does this change and why? Keep it to one topic. -->

## Related issue

<!-- For example "Fixes #123". For a small typo or fix, write "None". -->

## Type of change

- [ ] Bug fix
- [ ] New or changed Windows setting
- [ ] New feature
- [ ] Translation
- [ ] Documentation
- [ ] Build, packaging or maintenance

## How I tested it

<!-- Say what you built and what you tried by hand, on which Windows build.
If you ran the app, say where: a virtual machine or your own PC, and the Windows build.
Name one failure case you tried, such as a cancelled review or a value changed outside Winora. -->

## Source (new or changed settings only)

<!-- Link to the official page the setting is based on, the risk level you chose and why. -->

## Screenshots

<!-- Optional. Add before and after images for visible changes. Hide your user name, computer name and paths. -->

## Translations only

<!-- Screens checked. Say whether you are a native speaker, fluent, or used a tool and checked it yourself. -->

## Checklist

- [ ] `dotnet build` passes with no warnings
- [ ] Tests use fakes or the sandbox registry key and never touch real Windows settings
- [ ] I added or updated a test, or I explained why the change cannot be tested
- [ ] The change is focused and does one thing
- [ ] Every new or changed string is in both `Strings.resx` and `Strings.fa.resx`
- [ ] The change works with the keyboard and in the Persian right-to-left layout
- [ ] A new or changed setting has an official source, a risk level and an undo, or the review says it cannot be undone
- [ ] User-visible changes are recorded in `CHANGELOG.md` under `[Unreleased]`
- [ ] The documents in `technical-docs/` are updated if behavior changed
- [ ] Screenshots, logs and test files contain no personal data
- [ ] I wrote this change or I have the right to submit it under the GPL-3.0-only license
