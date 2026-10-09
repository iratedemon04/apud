# MainForm refactor — batch UI test checklist

One pass through a real cataloguing session covers the whole MainForm refactor
(`docs/MAINFORM-REFACTOR-PLAN.md`, modules 3–5 built back to back on 2026-10-09 without
per-module UI drives). Run it off the published `publish\Apud\Apud.exe`.

- Sections follow a session: **open catalogue → search → open/edit/push records → import →
  export → backup → close.** Within each section the **riskiest items come first**.
- Each item is tagged with the module whose commit it exercises:
  **[M3]** `CatalogueController` · **[M4]** `MenuBuilder` · **[M5]** `AboutDialog` + `TextPrompt`.
  **[M1]**/**[M2]** = regression re-checks of the already-accepted `SyncCoordinator` /
  `SearchController`, included because module 3 rewired how they are built.
- Tick `[x]` as each item is confirmed. On a failure, jot what you saw next to the item.

## Before you start

- Items marked **(scratch)** create or destroy data. Do them in a **scratch catalogue**
  (File → New Catalogue in a throwaway folder), then Ctrl+O back to your real one. Everything
  else is safe in your real catalogue.
- The settings checks on the real catalogue are written so you **re-confirm the current value**
  (OK on the same folder / same code). Nothing needs changing.

## Already covered by automated tests (no need to check by hand)

Every command id, key, context and menu entry is unchanged; each command routes to the expected
handler, including the BIB/AUT arguments; every menu item is a registered command and clicking
it runs that command; the shipped `keymap.json` loads clean (`MainFormWiringTests`,
`MenuBuilderTests`). The no-catalogue guard on every catalogue command, org-code storage,
per-base output-folder settings, opening a catalogue, and the export "nothing to export"
messages are unit-tested (`CatalogueControllerTests`). What's left is what only a person can
see: dialogs and who owns them, focus, the import thread, files on disk.

---

## 1. Open a catalogue

- [ ] **[M3]** Launch Apud: it reopens the last catalogue by itself. Title bar `Apud — <path>`;
      message bar `Catalogue open — BIB: n, AUT: m record(s).` (plus `k draft(s) reopened.` if any).
- [ ] **[M3]** Saved drafts from last session are back in the sidebar; the screen lands on Search,
      not on a record.
- [ ] **[M3]** File → Open Catalogue (Ctrl+O): the dialog starts in the last folder used. Open another
      `.db` → sidebar empties, search results and history clear, that catalogue's drafts load, title and
      counts update. Ctrl+O back to the real one.
- [ ] **[M3]** Ctrl+O on something that isn't a catalogue (a `.txt` renamed to `.db`) → "Cannot open
      catalogue" error, centred over Apud; the catalogue you had stays open and usable.
- [ ] **[M3] (scratch)** File → New Catalogue: suggests `catalog.db`; picking an existing file asks to
      overwrite; the new catalogue opens empty (`BIB: 0, AUT: 0`).
- [ ] **[M3]** With no catalogue open (e.g. launch after moving the last `.db` away): File → Import,
      Set BIB Output Folder, Set Authority Output Folder, Set Organization Code, Export Base and Export
      Selected each show the "First open a catalogue" popup and do nothing else.
- [ ] **[M4]** Menu bar shows File, Base, Record, Window, Help with their entries and shortcut text;
      Alt+F / Alt+B / Alt+R / Alt+W / Alt+H open them from the keyboard.

## 2. Search

- [ ] **[M4][M2]** Ctrl+B switches base and the **Base menu checkmark moves** BIB ⇄ AUT (those two
      menu items now come from `MenuBuilder`); scopes and result columns change with it.
- [ ] **[M4]** Base → AUT and Base → BIB clicked in the menu switch the base the same way.
- [ ] **[M2]** Search BIB, ↓/↑ through results, Enter or double-click opens the record into the sidebar.

## 3. Open, edit and push records

- [ ] **[M3]** Push a BIB record (Ctrl+L) → its `<001>.mrk` is written to your BIB output folder
      (if one is set; with none set nothing is written, silently — that's normal).
- [ ] **[M3]** Push an AUT record → its `.mrk` goes to the **AUT** output folder, not the BIB one.
- [ ] **[M3] (scratch)** Push a throwaway record, then delete it (Ctrl+Delete, confirm) → its `.mrk`
      disappears from that base's output folder.
- [ ] **[M3][M5]** File → Set Organization Code: the prompt opens centred on Apud showing the current
      code; OK → `Organization code set to <code>.` Cancel/Esc changes nothing. Push a new record → its
      003 carries the code. **(scratch)** a blank entry → `Organization code cleared.`
- [ ] **[M3]** File → Set BIB Output Folder: the picker opens at the current BIB folder; OK →
      `Output folder set.` Same for Set Authority Output Folder (opens at the AUT folder).
- [ ] **[M4]** Menu clicks run their commands: Record → New Field, Validate Record, Validate & Push on
      an open record; Window → Zoom In / Zoom Out / Reset Zoom; Help → Field Help, Getting Started,
      Backup Times.
- [ ] **[M5]** Help → About Apud: version line, OK closes it; the **Legal** link shows the copyright
      and MIT licence text over the About box ("Cossío Vázquez" accents intact).

## 4. Import

- [ ] **[M3] (scratch)** Big pushed import (a whole folder, thousands of records): the progress dialog
      animates, Apud never shows "Not Responding", the dialog closes itself, message
      `Imported N record(s) — BIB x, AUT y.`; the new records are searchable straight away.
- [ ] **[M3]** Import exactly one record as **pushed** (scratch, or a genuinely new record) → it opens
      straight into the editor.
- [ ] **[M3]** Import as **drafts** (Ctrl+I → Single File, a few LC records): each lands in the sidebar
      as an unsaved `*draft`, the first is selected and shown; message `Imported N record(s) as drafts.`
- [ ] **[M3]** Flip one of the two normalize checkboxes in the import dialog, finish, start another
      import: the checkbox kept your choice.
- [ ] **[M3]** Ctrl+I → the Single File / Whole Folder chooser opens centred on Apud; Cancel or Esc does
      nothing. Cancelling the file picker or the wizard imports nothing either.
- [ ] **[M3]** Import a folder with no `.mrk` in it → "No records found", nothing imported.

## 5. Export

- [ ] **[M3]** Select several saved records in the sidebar → File → Export Selected → suggests
      `selection.mrk`; the file holds exactly those records; message `Exported N record(s) to <path>.`
- [ ] **[M3]** File → Export Base with BIB active suggests `BIB.mrk`; after Ctrl+B it suggests `AUT.mrk`.
- [ ] **[M3]** Export Selected with only unsaved/new records selected → `Select one or more saved
      records in the sidebar first.`
- [ ] **[M3] (scratch)** Export Base on an empty base (AUT in a fresh catalogue) → `AUT is empty —
      nothing to export.`

## 6. Backup and restore

- [ ] **[M1][M3]** Backup Server → Restore from Server → pick a snapshot → open the restored copy: it
      becomes the current catalogue (title, counts, sidebar reset) — this path now goes through
      `CatalogueController.Open`. Ctrl+O back to the real one afterwards.
- [ ] **[M1][M5]** Ctrl+U backs up: the key-passphrase prompt (if your key has one) opens centred on
      Apud and **masks what you type**; Enter accepts, Cancel aborts; then the snapshot message.

## 7. Close

- [ ] **[M1]** Push a record, then close Apud → "back up first?" prompt; Cancel keeps Apud open.
- [ ] **[M3]** Next launch reopens the catalogue that was open when you closed (the "last catalogue"
      is now saved from `AttachCatalogue`).

---

## Reporting a bug

Tell me what you did, what you expected, and what happened (message-bar text helps). I'll find the
commit with the commits below (bisecting between them if it's unclear), fix it as its own commit with
a regression test where possible, and tick the item here once you confirm the fix.

| Commit | What it changed |
|---|---|
| `368a0cc` | last commit before this refactor session (known-good) |
| `ec91991` | wiring tests only — no behaviour change |
| `1549017` | **M3** `CatalogueController` (new/open, import, export, output folders, org code) |
| `3caf92b` | **M4** `MenuBuilder` (menu bar construction) |
| `0d19c65` | **M5** `AboutDialog` + `TextPrompt` |
