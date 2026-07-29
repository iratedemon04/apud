# Aleph workflow reference (from user's real ALEPH v24 screenshots, 2026-07-28)

*The user's daily cataloguing environment: ALEPH 500 v24 (hercules.itam.mx, bases
BRB01=bib / BRB10=authority). Apud's UI should follow this logic as closely as
sensible. Studied from 5 screenshots of his actual workflow; this file is the
durable record of what they showed, per Apud module.*

## Editor layout (→ Modules 5b viewer, 6 editor)

One field = one block of rows, four columns:

| Field name (italic, from tag) | Tag (red, e.g. `150`) | Indicators | Content |

- **Each subfield is its own row**: subfield code (single letter, narrow styled
  column, red/underlined in Aleph) + value text beside it. Repeated subfields
  stack vertically under one tag. NO `$` notation anywhere on screen (confirms
  the standing decision).
- Field-name labels are derived from the tag and localized ("Topical Term" for
  150, "SeeF.Trac.Topic" for 450, "Dewey Class No." for 083, "Catalog. Source"
  for 040). Apud needs a tag→name table (Spanish) — plan for Module 6/10.
- LDR and control fields (001/005/008) display on a single row; **blank
  positions render as `^`** in fixed data (e.g. `00000nz^^^22^^^^^n^^^^^^`),
  fill character `|` shown literally. Blank indicators render as underscores.
- A **header bar** (red text in Aleph) is always visible above the record:
  base + system number + heading/title summary ("AU System No. 192270
  Responsabilidad social de la empresa -- ...").
- Left pane: tree of **open records** ("Edit Records": BRB10-192270,
  BRB01-319050, ...) — several records from different bases open at once, plus
  Import Records and Triggers nodes. Apud 5b starts with one record at a time,
  but the nav pane should not preclude an open-records list later.

## 008 fixed-field dialog (→ Module 7, F8)

- Modal form, OK/Cancel; **two columns of labeled single-char text boxes**;
  every label carries the position number: "Kind of record (09)", "Heading
  use--main or added entry (14)".
- Undefined positions get a (disabled) box too — the map is positional and
  complete, nothing hidden.
- "Date entered on file (00-05)" pre-filled with today (YYMMDD).
- Separate position sets per record type (screenshot shows the AUTHORITY 008;
  bib has its own).

## Search (→ Module 5c)

- **Advanced Search**: three rows of [field-scope dropdown + text box] joined
  by AND/OR/NOT radio per row; base selector on top; "Todos los campos" = the
  all-fields scope (Apud: the `anytext` FTS column; title/author/subjects/cn
  are the scoped ones).
- **Search history grid** below the form, accumulating per session: request
  ("( Palabras= internet mexico )"), database, hit count. Rows are reusable
  (Show/Save/Load) and combinable via **Cross sets** (AND/OR/first-not-second).
  Even a plain-word search lands in the history — the history IS the workflow:
  he iterates queries and compares counts (e.g. "fuero politico" 0 → "fuero" 26).
- Left tree: **Find** (form search), **Browse** (headings list → our Module 8),
  **Show** (result display).
- Apud 5c minimum honoring this: field-scoped search + a session search-history
  list with hit counts. Cross sets are a nice-to-have (docs/DEFERRED.md if cut).

## Base switching (→ Module 5b)

- Menu action ("Connect to..."), flat list of bases with a radio mark on the
  current one. Not tabs. Apud: BIB/AUT switch as a menu (and the nav pane
  reflecting it) feels native to him.

## Import (→ Module 5c wizard)

- Convert → inspect → load pipeline: input file + conversion-procedure
  dropdown; converted outputs listed with timestamps; a **Record Info pane
  shows the raw record before committing** (tags + `$$a` raw form in Aleph).
- Aleph's pane exposes encoding damage ("GeografÃ¤..." mojibake) — his real
  pain. Apud's UTF-8-only .mrk pipeline removes the conversion step entirely;
  the wizard equivalent is Analyze report → inspect → Commit, which matches
  this shape 1:1.
- His 040 at work is MX-MxITA (ITAM); the BAC catalogue uses MX-MxBAC.
