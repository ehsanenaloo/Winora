# Accessibility, scaling and high contrast (Desktop)

Scope: the WPF Desktop shell and its eleven destinations, in English and Persian (RTL), light, dark and high contrast. This document says what automated tests prove and what a human must still check on real assistive technology and real hardware. Nothing here is a claim of conformance: automated checks read UI Automation peers, layout and rendered pixels, they do not replace Narrator, NVDA, a real Windows contrast theme or a real 150%/200% display.

## What the automated tests cover

All of it runs in `tests/Winora.Desktop.Tests` against fakes (no registry, shell, clipboard, folder dialog, exporter or session is touched). Screenshots and tables are written to `artifacts/ui-m3-*` when `WINORA_TEST_ARTIFACTS` is set.

| Area | Test (file) | What is asserted |
| --- | --- | --- |
| UI Automation tree | `AccessibilityTests.EveryInteractiveElementOnEveryPageInBothLanguagesHasNameRoleHelpAndLabel` | For all 11 pages x en/fa, every visible focusable button, combo box, text box, list, check box and navigation item has a non-empty automation name; a specific control type (never Custom/Pane/Group; ComboBox, Edit, CheckBox, Button, List where applicable); help text for every action that is not self-evident (all buttons, inputs and lists); `LabeledBy` for every editable input and combo box; read-only path boxes have a name. |
| Template-owned controls | `TemplateOwnedFocusTargetsAreNamedToo` | The navigation pane toggle (a WPF-UI template part that ships unnamed) now has a name and help text; no focusable control in the window is unnamed. |
| Headings and regions | `PagesHaveOneLevel1HeadingMatchingTheirNameAndSectionHeadings` | Exactly one `AutomationHeadingLevel.Level1` per page, equal to the page name, focusable; level-2 section headings on pages with sections; heading precedes the first control; page root and navigation are named. WPF has no UIA landmark property, so named regions are the available equivalent. |
| Live regions | `OutcomeSurfacesAreLiveRegionsPoliteForInfoAndAssertiveForErrors` | Outcome bar, settings notice and diagnostics notice are live regions: polite for informational/success, assertive for warning/error (bound to severity); failure states are assertive; progress text is polite. |
| Live regions are announced | `LiveRegionTests.A_live_text_is_announced_when_it_appears_and_when_it_changes` | WPF only stores the live setting; `Controls/LiveRegions.cs` raises LiveRegionChanged for every element with a live setting when it becomes visible or its text or name changes (banners, results, progress lines, toasts, the title bar search announcement). Hidden and closed elements are not announced. |
| Row actions by keyboard | `DetailsDialogTests.Space_on_a_startup_row_stages_its_switch`, `Scheduled_task_details_offer_run_and_delete_for_keyboard_users`, `Quarantine_details_offer_restore_and_delete_for_keyboard_users`, `SettingsPageTests.The_context_menu_key_opens_the_details_with_the_row_menu_actions` | Row buttons and switches are not tab stops in long lists. Space on a row flips its switch or tick box (Startup, Scheduled tasks, Apps); the details dialog (Enter) offers the row actions (Run or Stop, Delete, Restore, Reset); the context menu key or Shift+F10 opens a setting's details. |
| Item names on repeated buttons | `LiveRegionTests.A_repeated_button_is_named_with_its_item` | A button that repeats on every row or card is named "label, item" (for example "Export, Gaming"); `Controls/A11y.cs` composes the name and follows a language change. |
| Toasts with an action | `LiveRegionTests.A_toast_with_an_action_stays_until_it_is_closed` | A toast with an action button stays until it is closed (WCAG 2.2.1); others close after 6 seconds. |
| Motion carries nothing | `EveryProgressRingHasAnAccessibleNameAndVisibleTextSoMotionCarriesNothing`, `ReducedMotionTurnsPageTransitionsOffAndKeepsEveryStateInText` | Every busy ring has a name and the visible word "Working"; page transitions are switched off when `SystemParameters.ClientAreaAnimation` is off (and re-applied on change); no XAML defines a storyboard. |
| Page states and inventory rows | `StatesAndRowsTests`, `UiConsistencyTests` | Pages that read data show the shared LoadingSkeleton (polite, named by its text), ErrorState (assertive, one named Try again button) and EmptyState (polite, named by its headline). Inventory rows are not tab stops: the list item or the row control takes focus, the stripe is decoration and the selection is also exposed through `SelectionItemPattern`. Locked rows say "Part of Windows" or "Protected by Winora" in a Managed badge whose text is its UIA name. |
| Tab order | `TabOrderFollowsTheVisualOrderWithoutTrapsOnEveryPage` | Real `MoveFocus(Next)` on an activated window: the cycle closes on its first element (no trap), no element twice, only visible/enabled elements, navigation first then content, and inside the content the order follows reading order (top to bottom, and left to right in English / right to left in Persian; master-detail may move to a new column). Every stop is nameable. Pages with nothing to operate (planned pages, empty history) have no content stop by design. |
| Focus management | `NavigationMovesKeyboardFocusToThePageHeadingAndBackReturnsToThePreviousPage` | After navigating (also after Alt+Left) keyboard focus is on the new page heading. |
| Dialogs | `DialogsCloseWithEscapeKeepEnterOnTheDefaultButtonAndReturnFocusToTheInvokingControl` | Tab stays inside an open dialog; Escape closes it as "Back" (nothing applied); focus returns to the control that opened it (or the page heading when that control is gone); global shortcuts are ignored while a dialog is open. WPF-UI dialogs do not handle Escape themselves, Winora does. |
| Shortcuts | `ShortcutMatchingRecognisesExactlyTheDocumentedGlobalKeys`, `GlobalShortcutsDoTheirJobAndF1ListsTheDocumentedSet`, `F6_moves_between_navigation_the_page_the_toast_and_the_Action_center_and_Shift_F6_goes_back` | Ctrl+F search, F5 refresh the page, Alt+Left back, Ctrl+, Settings, F1 dialog, Ctrl+Enter review, F6 and Shift+F6 regions; the dialog lists exactly `Shortcuts.All` in both languages (also Esc, Enter, Tab rows). |
| Contrast, light and dark | `ContrastTests.TokensMeetWcagInLightAndDark` | WCAG ratios from rendered pixels of the real controls: text 4.5:1 and component boundaries/focus 3:1. See the table below. |
| High contrast | `HighContrastTests.*` | `Styles/HighContrast.xaml` contains only `SystemColors` references and `Transparent` (no literal color); it covers every brush WPF-UI defines for high contrast and every brush Winora's XAML asks for; text, controls and chips are readable in four simulated contrast palettes; the window drops Mica, resolves its brushes to `SystemColors`, and every chip looks the same so text carries the meaning. |
| Layout at 820 x 600 | `LayoutScaleTests.SmallestWindowAtEveryTextScaleInBothLanguagesHasNoClippedChromeAndEverythingIsReachable` | 6 pages x en/fa x 100/125/150/200% content scale: chrome text is not arranged smaller than it needs, trimmed or outside its card; buttons are not narrower than their content; no control hangs over the window edge; no horizontal overflow; a page taller than the window shows a scroll bar and scrolls to its end. Also the partial-results page (`PartialResultsBarAndListStayReachableOnAShortWindowAndAtLargeText`), Overview cards (`OverviewCardsShareTheWidthInsteadOfKeepingAFixedSize`) and scroll position (`ScrollPositionNeverCarriesOverFromOnePageToTheNext`). |
| Right-to-left bidi | `BidiTests.*` | Latin names, versions and numbers inside Persian rows keep their own order (see below). |
| Popups by keyboard | `A11yRestTests.Tab_from_the_search_box_moves_into_the_results_and_Escape_closes_them_and_returns_focus`, `ActionCenterUiTests.At_1000_and_below_the_pane_is_a_drawer_with_a_backdrop_that_Esc_and_the_backdrop_close` | Tab in the title bar search box moves into the results (the "Show all" button); Escape closes the results and returns focus to the box. The pending list of the action bar takes focus when it opens, the arrow keys move inside it and Escape closes it and returns focus to its button. |
| Access keys | `A11yRestTests.Dialog_buttons_have_different_access_keys_in_English_and_none_in_Persian`, `Review_and_Discard_of_the_Action_center_get_two_letters_in_English_and_none_in_Persian`, `Buttons_of_two_with_the_same_first_letter_get_different_keys`, `Page_header_actions_get_letters_and_keep_following_a_bound_text` | Dialog buttons (primary, secondary, close), the action bar buttons and the page header actions show an underlined letter and answer to Alt plus that letter. Letters are chosen from the text and never repeat inside one group. Right-to-left pages have none (see below). |
| Expand and collapse | `A11yRestTests.A_chevron_toggle_reports_expanded_and_collapsed_and_no_pressed_state`, `The_page_history_toggle_keeps_its_id_and_reports_expand_and_collapse`, `An_expand_button_reports_its_state_and_still_invokes` | The chevron toggle and the history row button report ExpandCollapse (expanded or collapsed), not pressed. The page history toggle keeps its id `HistoryToggle`. |
| Disk page | `A11yRestTests.A_disk_card_says_it_is_selected_in_its_name`, `The_treemap_is_a_custom_control_with_a_localized_type_not_an_image` | The chosen disk card says "selected" in its name. The treemap peer is a custom control typed "disk usage map" with the folder and size as its name and the selected folder as its item status. |
| Names and labels | `A11yRestTests.An_unnamed_progress_bar_gets_a_name_and_a_named_one_keeps_its_own`, `The_search_group_title_is_a_heading_and_the_filter_groups_are_labelled_by_their_text` | A progress bar nobody named is called "Progress"; search group titles are level 2 headings; the risk and source filter groups are labelled by their text. |
| Toggle look and focus | `A11yRestTests.The_filter_toggle_uses_the_Winora_toggle_with_a_two_pixel_focus_border`, `The_selected_search_result_has_a_two_pixel_focus_border`, `The_check_mark_on_an_accent_swatch_is_black_on_light_colors_and_white_on_dark_ones`, `No_style_lowers_the_opacity_of_a_chip_or_label_except_when_the_control_is_disabled` | One toggle button style for the filter bar and the action bar; a 2 px focus border on the keyboard-focused toggle and on the selected search result; the check mark on an accent swatch is black or white, whichever has the higher contrast; no XAML lowers opacity outside disabled states. |
| Right to left | `A11yRestTests.The_Back_symbol_and_the_Back_shortcut_follow_the_direction`, `In_a_Persian_window_the_first_run_Back_arrow_points_right_and_Alt_Right_goes_back`, `The_closed_group_chevron_points_to_the_text_it_opens_in_both_directions` | The first run Back arrow, the group chevron (rendered pixels, both directions) and the Back shortcut follow the reading direction. |

### Contrast results (rendered, light and dark)

Lowest measured ratios after the M3 fixes (`artifacts/ui-m3-contrast-light-dark.tsv` has every row):

- Text: primary 12.8 to 17.1, secondary 6.1 to 10.2, caution text 5.0 to 12.3, text on accent 5.6 (light) and 10.9 (dark), status chips 5.6 to 12.2, all at or above the 4.5:1 requirement.
- Components: control border token 4.5:1 (light) and 4.1:1 (dark) on the card surface (3:1 required); button, text box, combo box, check box and segmented-item boundaries all measured 4.1:1 or better; large icon graphics 3.3:1 (light).
- Fixed in M3 (they failed before): control borders were 1.1:1 to 1.2:1; light status chips were 2.2:1 to 3.7:1 (green, blue, amber, red with white text). `Styles/ThemeLight.xaml` and `ThemeDark.xaml` now supply the border and chip tokens and `App.xaml` has the chip style (a replacement `ui:Badge` template, because WPF-UI bakes the chip colors into its template).
- Simulated high contrast (four palettes, 27 pairs each): lowest 5.3:1 (disabled text on window), everything else higher. These are simulated palettes derived from the ones WPF-UI ships; they are not Windows' own values.

### High contrast resource path

`MainWindow.ApplyTheme` switches to the high-contrast path whenever `SystemParameters.HighContrast` is true: the backdrop is `None` (no Mica), WPF-UI is applied with its high-contrast theme as a base, and `ThemeOverlay` merges `Styles/HighContrast.xaml` last. That file maps all 384 brush keys to `Window`, `WindowText`, `Highlight`, `HighlightText`, `ControlText`, `HotTrack` or `GrayText` through `DynamicResource`, so a user's custom contrast theme is followed live. WPF-UI writes its accent brushes straight into the application dictionary, which outranks merged dictionaries; the overlay therefore removes those root keys while high contrast is on (the next normal theme apply writes them again). Chips lose their color in high contrast: every chip is the same outline, and the label text ("Low risk", "Applied", "Not set") carries the meaning in every theme. Test hook: `MainWindow.HighContrastOverride` forces the path without touching the Windows setting.

### Text size and scaling

Winora is per-monitor DPI aware (manifest). Layout is DIP based, so 125/150/200% displays shrink the available DIPs rather than change the layout; the 820 x 600 minimum is judged in DIPs. "Text size" in Windows Accessibility is honored through `ITextScaleSource` (reads, never writes, `HKCU\Software\Microsoft\Accessibility\TextScaleFactor`, 100% to 225%) as a layout zoom of the whole content area: it is not a font-only change, everything stays proportional and reflows into the room left (the navigation pane collapses when the effective width drops below 820). The test source is a fake; the real registry value is never read in tests.

### Keyboard shortcuts (documented set, `Shortcuts.cs`)

| Keys | Action |
| --- | --- |
| Ctrl+F | Focus the search box of the current page |
| Ctrl+K | Focus the search box in the title bar (settings, pages, profiles and tools); Esc closes it and gives focus back |
| F5 | Refresh the current page (read-only refresh; Overview scans) |
| Alt+Left (Alt+Right on a right-to-left page) | Go back to the previous page |
| Alt+Right (Alt+Left on a right-to-left page) | Go forward to the page you left with Back |
| Ctrl+, | Open Settings |
| Ctrl+1 to Ctrl+5 | Open the first page of Home, Tune, Clean, Manage or Tools |
| Ctrl+Z | On the History page only, outside a text box: review the undo of the newest change that can be undone |
| F1 | Show the shortcut list (also Settings > Keyboard and help) |
| Ctrl+Enter | Review and apply the staged changes (it opens the review of the page named in the Action center; it never applies by itself) |
| Ctrl+Shift+A | Open or close the Action center |
| F6 / Shift+F6 | Move between the navigation, the page, a notification and the Action center (while it is open) |
| Esc | Close a dialog (as Back), close the details dialog or clear the search box |
| Enter | Open the selected row's details, or press the focused button |
| Space | On a Startup, Scheduled tasks or Apps row: flip its switch or tick box |
| Context menu key or Shift+F10 | On a setting row: open its details with Reset and Revert last |
| Tab / Shift+Tab | Next / previous control |

### Access keys

Dialog buttons, the Action center buttons (Review, Discard) and the page header actions show one underlined letter (Alt plus the letter presses the button). `Controls/AccessKeys.cs` picks the letter from the current text: the first Latin letter that no other button of the same group uses. Two groups can share a letter (a dialog over a page); WPF then cycles between the targets on repeated presses. Pages that read right to left have no access keys: Persian text has no Latin letter to pick, an underline inside joined letters breaks the word shape, and Alt plus a Persian letter depends on the keyboard layout. The accessible name of a button never carries the underscore. The empty and error state buttons and the buttons inside page bodies have none yet.

### Popups by keyboard

The results of the title bar search are a popup. Tab moves keyboard focus into them, Tab and the arrow keys move inside, Escape closes them and puts focus back on the control that opened them. The search box keeps arrow-key selection of the rows while focus stays in the box.

## What still needs a human (precise checklist)

Do each on a physical or VM Windows 11 build from technical-docs/manual-test-plan.md and record the result there.

1. **Narrator**: start Narrator, open each of the 11 pages in English and Persian. Check the page name and heading are announced on navigation; a Customize card is read as "title, current value" with the help text; the segmented choice reads as a list with the selected item; a review dialog reads its title, both values and the write note ("Winora will still write ...") before the buttons; the outcome bar interrupts for an error and does not interrupt for a success; the progress text "Working" is read. Persian: confirm Narrator has a Persian voice (otherwise it spells the text) and that numbers and Latin names are read in a sensible order.
2. **NVDA** (current release): repeat the Customize apply, a failed export (Diagnostics) and the first-run screen; confirm the live regions fire once, not repeatedly, and that browse mode reaches every heading with `H`.
3. **Real Windows high contrast**: Settings > Accessibility > Contrast themes: Aquatic, Desert, Dusk, Night sky, then toggle with Left Alt + Left Shift + Print Screen while Winora runs. Check every page and every dialog; no invisible text, no unreadable disabled text, focus visible, chips distinguishable by text, no Mica. This is the one item the simulated palettes cannot replace.
4. **Real DPI**: 100%, 125%, 150%, 200% and a mixed-DPI two-monitor setup (drag the window between them); fonts crisp, nothing clipped at 820 x 600, dialogs fully on screen, title bar buttons usable. Known risk: at 200% on a 1080p screen the work area is about 960 x 500 DIPs, shorter than the 600 DIP minimum height, so the bottom of the window can fall below the taskbar; pages scroll but the window frame does not fit.
5. **Text size**: Settings > Accessibility > Text size at 150% and 225%: Winora follows it as a zoom; check it applies after changing the slider while Winora is open, and that the navigation pane collapses and is operable by keyboard.
6. **Keyboard only**: complete a full Customize flow (search, stage, review, apply, revert) and a diagnostics export without the mouse; open every dialog and close it with Escape; confirm the focus indicator is visible everywhere and on a Persian page.
7. **Reduced motion**: turn Windows animation effects off and confirm no page transition runs; switch back on and confirm they return.
8. **Folder dialog**: the standard Windows folder dialog is a seam and was never shown by tests; confirm it opens, is keyboard operable and its cancel path shows "No folder was chosen".

## Known limitations

- Text cut with an ellipsis on a setting row is available in full in the details dialog (Enter or the context menu key); UI Automation exposes the full text. The tooltip is a mouse extra only.
- The result title of a tool run (Tools page) is not marked as a heading yet.
- WPF-UI busy rings have no UI Automation peer; every ring in the pages carries a name and the word "Working" next to it (checked by `EveryProgressRingHasAnAccessibleNameAndVisibleTextSoMotionCarriesNothing`).
- Navigation items report the UIA control type `TabItem` (WPF-UI's `NavigationViewItem`); screen readers announce them as tabs. They are individually focusable (about 11 tab stops) rather than one arrow-key group.
- The tabs of a page are one tab list (the tab rail in the fixed header): UIA control type `Tab`, each tab a `TabItem` with a name, and the help text "Switch between the parts of this page with the arrow keys." Left and Right (reversed in right-to-left), Home and End move between the tabs and select them; Tab enters the list on the selected tab. The previous and next buttons that appear when the row is too wide are not focus targets (the keys do the same). The content area under the rail is not announced as a second tab list. Checked by UI Automation peers in `PageWorkspaceTests`; no screen reader session has been done.
- A long list is the one scroll area of its page, and the notices above its rows and the history of changes below them are the first and last items of the list. The history is reached by scrolling to the end of the list; the keyboard path into its buttons was not checked with a screen reader.
- The title bar's minimize, maximize and close buttons come from the WPF-UI template; the tests do not name them (they are not focus targets in the tab order) and no human check has been done.
- In the compact navigation pane at large zoom the menu items scroll inside the pane; every item stays reachable by Tab (focus scrolls it into view) but the layout was only inspected in screenshots.
- Persian text is machine drafted and needs native review (technical-docs/persian-review.md); the bidi fix covers rows with Latin publisher/version tokens (Applications); other mixed strings were checked in screenshots only.
- `LRM`/`RLM` marks are used instead of the Unicode embedding/isolate characters because WPF 10 renders `LRE`, `RLE` and `LRI` without effect (verified by rendering); marks are display-only and never stored, searched or copied.

The title bar starts with four buttons, in reading order: Menu (help, about and support), Sidebar (shows or hides the list of pages), Back and Forward. Each has a name and a tooltip card with its keys; on a right-to-left page the order, the arrows and the panel icon are mirrored. Tests: `TitleBarTests`.
