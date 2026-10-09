# MainForm refactor — batch UI test checklist

One pass through a real cataloguing session covers the whole MainForm refactor
(`docs/MAINFORM-REFACTOR-PLAN.md`). Run it off the published `publish\Apud\Apud.exe`.

- Sections follow a session: launch/open → search → edit/push → import → export → backup → close.
- Within a section the **riskiest items come first**.
- Each item is tagged with the module whose commit it exercises, so a failure maps to a commit:
  **[M3]** `CatalogueController`, **[M4]** `MenuBuilder`, **[M5]** `AboutDialog` + `TextPrompt`.
  **[M1]**/**[M2]** items are regression re-checks of the already-accepted `SyncCoordinator` /
  `SearchController`, included because later modules rewired their construction.
- Tick `[x]` as each item is confirmed. A failure: note what happened next to the item.

**Already covered by automated tests (no need to check by hand):** every command id, key,
context and menu entry is unchanged; each command routes to the expected handler (including
BIB/AUT arguments); every menu item is a registered command; the shipped `keymap.json` loads
clean (`MainFormWiringTests`). The no-catalogue guard, org-code storage, per-base output-folder
settings and the export "nothing to export" messages are unit-tested (`CatalogueControllerTests`).
What's left below is what only a human can see: dialogs, focus, threading, files on disk.

---

## 1. Launch and open a catalogue

- [ ] **[M4]** The menu bar shows File, Base, Record, Window, Help, each with its entries and
      shortcut text; Alt+F / Alt+B / Alt+R / Alt+W / Alt+H open them from the keyboard.
- [ ] **[M3]** Launch Apud: it reopens the last catalogue by itself. Title bar reads `Apud — <path>`;
      message bar reads `Catalogue open — BIB: n, AUT: m record(s).` (plus `k draft(s) reopened.` if any).
- [ ] **[M3]** Saved drafts from last session reappear in the sidebar on launch; the screen lands on
      Search, not on a record.
- [ ] **[M3]** File → Open Catalogue (Ctrl+O): the dialog starts in the last folder used. Open a
      different `.db` → sidebar empties, search results/history clear, that catalogue's drafts load,
      title and counts update.
- [ ] **[M3]** Open something that is not a catalogue (e.g. rename a `.txt` to `.db`) → a
      "Cannot open catalogue" error over the Apud window; the catalogue you had stays open and usable.
- [ ] **[M3]** File → New Catalogue: Save dialog suggests `catalog.db`; choosing an existing file asks to
      overwrite; the new catalogue opens empty (`BIB: 0, AUT: 0`). (Use a scratch folder.)
- [ ] **[M3]** With no catalogue open (launch after moving the last `.db` away, or start fresh): File →
      Import / Set BIB Output Folder / Set Authority Output Folder / Set Organization Code / Export Base /
      Export Selected each show the "First open a catalogue" popup and do nothing else.

## 2. Search

- [ ] **[M2]** Search BIB, ↓/↑ through results, Enter/double-click opens the record into the sidebar.
- [ ] **[M4][M2]** Ctrl+B switches base and the **Base menu checkmark moves** BIB ⇄ AUT (the menu's
      BIB/AUT items now come from `MenuBuilder`); scopes and result columns change with it.
- [ ] **[M4]** Base → AUT and Base → BIB clicked from the menu switch the base the same way.

## 3. Open, edit and push records

- [ ] **[M4]** Clicking menu items runs them: Record → New Field / Validate Record / Validate & Push on an
      open record, Window → Zoom In / Zoom Out / Reset Zoom, Help → Field Help / Getting Started /
      Backup Times / About Apud.
- [ ] **[M5]** Help → About Apud: version line, OK closes; the **Legal** link opens the copyright + MIT
      licence text over the About box (accents in "Cossío Vázquez" intact).
- [ ] **[M3]** File → Set BIB Output Folder → pick a folder → `Output folder set.` Push a BIB record
      (Ctrl+L) → `<001>.mrk` appears in that folder.
- [ ] **[M3]** File → Set Authority Output Folder → pick a *different* folder. Push an AUT record → its
      `.mrk` lands in the AUT folder, not the BIB one.
- [ ] **[M3]** Delete a pushed record (Ctrl+Delete, confirm) → its `.mrk` is removed from that base's
      output folder.
- [ ] **[M3]** Reopen Set BIB Output Folder: the picker starts at the folder chosen before.
- [ ] **[M3][M5]** File → Set Organization Code: the prompt opens centred on Apud and shows the current
      code. Enter a code → `Organization code set to …`; push a new record → its 003 carries the
      code. Cancel (or Esc) changes nothing; a blank entry → `Organization code cleared.`

## 4. Import

- [ ] **[M3]** Big pushed import (a whole folder, thousands of records): the progress dialog animates,
      Apud never shows "Not Responding", the dialog closes itself, and the message reads
      `Imported N record(s) — BIB x, AUT y.` The new records are searchable straight away.
- [ ] **[M3]** Import exactly one record as **pushed** → it opens straight into the editor.
- [ ] **[M3]** Import as **drafts** (Ctrl+I → Single File, a few LC records): each opens in the sidebar
      as an unsaved `*draft`, the first one is selected and shown; message `Imported N record(s) as drafts.`
- [ ] **[M3]** Flip one of the two normalize checkboxes in the import dialog, finish the import, start
      another: the checkbox remembers your choice.
- [ ] **[M3]** Ctrl+I → the Single File / Whole Folder chooser appears centred on Apud; Cancel (or Esc)
      does nothing. Cancel in the file picker or the wizard also imports nothing.
- [ ] **[M3]** Import an empty folder (no `.mrk`) → "No records found" message, nothing imported.

## 5. Export

- [ ] **[M3]** File → Export Base with BIB active → Save dialog suggests `BIB.mrk`; after saving:
      `Exported N record(s) to <path>.` Switch to AUT (Ctrl+B) → it suggests `AUT.mrk`.
- [ ] **[M3]** Select several saved records in the sidebar → File → Export Selected → `selection.mrk`
      with exactly those records.
- [ ] **[M3]** Export Selected with only unsaved/new records selected → `Select one or more saved
      records in the sidebar first.`
- [ ] **[M3]** Export Base on an empty base (e.g. AUT in a fresh catalogue) → `AUT is empty — nothing to export.`

## 6. Backup and restore

- [ ] **[M1][M3]** Backup Server → Restore from Server → pick a snapshot → choose to open the restored
      copy: it opens as the current catalogue (title, counts, sidebar reset) — this now goes through
      `CatalogueController.Open`.
- [ ] **[M1][M5]** Ctrl+U backs up: the key-passphrase prompt (if your key has one) opens centred on
      Apud and **masks what you type**; Enter accepts, Cancel aborts the backup; then the snapshot message.

## 7. Close

- [ ] **[M1]** Push a record, then close Apud → "back up first?" prompt; Cancel keeps Apud open.
