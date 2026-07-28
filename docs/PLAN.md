# Work Plan — **Apud** — Free Aleph-Style Original Cataloguing Package
**Name:** Apud (from the imprint line of early printed books, "apud [publisher]"). Repo root: `C:\Users\ACV\Projects\Apud\`; solution `Apud.sln`, projects `Marc.Core`, `Apud.Data`, `Apud.Sync`, `Apud.App`, `Apud.Tests`.
**Date:** 2026-07-27 · **Status:** draft for review, nothing built yet

---

## 1. Product definition

A free, fully offline Windows desktop application for **original cataloguing in MARC21**, modeled on the Aleph 500 Cataloging module's keyboard-driven workflow. Two bases (BIB, AUT), template-driven record creation, position-aware fixed-field forms, authority control with stored heading links, and a validate-and-push cycle (Ctrl+L) that derives all mechanical data at commit time.

**Non-goals (v1):** circulation, acquisitions, serials check-in, OPAC, Z39.50/SRU, multi-user concurrency, MARC-8 encoding (UTF-8 only), UNIMARC. The architecture must not *preclude* these (an ILS is the long-term ambition) but v1 is the cataloguing module only.

**Philosophy: single-machine software.** One installation = one cataloguer = one local database. Teams use hub-and-spoke: the **main machine** owns the master DB and the server relationship (§9b); satellite cataloguers run the same app fully standalone (no sync configured) and contribute via export → import on the main machine, where records earn Ctrl+L into the master base. No record locking, no live multi-user protocol — considered (SFTP lock files) and **rejected** to protect simplicity. 001 discipline is the team's responsibility, not the software's (see §8 stage 5).

**Design laws:**
1. Nothing institution-specific in code. All local convention lives in **templates + settings + keymap**, which are portable files.
2. Every operation reachable from the keyboard; the mouse is optional everywhere.
3. The database is the source of truth; .mrk/.mrc are interchange formats.
4. No network I/O anywhere in the codebase **except the Sync module (§9b)**, which speaks only SSH/SFTP to a user-configured server. Cataloguing never depends on connectivity; sync failure never blocks Ctrl+L.

---

## 2. Tech stack

| Layer | Choice | Rationale |
|---|---|---|
| Runtime | .NET 8 (LTS), `net8.0-windows` | current LTS; self-contained publish → runs with no framework install |
| UI | WinForms | dense grids, native key handling, fastest path to Aleph-style UI |
| DB | SQLite via `Microsoft.Data.Sqlite` | zero-admin single file; FTS5 for keyword search |
| Tests | xUnit | round-trip and validator tests are the backbone of correctness |
| Server sync | SSH.NET (SFTP) | pure managed library, no external binaries; every Linux server already runs sshd — nothing to install server-side |
| Installer | Inno Setup | free, standard, produces one `setup.exe`, clean uninstall |
| VCS | git, local repo in this folder | history from day one |

**Solution layout** (4 projects, strict dependency order `Core ← Data ← App`, `Tests → all`):

```
Cataloguer.sln
├─ src/Marc.Core/        # MARC domain: record model, parsers, serializers,
│                        #   validation engine, fixed-field layout engine. NO UI, NO DB.
├─ src/Cataloguer.Data/  # SQLite: schema, repositories, indexing, import/export jobs
├─ src/Cataloguer.Sync/  # SSH/SFTP backup+publish to a Linux server — the ONLY assembly with network access
├─ src/Cataloguer.App/   # WinForms: forms, keymap engine, dialogs
└─ tests/Cataloguer.Tests/
```

`Marc.Core` having no dependencies is what later lets us reuse it for an OPAC/exporter/CLI without dragging UI along.

---

## 3. MARC engine (`Marc.Core`)

### 3.1 In-memory model
```csharp
MarcRecord { Leader (24 chars), List<MarcField> Fields, RecordKind (Bib|Authority) }
MarcField  { Tag, // "001".."999"
             IsControl, // tag < 010
             Data,      // control fields: raw string
             Ind1, Ind2, List<MarcSubfield> } // data fields
MarcSubfield { Code (char), Value (string) }
```
Field order is significant and preserved; sorting by tag is a *command*, never automatic.

### 3.2 Serialization (all round-trip tested)
- **.mrk (MARCMaker/MarcEdit dialect):** `=TAG  ##$a...` lines; `\` = blank indicator; `$$` literal-dollar convention; UTF-8 **without BOM**, LF or CRLF tolerated on read, configurable on write.
- **.mrc (ISO 2709): CUT (2026-07-27).** `.mrk` is Apud's only file format for import/export/templates; MarcEdit converts `.mrk`↔`.mrc` externally when binary MARC is needed. *(UI note for Module 6: `$a` notation is file syntax only — the editor renders subfield codes as styled cells, never dollar signs on screen.)*
- Reader errors are **recoverable diagnostics** (line/offset + message), not exceptions — they feed the import report.

### 3.3 Fixed-field layout engine (data-driven — this is the F-menu's backbone)
Byte layouts are **not code**; they are JSON resources:

```json
{ "field": "008", "material": "BK", "positions": [
  { "off": 0, "len": 6, "name": "Date entered", "auto": "yymmdd" },
  { "off": 6, "len": 1, "name": "Type of date", "values": {"s":"Single","c":"Continuing","m":"Multiple","r":"Reprint","n":"Unknown","q":"Questionable","t":"Pub+copyright"} },
  { "off": 7, "len": 4, "name": "Date 1" },
  { "off": 11, "len": 4, "name": "Date 2" },
  { "off": 15, "len": 3, "name": "Place of publication", "lookup": "marc-countries" },
  ...
]}
```
Ship layouts for: **LDR** (bib + authority), **008/BK, 008/CR (serials), 008/MP, 008/MU, 008/VM, 008/MX, 008 authority**, and **006/007** for the same set. Material type for 008 is derived from LDR/06-07 exactly as MARC21 specifies. Lookup tables shipped as resources: MARC country codes, language codes, relator terms — used by dialogs *and* by the validator.

Adding a material type later = adding a JSON file, zero code.

---

## 4. Data model (`Cataloguer.Data`)

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE record (
  id             INTEGER PRIMARY KEY,
  base           TEXT NOT NULL CHECK (base IN ('BIB','AUT')),
  control_number TEXT,                 -- 001; assigned at first successful push
  leader         TEXT NOT NULL,
  status         TEXT NOT NULL CHECK (status IN ('draft','pushed')),
  created_utc    TEXT NOT NULL,
  updated_utc    TEXT NOT NULL,
  UNIQUE (base, control_number)
);

CREATE TABLE field (
  id        INTEGER PRIMARY KEY,
  record_id INTEGER NOT NULL REFERENCES record(id) ON DELETE CASCADE,
  seq       INTEGER NOT NULL,          -- preserves field order
  tag       TEXT    NOT NULL,
  ind1      TEXT, ind2 TEXT,           -- NULL for control fields
  content   TEXT    NOT NULL           -- control data, or subfields joined with US (U+001F)
);
CREATE INDEX ix_field_record ON field(record_id, seq);

-- Authority links: the heart of authority control
CREATE TABLE heading_link (
  field_id       INTEGER PRIMARY KEY REFERENCES field(id) ON DELETE CASCADE,
  auth_record_id INTEGER NOT NULL REFERENCES record(id)
);

-- Browse index over AUT (rebuilt incrementally on every AUT push)
CREATE TABLE heading_index (
  auth_record_id INTEGER NOT NULL REFERENCES record(id) ON DELETE CASCADE,
  kind        TEXT NOT NULL CHECK (kind IN ('auth','see','seealso')), -- 1XX / 4XX / 5XX
  tag         TEXT NOT NULL,
  normalized  TEXT NOT NULL,           -- NACO-style normalization (see §6.2)
  display     TEXT NOT NULL
);
CREATE INDEX ix_heading_norm ON heading_index(normalized);

CREATE TABLE sequence (base TEXT PRIMARY KEY, next_value INTEGER NOT NULL);
CREATE TABLE setting  (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE schema_version (version INTEGER NOT NULL);   -- forward migrations

-- Keyword search
CREATE VIRTUAL TABLE record_fts USING fts5(control_number, title, author, subjects, anytext, content='');
```

Draft vs pushed: **draft** records are savable-anytime workspaces (Ctrl+S equivalent, no validation); only **Ctrl+L** promotes to *pushed*, and only pushed records get a 001 and enter the FTS/browse indexes. Editing a pushed record re-enters draft state on that record until re-pushed (Aleph's local-copy feel without separate files).

DB file default location: `%USERPROFILE%\Documents\<AppName>\catalog.db` (changeable in settings; File → Backup = checkpoint WAL + copy file).

---

## 5. Editor UI

**Main window:** menu bar (every command listed with its shortcut, Aleph-style), a left **navigation pane** (search/browse results, drafts list), the **record editor** center, a bottom **message bar** (validation output, click/Enter jumps to offending field). Multiple records open in tabs; Ctrl+Tab cycles.

**Editor grid:** one row per field: `TAG | I1 | I2 | content`. Subfields rendered inline as `$$a Value $$b Value` with the delimiter highlighted; cursor-aware — the App always knows which subfield the caret is in (drives F3/F4 context). Fields with a stored heading link show a marker glyph; broken links (auth deleted) show a warning glyph.

**Grid keys (within-editor, non-F):** Tab/Shift+Tab next/prev cell; Enter from content = new subfield prompt (configurable); Alt+Up/Down move field; standard clipboard ops on whole fields.

---

## 6. The F-menu / keyboard model

### 6.1 Keymap engine
All bindings live in `keymap.json` (portable, user-editable, reloadable). Commands are named (`record.push`, `field.new`, `heading.browse`, ...); the menu bar renders from the same command table, so menus and keys can never disagree. **Chords and menu accelerators both supported.**

### 6.2 Proposed default keymap — **FOR YOUR CORRECTION**
Modeled on Aleph 500 defaults as best I know them; you know them better. Mark each ✔/✘ and give the binding your hands expect. Columns: proposed key → command.

| Key | Command | Notes |
|---|---|---|
| **Ctrl+A** | Open template → new draft record | Aleph "Open Template" |
| **Ctrl+O** | Open MARC file as new draft | covers your downloaded LC records |
| **F3** | Browse headings (current base) at caret's field | picker fills authorized form + stores link |
| **F4** | Browse headings, *other* base | e.g. from AUT record, peek BIB uses |
| **Ctrl+F3** | Expand from authority (refresh field from linked auth record) | |
| **F5** | New field — pick from list (tag + name, filtered as you type) | |
| **F6** | New field — type tag directly | |
| **F7** | New subfield at caret | |
| **Ctrl+F5 / Ctrl+F7** | Delete field / delete subfield | |
| **F8** | Fixed-field form for the field at caret (LDR/006/007/008) | position-by-position dialog, §3.3 |
| **Ctrl+L** | **Validate + push to base** | your core cycle; errors → message bar |
| **Ctrl+S** | Save draft (no validation) | |
| **Ctrl+W** | Check record (validate only, no push) | dry-run of Ctrl+L |
| **F1** | Help on current field (tag documentation panel) | offline MARC21 docs |
| **F2** | Search current base (find/browse window) | |
| **F9** | Record overview / holdings-style summary | v1: simple record summary |
| **F11 / F12** | Sort fields by tag / renumber-cleanup | explicit commands, never automatic |
| **Ctrl+D** | Duplicate current record as new draft | "derive" cataloguing |
| **Ctrl+T** | Save current record **as a template** | templates from real records |

Anything you correct becomes the shipped default; Aleph-faithful is the acceptance criterion.

### 6.3 Authority browse behavior (F3)
1. Caret in a controlled field (configurable set; default 100/110/111/130/240/6XX/700/710/711/730/800/810/811/830 for BIB) → F3 opens the browse list **positioned alphabetically at the current field text** (normalized comparison).
2. List shows authorized headings interleaved with *see*-references (`X  →  see: Y`), Aleph-style; arrow keys scroll the index, typing re-positions.
3. Enter on an authorized heading: writes 1XX of the auth record into the bib field (tag-mapped, e.g. auth 100 → bib 100/600/700 depending on context), preserves your `$e` relator if present, stores `heading_link`.
4. Enter on a see-reference: jumps to its authorized target, then as above.
5. Esc = cancel, nothing written. F3 on an already-linked field = re-browse from the linked heading.
6. **Normalization** (for `heading_index.normalized` and comparisons): Unicode-decompose, strip diacritics, casefold, strip punctuation except first comma, collapse spaces — NACO-flavored, one documented function, used everywhere.
7. **Ripple:** pushing an AUT record whose 1XX changed rewrites the heading text of every linked bib field in one transaction and reports the count. Deleting an AUT record with links is refused (shows the linked-records list) unless links are first moved.

---

## 7. Templates

Portable files in `<data>\templates\*.mrk` + optional sidecar `*.template.json`:
- The `.mrk` body is a normal record skeleton (you already author these).
- Sidecar (optional) adds per-field metadata: `prompt` (cursor stops on tab-through), `protected` (skip cursor), `oncreate` auto-fills (e.g. `"008[0-5]": "today"`), and the material-type hint for F8 layouts.
- A template with no sidecar still works (every field is a normal stop).
- Ship 3 starter templates (monograph, serial, authority-personal-name); users replace them.

## 8. Validation pipeline (Ctrl+L / Ctrl+W)

Ordered stages, all producing `(severity, field-ref, code, message)`; **error** blocks push, **warning** doesn't:
1. **Structural:** legal tag, indicator characters, subfield codes present after every delimiter (catches `$Preciado`-type slips), no empty subfields, control-field lengths.
2. **Fixed-field:** LDR/008/006/007 validated position-by-position against the same JSON layouts the F8 dialog uses (single source of truth); country/language codes checked against shipped tables.
3. **Profile rules** (per-base, user-editable JSON): mandatory fields, non-repeatable fields, required subfields per tag, indicator value constraints. Ship a sane MARC21 default profile.
4. **Authority:** every controlled field must carry a valid `heading_link` whose text still matches the linked auth 1XX (else error with one-key "re-browse" fix). Optionally warn on 4XX matches (typed a *see* form).
5. **Auto-fill (only after 1–4 pass):** 001 — **kept as-is if present** (001s are often set in pre-processing / by the cataloguer; the app never overwrites one), assigned from the sequence only when empty; duplicate 001 in the base = ordinary validation error, resolved by the cataloguer. Then 005 timestamp, 003 org code, leader record-length/base-address recomputed, template `oncreate` finalizers, transactional write + index update.

## 9. Import / Export
- **Import wizard:** pick files/folder (.mrk/.mrc) → parse all → report grid (per record: OK / warnings / errors, drill-down) → choose: import all as *pushed* (trusted migration, keeps existing 001s, bumps sequence past max) or as *drafts* (each must earn Ctrl+L). Never partial-commits a file silently.
- **Plain-text authority list import:** one heading per line, `X va Y` / `X see Y` (separator configurable) → skeletal AUT records (1XX + 4XX).
- **Hub-and-spoke conveniences:** on import, controlled fields **auto-link** to AUT headings on exact normalized match (only misses need a manual F3); **batch push** command runs Ctrl+L over all drafts in a selection and reports the failures list. Satellites stay authority-consistent by importing the main machine's AUT export.
- **Export:** selection/search-result/base → .mrk, .mrc, or MARC-JSON; encoding options per [[bac-catalog-encoding]] lessons (UTF-8, BOM choice, EOL choice).

## 9b. Server backup / publish (`Cataloguer.Sync`)

Windows cataloguer, Linux main server — first-class scenario. **Backup/publish, not multi-user sync** (concurrent cataloguing is a future server-side component; the Core/Data split keeps that door open).

- **Transport:** SFTP over SSH via SSH.NET. Auth: SSH private key (path in settings, passphrase prompted per session, never stored) or Pageant/agent. Host-key fingerprint pinned on first connect (TOFU), verified thereafter.
- **Snapshot:** `VACUUM INTO` a temp copy (consistent mid-session) → upload as `catalog-YYYYMMDD-HHMMSS.db.tmp` → server-side rename on completion (atomic; a dropped connection never leaves a corrupt "backup"). Optionally also uploads a full .mrc/.mrk export of the pushed base so the server copy is consumable by any software.
- **Layout on server (configurable root):** `snapshots/` with retention (keep last N, prune older) + `latest/catalog.db` and `latest/export.mrc` at stable paths for server-side scripts (website generators, cron) to consume.
- **Triggers:** manual command (keymapped), on-exit prompt when pushes happened since last upload, optional auto-after-N-pushes. No daemon, no scheduler in-app.
- **Restore:** File → Restore lists server snapshots → downloads → opens **read-only side-by-side** first; overwriting the local DB is an explicit separate confirmation.
- **Isolation rule:** only this assembly may open sockets; it is invoked from the App layer and can be absent (feature off) with zero effect on cataloguing.

## 10. Settings (first-run wizard + dialog)
Institution MARC org code (003/040$a), default language, default cataloguing conventions ($e rda), classification default (082/084), controlled-field set, data folder location, keymap file, server sync (host, port, user, key path, remote root, retention N, trigger mode), UI language (**UI strings in resource files from day one; es-MX and en shipped** — the audience for a free Aleph-alike is substantially Spanish-speaking).

---

## 11. Milestones (each ends runnable + tested; order is dependency-driven)

| # | Deliverable | Acceptance criteria |
|---|---|---|
| **M0** | Solution scaffold, git repo, build script, empty window .exe | `dotnet publish` → double-clickable exe on a clean Win10 |
| **M1** | `Marc.Core`: model, .mrk + .mrc read/write | round-trip tests: parse→serialize→parse identical, incl. diacritics, `$$`, long records; malformed input → diagnostics not crashes |
| **M2** | DB layer + import/export + read-only record viewer + FTS search | import a 600-record .mrk folder < 5 s with full report; search finds them; export re-round-trips |
| **M3** | Editor grid + field/subfield commands + drafts + templates + keymap engine | create record from template start-to-finish keyboard-only; keymap.json edits take effect |
| **M4** | F8 fixed-field dialogs, all shipped layouts + code tables | 008 built entirely via dialog matches hand-written reference byte-for-byte |
| **M5** | AUT base: browse index, F3 flow, linking, see-refs, ripple, text-list import | rename an auth heading → all linked bibs update in one transaction with count reported |
| **M6** | Full Ctrl+L pipeline + profiles + auto-fill + Ctrl+W | seeded error corpus (incl. real-world slips) each caught with correct field ref; clean record pushes with correct 001/005/LDR |
| **M7** | Settings, first-run wizard, i18n pass, F1 field help, polish | new user on clean machine reaches first pushed record unaided |
| **M7b** | `Cataloguer.Sync`: SFTP snapshot upload, retention, restore, triggers | upload to a real Linux box; kill the connection mid-upload → server shows no corrupt/partial final file; restore round-trips |
| **M8** | Inno Setup installer + user manual (md→html) | install → catalogue → uninstall leaves data intact; version upgrade migrates schema |

Rough shape: M1, M5, M6 are the heavyweight milestones. Every session ends with tests green and the app runnable.

## 12. Risks / decisions already taken
- **UTF-8 only, no MARC-8** (v1): LC downloads are available UTF-8; MARC-8 tables are large and low-value here. Importer *detects* MARC-8 (leader/09 blank) and refuses with a clear message rather than corrupting.
- **Single-user file DB:** fine for the target; multi-user later = swap Data layer, Core untouched.
- **Keymap fidelity risk:** mitigated by §6.2 review (you) + config file.
- Diacritics/normalization is where cataloguing software usually rots — hence one normalization function, exhaustively unit-tested, used by browse, search, and validation alike.

## 13. Needed from you before M0
1. Red-pen the keymap table (§6.2).
2. A name.
3. Confirm milestone order (or reorder — e.g. if you want authority control before the fixed-field dialogs).
