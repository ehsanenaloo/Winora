# Per-user setting catalog

Source of truth is `src/Winora.Infrastructure.Windows/SettingCatalog.cs`; this table mirrors it and is checked by hand at review. All targets are under `HKCU` (64-bit view). Provenance refs and their evidence class are in [sources.md](sources.md). DWORD values are decimal. A cell with several values lists one value per member, in member order.

Abbreviations: CV = `Software\Microsoft\Windows\CurrentVersion`, ADV = `CV\Explorer\Advanced`. "removed" means the choice deletes the value. A row of several members is a group: a choice sets all members together, undo restores each member exactly, and a state where only some members match a choice is shown as a custom value. "set bit" and "clear bit" change only that bit of a number that is already stored (read, change, write); the number is a decimal string for the accessibility flags. "key" and "key, empty default" are rows whose effect is the presence of a registry key; the key is deleted only when it holds nothing but what Winora created. "Absent" means the value is normally missing on a default profile and behaves as the default; Winora lists a matching choice only where the table shows one, and shows "Not set" otherwise.

Every row has Version 1, scope current user and no elevation. The shell refresh column lists the extra fixed actions of a row: P is SPI_SETMOUSE, K is the keyboard repeat calls, A is the Sticky, Filter and Toggle keys calls, I is the "intl" broadcast, E is the "Environment" broadcast. All of them are owner approval pending. Rows whose recovery is Conditional hold values that Windows may set back, for example after an update. Resource keys: title `Setting_<id with _>_Title`, description `..._Description`, choice label `Choice_<id with _>_<choiceId>`. Persian text is machine-drafted and needs native review (technical-docs/persian-review.csv).

Not shipped, with the reason: taskbar button size (two mechanisms by build, one unverified), visual effects set (the registry value alone does not change the effects), Num Lock at sign-in (needs the logon-screen hive), taskbar last active click, Share drag tray (no source for the effect).

| Id | Category | Risk | Recovery | Builds | Target (SubKey \ Value) | Choices (value) | Default | Recommended | Sign-out | Explorer restart | Shell refresh | Source |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| explorer.show_file_extensions | Explorer | Low | Reversible | 22000+ | ADV \ HideFileExt | show=0, hide=1 | hide | show | no | no |  | SRC-M2-01 |
| explorer.show_hidden_files | Explorer | Low | Reversible | 22000+ | ADV \ Hidden | shown=1, hidden=2 | hidden | none | no | no |  | SRC-M2-01 |
| explorer.open_to | Explorer | Low | Reversible | 22621+ | ADV \ LaunchTo | home=2, this_pc=1, downloads=3, onedrive=4 | home | none | no | no |  | SRC-M2-01, SRC-W1-09 |
| explorer.compact_view | Explorer | Low | Reversible | 22621+ | ADV \ UseCompactMode | comfortable=0, compact=1 | comfortable | none | no | no |  | SRC-M2-01 |
| taskbar.alignment | Taskbar | Low | Reversible | 22000+ | ADV \ TaskbarAl | center=1, left=0 | center | none | no | no |  | SRC-M2-02 |
| taskbar.clock_seconds | Taskbar | Low | Reversible | 22000+ | ADV \ ShowSecondsInSystemClock | hidden=0, shown=1 | hidden | none | no | no |  | SRC-M2-02 |
| taskbar.search_mode | Taskbar | Low | Conditional | 22621+ | CV\Search \ SearchboxTaskbarMode | hidden=0, icon=1, box=2, icon_label=3 | icon | none | no | no |  | SRC-M2-02, SRC-W1-05 |
| taskbar.task_view_button | Taskbar | Low | Reversible | 22000+ | ADV \ ShowTaskViewButton | shown=1, hidden=0 | shown | none | no | no |  | SRC-M2-02 |
| privacy.advertising_id | Privacy | Low | Reversible | 22000+ | CV\AdvertisingInfo \ Enabled | enabled=1, disabled=0 | enabled | disabled | no | no |  | SRC-M2-03 |
| privacy.tailored_experiences | Privacy | Low | Reversible | 22000+ | CV\Privacy \ TailoredExperiencesWithDiagnosticDataEnabled | enabled=1, disabled=0 | disabled | disabled | no | no |  | SRC-M2-03 |
| search.web_results | Search | Low | Conditional | 22000-22631 | CV\Search \ BingSearchEnabled | enabled=1, disabled=0 | enabled | none | yes | no |  | SRC-M2-04 |
| appearance.apps_theme | Appearance | Low | Reversible | 22000+ | CV\Themes\Personalize \ AppsUseLightTheme | light=1, dark=0 | light | none | no | no |  | SRC-M2-05 |
| appearance.system_theme | Appearance | Low | Reversible | 22000+ | CV\Themes\Personalize \ SystemUsesLightTheme | light=1, dark=0 | light | none | no | no |  | SRC-M2-05 |
| appearance.window_snapping | Appearance | Low | Conditional | 22000+ | Control Panel\Desktop \ WindowArrangementActive | on="1", off="0" | on | none | yes | no |  | SRC-M2-06 |
| desktop.icon_this_pc | Desktop | Low | Reversible | 22000+ | CV\Explorer\HideDesktopIcons\NewStartPanel \ {20D04FE0-3AEA-1069-A2D8-08002B30309D} | shown=0, hidden=1 | hidden | none | no | yes |  | SRC-C1-01 |
| desktop.icon_recycle_bin | Desktop | Low | Reversible | 22000+ | CV\Explorer\HideDesktopIcons\NewStartPanel \ {645FF040-5081-101B-9F08-00AA002F954E} | shown=0, hidden=1 | shown | none | no | yes |  | SRC-C1-01 |
| desktop.icon_user_files | Desktop | Low | Reversible | 22000+ | CV\Explorer\HideDesktopIcons\NewStartPanel \ {59031a47-3f72-44a7-89c5-5595fe6b30ee} | shown=0, hidden=1 | hidden | none | no | yes |  | SRC-C1-01 |
| desktop.icon_network | Desktop | Low | Reversible | 22000+ | CV\Explorer\HideDesktopIcons\NewStartPanel \ {F02C1A0D-BE21-4350-88B0-7367FC96EF3C} | shown=0, hidden=1 | hidden | none | no | yes |  | SRC-C1-01 |
| desktop.icon_control_panel | Desktop | Low | Reversible | 22000+ | CV\Explorer\HideDesktopIcons\NewStartPanel \ {5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0} | shown=0, hidden=1 | hidden | none | no | yes |  | SRC-C1-01 |
| desktop.all_icons | Desktop | Low | Reversible | 22000+ | ADV \ HideIcons | shown=0, hidden=1 | shown | none | no | yes |  | SRC-C1-02 |
| explorer.item_checkboxes | Explorer | Low | Reversible | 22000+ | ADV \ AutoCheckSelect | off=0, on=1 | off | none | no | no |  | SRC-M2-01 |
| explorer.sync_notifications | Explorer | Low | Reversible | 22000+ | ADV \ ShowSyncProviderNotifications | shown=1, hidden=0 | shown | hidden | no | no |  | SRC-M2-01 |
| taskbar.end_task | Taskbar | Low | Reversible | 22631+ | ADV\TaskbarDeveloperSettings \ TaskbarEndTask | off=0, on=1 | off | none | no | no |  | SRC-C1-03 |
| taskbar.battery_percentage | Taskbar | Low | Reversible | 22621+ | ADV \ IsBatteryPercentageEnabled | hidden=0, shown=1 | hidden | none | no | no |  | SRC-C1-04 |
| appearance.snap_layouts | Appearance | Low | Reversible | 22000+ | ADV \ EnableSnapAssistFlyout | on=1, off=0 | on | none | no | no |  | SRC-C1-05 |
| appearance.window_shake | Appearance | Low | Reversible | 22000+ | ADV \ DisallowShaking | enabled=0, disabled=1 | enabled | none | no | no |  | SRC-C1-06 |
| appearance.transparency | Appearance | Low | Reversible | 22000+ | CV\Themes\Personalize \ EnableTransparency | on=1, off=0 | on | none | no | no |  | SRC-M2-05 |
| privacy.toast_notifications | Privacy | Low | Reversible | 22000+ | CV\PushNotifications \ ToastEnabled | on=1, off=0 | on | none | no | no |  | SRC-C1-07 |
| privacy.clipboard_history | Privacy | Low | Reversible | 22000+ | Software\Microsoft\Clipboard \ EnableClipboardHistory | off=0, on=1 | off | none | no | no |  | SRC-C1-08 |
| privacy.start_suggestions | Privacy | Low | Conditional | 22000+ | CV\ContentDeliveryManager \ SystemPaneSuggestionsEnabled | shown=1, hidden=0 | shown | hidden | no | no |  | SRC-C1-09 |
| privacy.preinstalled_apps | Privacy | Low | Conditional | 22000+ | CV\ContentDeliveryManager \ PreInstalledAppsEnabled | enabled=1, disabled=0 | enabled | disabled | no | no |  | SRC-E-01 |
| privacy.oem_preinstalled_apps | Privacy | Low | Conditional | 22000+ | CV\ContentDeliveryManager \ OemPreInstalledAppsEnabled | enabled=1, disabled=0 | enabled | disabled | no | no |  | SRC-E-01 |
| privacy.silent_app_installs | Privacy | Low | Conditional | 22000+ | CV\ContentDeliveryManager \ SilentInstalledAppsEnabled | enabled=1, disabled=0 | enabled | disabled | no | no |  | SRC-E-01 |
| start.recommended_items | Start | Low | Reversible | 22621+ | ADV \ Start_IrisRecommendations | hidden=0, shown=removed | shown | none | no | yes |  | SRC-W1-01 |
| start.recent_lists | Start | Low | Reversible | 22621+ | CV\Start \ ShowRecentList; CV\Start \ ShowFrequentList; ADV \ Start_TrackDocs | hidden=0/0/0, shown=removed/removed/removed | shown | none | no | yes |  | SRC-W1-01 |
| start.track_programs | Start | Low | Reversible | 22621+ | ADV \ Start_TrackProgs | off=0, on=removed | on | none | no | yes |  | SRC-W1-01 |
| start.account_notifications | Start | Low | Reversible | 22621+ | ADV \ Start_AccountNotifications; CV\SystemSettings\AccountNotifications \ EnableAccountNotifications | off=0/0, on=removed/removed | on | none | no | yes |  | SRC-W1-01 |
| start.all_apps_view | Start | Low | Conditional | 26200+ | CV\Start \ AllAppsViewMode | category=0, grid=1, list=2 | category | none | no | yes |  | SRC-W1-08 |
| start.hide_all_apps | Start | Medium | Reversible | 26200+ | CV\Policies\Explorer \ NoStartMenuMorePrograms | hidden=1, shown=removed | shown | none | no | yes |  | SRC-W1-08 |
| privacy.lock_screen_tips | Privacy | Low | Conditional | 22621+ | CV\ContentDeliveryManager \ SubscribedContent-338387Enabled; CV\ContentDeliveryManager \ RotatingLockScreenOverlayEnabled | off=0/0, on=removed/removed | on | off | no | no |  | SRC-W1-02 |
| privacy.start_suggested_content | Privacy | Low | Conditional | 22621+ | CV\ContentDeliveryManager \ SubscribedContent-338388Enabled | off=0, on=removed | on | off | no | no |  | SRC-W1-02 |
| privacy.windows_tips | Privacy | Low | Conditional | 22621+ | CV\ContentDeliveryManager \ SubscribedContent-338389Enabled; CV\ContentDeliveryManager \ SoftLandingEnabled | off=0/0, on=removed/removed | on | off | no | no |  | SRC-W1-02 |
| privacy.settings_suggestions | Privacy | Low | Conditional | 22621+ | CV\ContentDeliveryManager \ SubscribedContent-338393Enabled; CV\ContentDeliveryManager \ SubscribedContent-353694Enabled; CV\ContentDeliveryManager \ SubscribedContent-353696Enabled; CV\ContentDeliveryManager \ SubscribedContent-353698Enabled | off=0/0/0/0, on=removed/removed/removed/removed | on | off | no | no |  | SRC-W1-02 |
| privacy.welcome_experience | Privacy | Low | Conditional | 22621+ | CV\ContentDeliveryManager \ SubscribedContent-310093Enabled | off=0, on=removed | on | off | no | no |  | SRC-W1-02 |
| privacy.get_most_out_of_windows | Privacy | Low | Reversible | 22621+ | CV\UserProfileEngagement \ ScoobeSystemSettingEnabled | off=0, on=removed | on | none | no | no |  | SRC-W1-03 |
| privacy.suggested_toasts | Privacy | Low | Reversible | 22621+ | CV\Notifications\Settings\Windows.SystemToast.Suggested \ Enabled; CV\Notifications\Settings\Windows.SystemToast.BackupReminder \ Enabled; CV\Mobility \ OptedIn | off=0/0/0, on=removed/removed/removed | on | none | no | no |  | SRC-W1-03 |
| privacy.feedback_frequency | Privacy | Low | Reversible | 22621+ | Software\Microsoft\Siuf\Rules \ NumberOfSIUFInPeriod; Software\Microsoft\Siuf\Rules \ PeriodInNanoSeconds | never=0/removed, windows=removed/removed | windows | never | no | no |  | SRC-W1-03 |
| privacy.input_personalization | Privacy | Low | Reversible | 22621+ | Software\Microsoft\InputPersonalization \ RestrictImplicitInkCollection; Software\Microsoft\InputPersonalization \ RestrictImplicitTextCollection; Software\Microsoft\InputPersonalization\TrainedDataStore \ HarvestContacts; Software\Microsoft\Input\TIPC \ Enabled; Software\Microsoft\Personalization\Settings \ AcceptedPrivacyPolicy | limited=1/1/0/0/0, windows=removed/removed/removed/removed/removed | windows | limited | no | no |  | SRC-W1-04 |
| privacy.online_speech | Privacy | Medium | Reversible | 22621+ | Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy \ HasAccepted | off=0, windows=removed | windows | none | no | no |  | SRC-W1-04 |
| privacy.third_party_suggestions | Privacy | Low | Conditional | 22621+ | Software\Policies\Microsoft\Windows\CloudContent \ DisableThirdPartySuggestions | blocked=1, allowed=removed | allowed | blocked | no | no |  | SRC-W1-11 |
| privacy.cross_device_resume | Privacy | Low | Reversible | 22621+ | CV\CrossDeviceResume\Configuration \ IsResumeAllowed | off=0, on=removed | on | none | no | no |  | SRC-W1-12 |
| privacy.cloud_clipboard_upload | Privacy | Low | Reversible | 22621+ | Software\Microsoft\Clipboard \ CloudClipboardAutomaticUpload | off=0, on=removed | on | off | no | no |  | SRC-W1-12 |
| privacy.dev_tool_telemetry | Privacy | Low | Reversible | 22621+ | Environment \ DOTNET_CLI_TELEMETRY_OPTOUT; Environment \ POWERSHELL_TELEMETRY_OPTOUT | optout="1"/"1", send=removed/removed | send | none | no | no | E | SRC-W1-13 |
| search.box_suggestions | Search | Medium | Reversible | 22621+ | Software\Policies\Microsoft\Windows\Explorer \ DisableSearchBoxSuggestions | hidden=1, shown=removed | shown | none | no | yes |  | SRC-W1-05 |
| search.highlights | Search | Low | Reversible | 22621+ | CV\SearchSettings \ IsDynamicSearchBoxEnabled | off=0, on=removed | on | none | no | no |  | SRC-W1-05 |
| taskbar.button_grouping | Taskbar | Low | Reversible | 22631+ | ADV \ TaskbarGlomLevel; ADV \ MMTaskbarGlomLevel | always=0/0, when_full=1/1, never=2/2 | always | none | no | yes |  | SRC-W1-06 |
| taskbar.multi_display | Taskbar | Low | Reversible | 22621+ | ADV \ MMTaskbarEnabled; ADV \ MMTaskbarMode | all=removed/removed, main_only=0/removed, main_and_open=1/1, open_only=1/2 | all | none | no | yes |  | SRC-W1-06 |
| taskbar.notification_center | Taskbar | Medium | Reversible | 22621+ | CV\Policies\Explorer \ DisableNotificationCenter | hidden=1, shown=removed | shown | none | no | yes |  | SRC-W1-06 |
| appearance.alt_tab_tabs | Appearance | Low | Conditional | 22621+ | ADV \ MultiTaskingAltTabFilter | tabs20=0, tabs5=1, tabs3=2, windows_only=3 | tabs5 | none | no | no |  | SRC-W1-07 |
| appearance.snap_bar | Appearance | Low | Reversible | 22621+ | ADV \ EnableSnapBar | off=0, on=1 | on | none | no | no |  | SRC-W1-07 |
| appearance.desktop_spotlight | Appearance | Low | Reversible | 22621+ | Software\Policies\Microsoft\Windows\CloudContent \ DisableSpotlightCollectionOnDesktop | disabled=1, enabled=removed | enabled | none | no | no |  | SRC-W1-11 |
| explorer.drive_letters | Explorer | Low | Reversible | 22621+ | CV\Explorer \ ShowDriveLettersFirst | after_label=0, network_first=1, hidden=2, local_first=4 | after_label | none | no | no |  | SRC-W1-09 |
| explorer.nav_home | Explorer | Low | Reversible | 22621+ | Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903} \ System.IsPinnedToNameSpaceTree | hidden=0, shown=removed | shown | none | no | yes |  | SRC-W1-09 |
| explorer.nav_gallery | Explorer | Low | Reversible | 22621+ | Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c} \ System.IsPinnedToNameSpaceTree | hidden=0, shown=removed | shown | none | no | yes |  | SRC-W1-09 |
| explorer.nav_onedrive | Explorer | Low | Reversible | 22621+ | Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6} \ System.IsPinnedToNameSpaceTree | hidden=0, shown=removed | shown | none | no | yes |  | SRC-W1-09 |
| explorer.classic_context_menu | Explorer | Medium | Reversible | 22000+ | Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2} (key); Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32 (key, empty default) | modern=no key/no key, classic=key/key + default "" | modern | none | no | yes |  | SRC-W1-10 |
| explorer.copy_dialog_details | Explorer | Low | Reversible | 22621+ | CV\Explorer\OperationStatusManager \ EnthusiastMode | detailed=1, simple=removed | simple | none | no | no |  | SRC-W1-09 |
| explorer.autoplay | Explorer | Low | Reversible | 22621+ | CV\Explorer\AutoplayHandlers \ DisableAutoplay | off=1, on=removed | on | none | no | no |  | SRC-W1-09 |
| desktop.unsupported_hardware_notice | Desktop | Low | Reversible | 22621+ | Control Panel\UnsupportedHardwareNotificationCache \ SV1; Control Panel\UnsupportedHardwareNotificationCache \ SV2 | hidden=0/0, shown=removed/removed | shown | none | no | yes |  | SRC-W1-19 |
| desktop.lock_wallpaper | Desktop | Medium | Reversible | 22621+ | CV\Policies\ActiveDesktop \ NoChangingWallPaper | locked=1, allowed=removed | allowed | none | no | no |  | SRC-W1-19 |
| input.scrollbars | Input | Low | Reversible | 22621+ | Control Panel\Accessibility \ DynamicScrollbars | always=0, auto_hide=1 | auto_hide | none | yes | no |  | SRC-W1-14 |
| input.time_format_24h | Input | Low | Conditional | 22621+ | Control Panel\International \ sShortTime; Control Panel\International \ sTimeFormat | h24="HH:mm"/"HH:mm:ss", h12="h:mm tt"/"h:mm:ss tt" | h12 | none | no | no | I | SRC-W1-15 |
| input.mouse_acceleration | Input | Low | Reversible | 22621+ | Control Panel\Mouse \ MouseSpeed; Control Panel\Mouse \ MouseThreshold1; Control Panel\Mouse \ MouseThreshold2 | on="1"/"6"/"10", off="0"/"0"/"0" | on | none | no | no | P | SRC-W1-14 |
| input.keyboard_repeat | Input | Low | Reversible | 22621+ | Control Panel\Keyboard \ KeyboardDelay; Control Panel\Keyboard \ KeyboardSpeed | standard="1"/"31", fast="0"/"31" | standard | none | no | no | K | SRC-W1-14 |
| input.accessibility_hotkeys | Input | Low | Reversible | 22621+ | Control Panel\Accessibility\StickyKeys \ Flags; Control Panel\Accessibility\Keyboard Response \ Flags; Control Panel\Accessibility\ToggleKeys \ Flags | allowed=set bit 0x4/set bit 0x4/set bit 0x4, off=clear bit 0x4/clear bit 0x4/clear bit 0x4 | allowed | off | no | no | A | SRC-W1-14 |
| apps.game_mode | Apps | Low | Reversible | 22621+ | Software\Microsoft\GameBar \ AllowAutoGameMode; Software\Microsoft\GameBar \ AutoGameModeEnabled | on=1/1, off=0/0 | on | on | no | no |  | SRC-W1-16 |
| apps.game_capture | Apps | Medium | Reversible | 22621+ | System\GameConfigStore \ GameDVR_Enabled; CV\GameDVR \ AppCaptureEnabled | on=1/1, off=0/0 | on | none | no | no |  | SRC-W1-16 |
| apps.new_outlook_migration | Apps | Low | Reversible | 22621+ | Software\Policies\Microsoft\Office\16.0\Outlook\Options\General \ DoNewOutlookAutoMigration; Software\Policies\Microsoft\Office\16.0\Outlook\Preferences \ NewOutlookMigrationUserSetting | blocked=0/0, allowed=removed/removed | allowed | none | no | no |  | SRC-W1-17 |
| performance.storage_sense | Performance | Low | Conditional | 22621+ | CV\StorageSense\Parameters\StoragePolicy \ 01 | off=0, on=1 | on | none | no | no |  | SRC-W1-18 |
| performance.app_shutdown_timeouts | Performance | Medium | Reversible | 22621+ | Control Panel\Desktop \ WaitToKillAppTimeout; Control Panel\Desktop \ HungAppTimeout | windows=removed/removed, faster="5000"/"5000" | windows | none | yes | no |  | SRC-W1-18 |
| performance.auto_end_tasks | Performance | Medium | Reversible | 22621+ | Control Panel\Desktop \ AutoEndTasks | ask="0", auto="1" | ask | none | yes | no |  | SRC-W1-18 |

The English and Persian texts of the 14 rows below are kept for review. The texts of every other row are in the `Strings.resx` and `Strings.fa.resx` files of the Desktop and Cli projects and in persian-review.csv.

## Text (English / Persian machine draft)

| Key suffix | English | Persian (draft) |
| --- | --- | --- |
| explorer_show_file_extensions Title | Show file extensions | نمایش پسوند فایل‌ها |
| ... Description | Show the extension (such as .txt) at the end of every file name in File Explorer. | پسوند (مانند txt.) را در انتهای نام همه فایل‌ها در فایل اکسپلورر نشان می‌دهد. |
| ... show / hide | Show / Hide | نمایش / پنهان |
| explorer_show_hidden_files Title | Show hidden files and folders | نمایش فایل‌ها و پوشه‌های پنهان |
| ... Description | Show items marked hidden. Protected system files stay hidden. | مواردی که پنهان علامت خورده‌اند را نشان می‌دهد. فایل‌های محافظت‌شده سیستم پنهان می‌مانند. |
| ... shown / hidden | Show / Do not show | نمایش / عدم نمایش |
| explorer_open_to Title | Open File Explorer to | باز شدن فایل اکسپلورر روی |
| ... Description | Choose whether new File Explorer windows start at Home or This PC. | تعیین می‌کند پنجره‌های جدید فایل اکسپلورر از صفحه اصلی یا این رایانه شروع شوند. |
| ... home / this_pc | Home / This PC | صفحه اصلی / این رایانه |
| explorer_compact_view Title | Compact view | نمای فشرده |
| ... Description | Reduce the spacing between items in File Explorer lists. | فاصله بین موارد در فهرست‌های فایل اکسپلورر را کم می‌کند. |
| ... comfortable / compact | Standard spacing / Compact | فاصله استاندارد / فشرده |
| taskbar_alignment Title | Taskbar alignment | چینش نوار وظیفه |
| ... Description | Place taskbar icons in the center or at the left edge. | نمادهای نوار وظیفه را در وسط یا سمت چپ قرار می‌دهد. |
| ... center / left | Center / Left | وسط / چپ |
| taskbar_clock_seconds Title | Seconds in the taskbar clock | ثانیه در ساعت نوار وظیفه |
| ... Description | Show seconds in the system tray clock. | ثانیه را در ساعت ناحیه اعلان نشان می‌دهد. |
| ... hidden / shown | Hide seconds / Show seconds | پنهان کردن ثانیه / نمایش ثانیه |
| taskbar_search_mode Title | Taskbar search | جستجو در نوار وظیفه |
| ... Description | Choose how search appears on the taskbar. | نحوه نمایش جستجو در نوار وظیفه را تعیین می‌کند. |
| ... hidden / icon / box | Hidden / Icon only / Search box | پنهان / فقط نماد / کادر جستجو |
| taskbar_task_view_button Title | Task View button | دکمه نمای وظایف |
| ... Description | Show or hide the Task View button on the taskbar. | دکمه نمای وظایف را در نوار وظیفه نشان می‌دهد یا پنهان می‌کند. |
| ... shown / hidden | Show / Hide | نمایش / پنهان |
| privacy_advertising_id Title | Advertising ID | شناسه تبلیغاتی |
| ... Description | Let apps use your advertising ID for personalized ads. A device policy may override this. | به برنامه‌ها اجازه می‌دهد برای تبلیغات شخصی‌سازی‌شده از شناسه تبلیغاتی شما استفاده کنند. سیاست دستگاه ممکن است آن را نادیده بگیرد. |
| ... enabled / disabled | On / Off | روشن / خاموش |
| privacy_tailored_experiences Title | Tailored experiences | تجربه‌های شخصی‌سازی‌شده |
| ... Description | Let Windows use diagnostic data to offer tips and recommendations. | به ویندوز اجازه می‌دهد از داده‌های عیب‌یابی برای پیشنهاد نکات و توصیه‌ها استفاده کند. |
| ... enabled / disabled | On / Off | روشن / خاموش |
| search_web_results Title | Web results in Start search | نتایج وب در جستجوی استارت |
| ... Description | Include web suggestions when searching from Start. Verified only up to Windows 11 23H2; needs sign-out. | هنگام جستجو از استارت، پیشنهادهای وب را هم نشان می‌دهد. فقط تا ویندوز ۱۱ نسخه 23H2 بررسی شده است؛ نیاز به خروج از حساب دارد. |
| ... enabled / disabled | Include web results / Local results only | با نتایج وب / فقط نتایج محلی |
| appearance_apps_theme Title | App mode | حالت برنامه‌ها |
| ... Description | Choose light or dark for apps that follow the system. | روشن یا تاریک بودن برنامه‌هایی که از سیستم پیروی می‌کنند را تعیین می‌کند. |
| ... light / dark | Light / Dark | روشن / تاریک |
| appearance_system_theme Title | Windows mode | حالت ویندوز |
| ... Description | Choose light or dark for the taskbar and Start. | روشن یا تاریک بودن نوار وظیفه و استارت را تعیین می‌کند. |
| ... light / dark | Light / Dark | روشن / تاریک |
| appearance_window_snapping Title | Snap windows | چسباندن پنجره‌ها |
| ... Description | Let windows snap to screen edges and layouts. Takes effect after you sign out and in. | اجازه می‌دهد پنجره‌ها به لبه‌ها و چیدمان‌های صفحه بچسبند. پس از خروج و ورود دوباره اعمال می‌شود. |
| ... on / off | On / Off | روشن / خاموش |
