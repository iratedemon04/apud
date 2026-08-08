# MainForm refactor — extract collaborators before Module 12

> **Status: DECIDED 2026-08-06, do BEFORE Module 12.** The user wants `MainForm.cs` broken up before v1 because **Apud is open source** — that file (2,488 lines, 156 methods, ~zero unit tests) is where a new contributor lands first, and the orientation tax is paid by everyone who touches it, forever. Budget ~1 day. This is a sibling pre-M12 task alongside the DEFERRED triage (see `docs/TRIAGE.md` B2, now promoted from "decide" to "do").

## Goal

Reduce `MainForm` to a **thin shell**: a composition root (wire up the panels + split layout) plus top-level command/keymap dispatch. Every cohesive concern moves to its own file a reader can open in isolation. Target: `MainForm` comfortably under ~1,500 lines; each extracted concern one file.

**This is NOT a redesign.** It is a mechanical, behavior-preserving extraction. No feature changes, no keybinding changes, no UI layout changes.

## Hard constraints (the file has ~no tests — treat every move as load-bearing)

1. **Behavior-preserving.** The 312 existing tests stay green throughout. Keymap, menus, dialogs, and on-screen behavior are identical before/after.
2. **Pure moves.** Cut a cluster of methods into a new class; change signatures only as needed to pass dependencies. No logic rewrites riding along.
3. **Don't spread the coupling.** A collaborator must NOT get a reference to `MainForm`. Give it a *minimal* constructor of exactly what it needs — e.g. `Func<RecordRepository?> repo`, `Action<string> setMessage`, `Func<string,string,string,bool,string?> promptText`, an `IWin32Window owner` for dialogs. Handing the whole form around just renames the god-object.
4. **One collaborator at a time**, each ending in: `dotnet test` green → `publish.ps1` → a **targeted manual drive of that feature** → commit + push. A regression then surfaces at the smallest step. This per-step rhythm is the safety net standing in for the missing unit tests.
5. **Add tests where extraction enables them.** When a seam becomes injectable, write a test or two (e.g. a `SyncCoordinator` driven by the existing `FakeSftpTransport`). Turn zero-coverage glue into some coverage — a direct OSS win.

## Extraction order (most self-contained first = lowest risk, earliest payoff)

1. **`SyncCoordinator`** — the backup/restore command handlers: `ConfigureSync`, `UploadToServer`, `RestoreFromServer`, `PickSnapshot`, `OfferRecordFolderDownload`, `AskPassphrase`, the `_pushesSinceSync` counter, and the on-exit "back up first?" prompt. Already sits behind `Apud.Sync`; depends only on repo + settings + prompt/message callbacks. Cleanest cut, and `SyncService` is already testable via `FakeSftpTransport`.
2. **`SearchController`** (or fold the search UI into a `SearchView : UserControl`) — `RunSearch`, `ConfigureResultColumns`, `FillResults`, the `ListByIds`/paging (`_moreButton`, `_listAllMode`), the `SearchHistory` grid, the scope dropdown, and the base label/`base.toggle`. Cohesive cluster; the FTS logic underneath is already tested.
3. **`CatalogueController`** — catalogue lifecycle + settings: `NewCatalog`, `OpenCatalog`, the Import-wizard wiring (`RunImport`/`OpenRecordById`), Export, `SetMarcOutFolder`, `catalogue.org-code`. 
4. **(optional, if the day allows) `MenuBuilder`** — the menu construction from the command table. Pure, mechanical, and a big chunk of lines.

**Leave in `MainForm`:** the composition root (ctor wiring `_grid`/sidebar/findings/search panels + the `SplitContainer`), `ProcessCmdKey`/`ShouldDispatch`/`ActiveContext` dispatch, and the editor + open-records/sidebar command handlers (`NewField`/`DeleteCurrentField`/`OrderFieldsCommand`/`AddToSidebar`/`ShowSelectedOpenRecord`/…). Those are tightly bound to `_grid` + `_currentDoc` + `_findings`; extracting them is higher-risk and lower-value — defer past v1.

## Verification

- No unit tests exist for the WinForms glue, so per extraction: `dotnet test` green (proves the moved code compiles and the tested lower layers are intact) + `publish.ps1` + a **hands-on smoke drive of that exact feature** (do a real backup + restore for #1; run a search, re-run from history, List All + page for #2; New/Open/Import/Export a catalogue for #3).
- Add the injectable-seam tests noted in constraint #5.
- No screenshot parity needed — nothing about layout or the editor changes (the editor already lives in `RecordGrid`).

## Stopping heuristic

Stop when `MainForm` reads as "compose the panels, route the commands" — roughly <1,500 lines, each big concern in its own file. **Do not chase a pattern-perfect MVC.** The goal is contributor navigability, not architecture for its own sake.

## Sequence

Fits before Module 12: **DEFERRED triage → this refactor → `apud module 12 GO`.** Triage first (it's mostly quick decisions + two tiny fixes); do those fix-nows, then this refactor lands in the cleaned structure. Then Module 12 (installer/manual) → RC → `v1.0.0`.
