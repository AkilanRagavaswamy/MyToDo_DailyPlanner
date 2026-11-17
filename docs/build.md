# Building My To-Do : Daily Planner 1.0.0.0

A native Windows to-do list, calendar, reminders and work diary. Everything stays on this PC; version 1.0.0.0 makes no network connections. Google sign-in, sync, and backup and restore arrive in 1.1.0.0.

> **Status:** complete source for every 1.0.0.0 epic (E1–E12, E15–E16 code parts), written without a Windows build machine. Expect a first round of compile fixes. Send the build errors back and they will be fixed.

## Requirements

- Windows 10 1809 or later, or Windows 11, x64 or ARM64
- Visual Studio 2022 17.12 or later (or Visual Studio 2026) with the **WinUI application development** workload
- .NET 10 SDK
- NuGet access (restores Windows App SDK, CommunityToolkit.Mvvm, H.NotifyIcon.WinUI, Microsoft.Data.Sqlite, xUnit)

## Build and run

1. Open `MyTodo.sln`.
2. Set **MyTodo.App** as the startup project and choose **x64** (or ARM64) and **Debug**.
3. Press F5. Visual Studio deploys the MSIX package and starts the app. The first launch shows the welcome page.
4. Run the tests: **Test › Run All Tests** (`MyTodo.Tests` covers recurrence, quick-add parsing, reminder rules, insights, layout and the database).

If NuGet reports a missing package version, update it to the latest stable version in **Manage NuGet Packages**.

## Solution structure

| Project | Contents |
| --- | --- |
| `src/MyTodo.Core` | Models and settings defaults, recurrence engine (RRULE, DST-safe), quick-add parser, reminder rules, insights, calendar layout, ICS export. No UI, no network. |
| `src/MyTodo.Data` | SQLite (WAL) database, migrations, all stores, change log kept for 1.1.0.0 sync, FTS5 search, JSON export. |
| `src/MyTodo.App` | WinUI 3 app: windows, pages, calendar controls, reminders as scheduled Windows notifications, tray, hotkey, startup task, jump list. |
| `tests/MyTodo.Tests` | xUnit tests for Core and Data, including a check that neither library references `System.Net.Http`. |
| `store-assets` | 300 × 300 Store logo and 120 × 120 logo (for the Google consent screen in 1.1.0.0). |

## What is in 1.0.0.0 (by epic)

| Epic | Where |
| --- | --- |
| E1 Foundation and shell | `App.xaml(.cs)`, `Program.cs`, `MainWindow`, `Themes/`, `Package.appxmanifest` |
| E2 Data layer | `MyTodo.Data/*` |
| E3 Tasks and lists | `Views/ListsPage`, `Views/TodayPage`, `Views/TaskDetailPanel`, `ViewModels/TaskRow.cs` |
| E4 Recurrence | `Core/Recurrence/RecurrenceRule.cs` |
| E5 Reminders | `Core/Reminders/ReminderPlanner.cs`, `Services/ReminderService.cs` |
| E6 Windows integration and quick add | `Services/TrayAndHotkey.cs`, `Services/AppServices.cs` (startup, jump list), `QuickAddWindow`, `Core/Parsing/QuickAddParser.cs` |
| E7 Calendar | `Controls/WeekTimeline.cs`, `Controls/MonthGrid.cs`, `Views/CalendarPage`, `Views/CalendarActions.cs` |
| E8 Timer and attachments | `Services/AppServices.cs` (TimerService, AttachmentService), `Views/TaskDetailPanel` |
| E9 Diary | `Views/DiaryPage` |
| E10 Insights | `Core/Insights/InsightsCalculator.cs`, `Views/InsightsPage` |
| E11 Search | `DataStore.Calendar.cs` (FTS5), `Views/SearchPage` |
| E12 Settings and export | `Views/SettingsPage`, `Core/Export/IcsWriter.cs`, `DataStore.ExportJson` |
| First run | `Views/WelcomePage` |
| Keyboard shortcuts | `Views/ShortcutsPage`, accelerators in each page |

Defaults built in: reminders 1 hour, 30 minutes and 15 minutes before timed items; 3:00 PM the day before and 9:00 AM on the day for date-only tasks; short-notice rule; snooze 5 min, 15 min, 1 hour, tomorrow 9:00 AM; archive after 30 days; attachments up to 10 MB; 30-minute blocks; working hours 9:00–18:00; week starts Monday; theme follows Windows.

## Check these first on your PC

1. **Reminder buttons while the app is closed.** Toasts are scheduled with `ScheduledToastNotification` and activated through `AppNotificationManager`. If Snooze or Mark done does nothing when the app was not running, tell me and the activation path will be switched.
2. **Win + Shift + T.** Windows uses this combination to cycle taskbar apps backwards, so registration may fail. The app then shows a notice; pick Ctrl + Alt + T or Ctrl + Shift + Space in Settings › Startup and tray.
3. **Launch at startup.** Works only from the installed MSIX package; Windows asks for consent the first time.
4. **Mica.** Shown on Windows 11; Windows 10 gets a solid themed background.

## Store submission

1. In Visual Studio: **Project › Publish › Associate App with the Store**, pick the reserved name "My To-Do : Daily Planner".
2. **Publish › Create App Packages** for x64 and ARM64, then run the Windows App Certification Kit.
3. Upload the `.msixupload` in Partner Center (steps in the requirements document, section 13).

## Not in 1.0.0.0 (planned for 1.1.0.0)

Google sign-in, sync to Google Drive app data, restore after reinstall, daily local backup and restore (SYN-01–10, SET-01–02). The data model already keeps IDs, timestamps, tombstones and a change log so 1.1.0.0 can sync existing data.

**Important:** without backup, uninstalling 1.0.0.0 deletes its data. Use Settings › Your data › Export JSON to keep a copy.
