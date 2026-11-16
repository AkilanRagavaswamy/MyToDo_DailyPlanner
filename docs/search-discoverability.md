# Being found by "todo"

Two different search engines, only one of which lives in this repository.

## 1. Windows Start / taskbar search — fixed in code

Start menu search matches **whole-word prefixes** of the app's display name. It does not do
free substring matching, and it treats a hyphen as a word break.

The old name was `My To-Do : Daily Planner`, which tokenises as `My` / `To` / `Do` / `Daily` /
`Planner`. A query of `todo` is not a prefix of any of those, so the app never appeared.

Note that closing the name up to `MyToDo` does **not** fix it: that is a single token beginning
`My`, which `todo` does not prefix either. The word needs a token of its own.

Two changes in `src/MyTodo.App/Package.appxmanifest` fix that:

| Change | Effect |
| --- | --- |
| `DisplayName` is now `My ToDo : Daily Planner` (both in `<Properties>` and in `<uap:VisualElements>`) | `ToDo` is its own token, so `todo`, `tod`, `to` all prefix-match it |
| New `windows.appExecutionAlias` extension registering `todo.exe` | `todo` in the Run box, a terminal or Start launches the app, and Windows lists the alias next to it |

`<Identity Name="MyToDo.DailyPlanner">` is deliberately left alone. It is the package identity, not
a display string: changing it orphans the installed package and everything under its `LocalState`.

The alias element must be `uap5:ExecutionAlias`, not `desktop:ExecutionAlias`. The `desktop:` form
belongs under `uap3:AppExecutionAlias`; under `uap5:AppExecutionAlias` it fails deployment with
`DEP0700 ... Element '...desktop/windows10}ExecutionAlias' is unexpected`. The schema is only
checked at deployment, never by `dotnet build`, so verify with
`Add-AppxPackage -Register <path>\AppxManifest.xml` rather than trusting a successful build.

Both only take effect for an **installed package**. After reinstalling, Start search can take a
minute or two to re-index; `Get-AppxPackage *DailyPlanner*` confirms the new name is registered.

The window title, tray tooltip, welcome screen and export filenames were renamed to match, so the
app does not call itself two different things.

## 2. Microsoft Store search — not controllable from this repository

Store ranking is set in **Partner Center**, not in the package, and no manifest field feeds it.
Ranking is relevance plus installs, ratings and engagement, so "top of the results" cannot be
guaranteed by configuration — but the listing fields below are what relevance is computed from.
Fill them in under *Store listings → English (United States)*:

- **Product name**: `My ToDo : Daily Planner` — reserve the name first under *Product identity*.
  The name carries the most ranking weight, so the word `ToDo` belongs in it as its own word.
- **Search terms** (7 × 30 characters, invisible to shoppers, indexed):
  `todo`, `to do list`, `task manager`, `daily planner`, `kanban board`, `reminders`, `work diary`.
- **Short description** (≤ 200 characters), leading with the keyword:
  *A todo list and daily planner for Windows. Tasks, a kanban board, calendar, reminders and a work
  diary — all stored on your PC.*
- **Description**: open with a sentence containing "todo list" and "daily planner"; the first lines
  are weighted most.
- **Category**: Productivity. **Screenshots**: at least four, board view included.

The `Description` attribute on `<uap:VisualElements>` is *not* the Store description — it is the
tooltip Windows shows — but it was updated to the same wording for consistency.
