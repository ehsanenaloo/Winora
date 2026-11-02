# Localization and accessibility

Every user-visible label, warning and operation result is resource-backed English/Persian with English fallback. Stable machine codes remain in storage/reports and are rendered through resources in UI. Switching language updates existing bindings, CultureInfo and FlowDirection immediately; persist choice in application-owned preferences. Persian uses RTL; paths, identifiers and technical values stay LTR. Numbers/dates/sizes follow selected culture.

System/light/dark themes use WPF-UI. High contrast uses system colors; do not override contrast with fixed decorative colors. Keyboard access, focus visibility, accessible names and scalable layout are required. Initial shell includes eleven destinations and explicit unavailable messages, a simulated setting stage/review/apply flow and pending changes. Demonstration data and simulation must be clearly labeled. No fake health scores or performance gains.

Desktop STA tests may validate switching, fallback, direction and theme persistence. Human screen-reader, keyboard, DPI and visual QA remain separate release checks and must be reported honestly.

## Desktop UI baseline (UI redesign)

The Desktop shell is a WPF-UI `FluentWindow` (Mica, title bar, `NavigationView` with a compact pane below 1100 DIPs and a collapsed pane below the 820 DIP minimum) hosting five page kinds: Overview, Customize (catalog card with stage/review/apply/revert), History (timeline plus detail), Settings and a planned empty state for the seven unavailable destinations. Rules for later features:

- All user text is a `.resx` key (`Strings.resx` neutral English, `Strings.fa.resx` Persian). The desktop texts live in `src/Winora.Presentation/Resources` (the resource base name stays `Winora.Desktop.Resources.Strings`; the Persian satellite assembly is `fa/Winora.Presentation.resources.dll`). Persisted or logged codes stay untranslated; the UI renders them as `Code_<code with dots as underscores>` (the key of `ErrorCatalog.ResourceKey`). Every `ErrorCatalog` code must exist in both files; `ShellTests.EveryCatalogErrorCodeHasEnglishAndPersianText` enforces it.
- Colors come only from theme brushes; high contrast switches WPF-UI to its system-color dictionary and drops Mica. Spacing follows a 4 px grid, corners 8 px (cards) and 4 px (buttons), interactive controls are at least 40 px high.
- Right-to-left: `FlowDirection` follows the language; identifiers, paths, result codes and licence identifiers are LTR islands (`WinoraLtrText`). Dates use the selected culture; the window `Language` follows it for digit shaping.
- Failures go through `ErrorPresenter` only: exceptions are classified (shared `ErrorMapper`), logged once with a reference id, and shown as a localized InfoBar (warning for conflicts/unsupported, error otherwise) with `LiveSetting=Assertive`. Retryable transient failures offer Try again, conflicts offer Review again, critical severity and unhandled exceptions open a blocking dialog whose Copy diagnostics copies only the code, reference id and version. Cancellation is silent. Never show `Exception.Message`.
- Store calls run off the UI thread; one operation may be in flight, commands are disabled and a progress ring is shown while busy, and Apply is bound to the reviewed plan token so a stale review fails with `plan.stale` instead of applying.
- Unavailable destinations show a "Planned / Not available yet" state and expose no commands.

## M1 read-only inventory pages (Startup, Scheduled Tasks, Services, Applications)

These four pages share one view (`DiscoveryView`) and one base view model. The Applications page shows real, read-only Windows data and exposes no command that changes it; its page-level notice is "View only. Winora does not change these items yet." (`DiscReadOnlyTitle` + `DiscReadOnlyMessage`), and the simulation banner is not shown on these pages. The Startup, Scheduled tasks and Services pages are managers and carry no view-only notice (see [services.md](services.md)).

- Enum labels are resources named `Enum_<EnumType>_<Member>` (`LocalizationService.Enum`), for `StartupSource`, `StartupApproval`, `TaskRunState`, `ServiceStartMode`, `ServiceRunState`, `ServiceKind`, `InstallationScope`, `InstallationKind` and `RegistryViewKind`. `DiscoveryTests.EveryDiscoveryEnumMemberHasEnglishAndPersianText` fails when a member lacks English or Persian text. Stable ids and filter values stay untranslated.
- Page text uses prefixes `Disc*` (page chrome, states, partial results), `Filter*`, `Sort*`, `Field*`, `Badge*`, `Size*` and `Inv*` (Overview "System inventory"). Per-page search placeholders and empty states are `DiscSearch_<Page>`, `DiscEmptyTitle_<Page>` and `DiscEmptyBody_<Page>`.
- Partial results: the InfoBar states the number of unreadable entries and explains access denied in plain words (`DiscAccessDeniedHint`); other issue codes use the shared `Code_<code>` text through `ErrorPresenter`. Raw exception text and source key names are never shown.
- Dates use the selected culture ("g"); sizes are formatted from `EstimatedSizeKb` as KB, MB or GB with the culture's number format; counts use `N0`. Persian uses the Persian calendar through the `fa` culture.
- Identifiers, commands, paths, registry locations and service names are LTR islands (mono font, `FlowDirection=LeftToRight`, start aligned). Ordinary names and publishers follow the page direction, so mixed Latin/Persian names can reorder numbers in right-to-left rows; this is standard bidi behavior and is not corrected.
- Keyboard: arrow keys move through rows, Ctrl+F focuses the search box from anywhere in the window, Escape clears search, Enter or a double click on a row opens its details in a dialog (Escape or OK closes it and focus goes back to the row). Below a 900 DIP window width the filter row folds behind a "Filters" button.
- Search, sort, filters and counts are view-only state; none of it is persisted.

## M2 real settings (Customize and History)

- Catalog text keys: `Setting_<id with dots as underscores>_Title` and `_Description`, choice labels `Choice_<id underscored>_<choiceId>`, imported from technical-docs/settings-catalog.md into the Desktop and CLI resx files. Enum labels `Enum_SettingCategory_*`, `Enum_RiskLevel_*`, `Enum_RecoveryKind_*`. `SettingsUiTests.EveryCatalogDefinitionHasEnglishAndPersianText` and `SettingsCommandTests.EveryCatalogDefinitionHasCliText` fail when the real catalog gains a key without text.
- Page text uses prefixes `Set*` (cards, banner, search, review dialog, notices) and `Hist*` (history groups); `SetHist_<State>` is the history line of a real outcome.
- The persistent Customize banner is "These settings change your Windows user settings. Each change is reviewed first and can be reverted." The simulation banner is not shown on Customize or History; simulation rows in History are labelled "Simulation record" and grouped separately.
- Registry locations in the review dialog are LTR islands (mono font) inside right-to-left pages. Titles and choice labels follow the page direction.
- Outcome wording is state-specific: Applied (in effect), Pending restart (written, not in effect; says what must happen and that Winora did not do it), No change, Changed by something else (Winora did not overwrite), Needs review, and Stopped partway for bulk. Bulk text never claims all-or-nothing behavior.
- Two shared codes were reworded neutrally in the Desktop and CLI resx (`Code_operation_external_conflict`, `Code_plan_stale`) because the old text said "simulated value"; technical-docs/errors.md still quotes the old guidance.
- Persian text added in M2 is machine drafted; native review is still required.

## M3 UI (first run, About, diagnostics, accessibility, default state)

- New text groups: `Welcome*` (first-run screen), `Diag*` and `CopyPath*` (Settings diagnostics, copyable paths), `Shortcut*` (F1 dialog), `NavigationToggle*`, `BuildInfo*`, `NoticesFileLabel`, and the Customize default-state keys `SetNotSet`, `SetWindowsDefaultHint`, `SetNotSetHint`, `SetDefaultNotSetFormat`, `SetReviewWritesSame`, `SetReviewCreates`. Persian for all of them is machine drafted and needs native review (technical-docs/persian-review.md; re-export technical-docs/persian-review.csv, the new keys are not in the current file).
- The twelve `Planned_*` keys of Startup, Scheduled Tasks, Applications and Services were removed (those pages are real); Optimize, Cleanup and Power keep theirs. No test enforces specific `Planned_*` keys; the generic en-to-fa parity test still passes.
- Absent values: when the registry value is not stored and the catalog source note documents "Absent behaves as ..." (open_to, compact_view, alignment, clock_seconds, task_view_button), the card shows the default choice as current with the hint "Windows default (not set)". Every other unset value shows "Not set". Batch 2 adds taskbar.button_grouping, appearance.snap_bar, explorer.drive_letters, input.scrollbars and performance.auto_end_tasks to that list. Most other batch 2 rows have a choice that removes its values, so an unset row matches that choice. A grouped row with only some values stored shows "Custom value". The cards of rows with Conditional recovery show the text `SetConditionalNote` (Windows may set the value back). The categories Start, Input, Apps and Performance have `Enum_SettingCategory_*` texts in both languages. "Custom value" is only for a stored value that matches no authored choice. technical-docs/settings-catalog.md still says Winora lists no choice for Absent; the UI now maps documented absence to the default choice, the engine is unchanged (an absent value is not `AlreadyConfigured`, so choosing the default writes a DWORD and the review says so: "Winora will still write {value} = {value}").
- Right-to-left rows: Latin tokens (publisher, version) are pinned with left-to-right marks (`Bidi.Ltr`, `Bidi.Compose`) because WPF puts a number that follows a Latin word on the wrong side and ignores `LRE`/`LRI`; the separator is wrapped in right-to-left marks so two pinned tokens cannot merge into one run. Marks are display-only.
- Keyboard text lives in `Shortcuts.All` (keys are technical, descriptions are `Shortcut*` keys); the F1 dialog is built from it.
- Accessibility rules, coverage and the human checklist are in technical-docs/accessibility.md.

## Persian text: formatting, direction and terms

### One formatter (`LocalizationService`)

- `Number`, `Percent`, `Size` and `When` are the only ways view models turn a number, size or date into text. In Persian they give Persian digits, the Persian decimal and group marks ("۳٫۰۳", "۴٬۸۰۰"), the Persian percent sign ("۷۶٪") and the Persian calendar. A size is one string with its unit ("۱۶ گیگابایت"); a whole value has no decimals ("16 GB", "1.5 KB").
- `Format` turns number arguments into Persian digits. A number that is already text (a version, a build number, an address, an id) stays Latin and is passed through `Ltr`.
- `Plural(key, count, ...)` picks `key + "One"` for a count of one. English needs the singular form; the Persian "One" text may equal the plain one. `PersianTextTests` checks that every "One" text has its plain text and that both languages use the same placeholders.
- Day and time joins (`HistDayTime`), lists (`ListSeparator`, `ListSeparatorStrong`) and the breadcrumb arrow (`BreadcrumbSeparator`) are resources, so Persian uses "،", "؛" and "‹".

### Latin data in right-to-left text

- `Ltr(text)` pins Latin data with left-to-right marks in a right-to-left page and leaves it unchanged in English. Use it for paths, drive letters, URLs, addresses, versions, build numbers and hardware strings. `LtrList` joins such values with the list separator of the language.
- `Name(text)` is for names that can be Latin or Persian (programs, devices, plans, adapters): it pins only text without a right-to-left letter.
- List row titles bind `DiscoveryRow.DisplayTitle`, which pins Latin titles. The marks change nothing in a left-to-right page.
- Marks are display-only. Copied text (the This PC summary) goes through `Bidi.Strip`; search and stored values never carry marks.
- WPF ignores the Unicode isolates (U+2066 to U+2069) and the embedding characters, so the marks are the one tested way.

### Status words

Every history row, on the History page and in the change history block of a page, shows the word of its `HistoryResult`: Applied, Undone, Failed, Cancelled, Removed, Restored and Changed later. Pages map their own states to this set; they do not pass their own words.

### Glossary (Persian)

| Idea | Persian term | Do not use |
|---|---|---|
| profile | نمایه | پروفایل |
| undo (verb and noun) | برگرداندن، برگردانده شد، برگردانده‌شده | واگردانی، بازگردانی، بازگرداندن |
| can be undone | برگشت‌پذیر | قابل بازگردانی، قابل برگرداندن |
| cannot be undone | برگشت‌پذیر نیست | قابل برگشت نیست، قابل بازگشت نیست |
| restore (an app, a task, a file) | بازیابی، بازیابی شد، بازیابی کنید | بازگرداندن، برگرداندن (for an app) |
| applied | اعمال شد (verb)، اعمال‌شده (adjective) | other spellings |
| block (a permission) | مسدود کردن، مسدود (state) | بستن، بسته |
| file | فایل | پرونده |
| scan | پویش | اسکن |
| performance | کارایی | عملکرد |
| system | سیستم | سامانه |
| risk | ریسک، پرریسک، کم‌ریسک | خطر، پرخطر (for a risk level) |
| changes (plural noun) | تغییرها، تغییرهای | تغییرات |
| Start menu | استارت، منوی استارت | منوی شروع |
| built-in | پیش‌ساخته | داخلی |
| power plan, power | طرح انرژی، انرژی | طرح توان |
| Windows Update | به‌روزرسانی ویندوز | ویندوز آپدیت، Windows Update (in Persian text) |
| File Explorer | کاوشگر فایل | اکسپلورر، File Explorer، فایل اکسپلورر |
| brand | وینورا | Winora (in Persian text) |
| smooth scrolling | پیمایش نرم | اسکرول نرم |
| export diagnostics | ذخیره‌ی اطلاعات عیب‌یابی | برون‌بری |
| ezafe after "ه" | ه‌ی (with a zero-width non-joiner), for example صفحه‌ی اصلی، تاریخچه‌ی تغییرها | هٔ، a bare "ه" (صفحه اصلی) |

`PersianTextTests.Persian_texts_use_one_term_per_idea` fails when a rejected variant comes back in the Desktop, CLI or worker Persian resources.
