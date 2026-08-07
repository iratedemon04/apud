# Apud UI Rewrite — Record editor: replace `DataGridView` with a real textbox grid

> **Status: APPROVED PLAN, NOT YET IMPLEMENTED (2026-08-05; feasibility-reviewed 2026-08-06).** This is the plan to execute when the user says "apud ui rewrite." It was designed and approved in a prior session; a copy also lived at `~/.claude/plans/nifty-dancing-robin.md`. A 2026-08-06 senior-dev review confirmed the approach is sound — it replaces the wrong abstraction (`DataGridView`'s shared-editor state machine) with the textbox grid that already works bug-free in this repo (`FixedFieldForm`) — and its caveats are folded in below: see the new **Build order** section (spike the two unproven bits BEFORE deleting anything) and the risk notes flagged **⚠** in Design and Visual fidelity. Before starting, re-read the current `MainForm.cs` editor section — line numbers below may have drifted. Follow the plan's own Verification section (screenshot parity first).

## Context

The record editor is a `DataGridView` (`MainForm._viewer`). A `DataGridView` is a *virtual* grid: it lends **one shared editing control** to whichever cell is active and runs an "edit-mode" state machine (BeginEdit → EditingControlShowing → CellEndEdit → EndEdit). Every recurring bug — cursor vanishing after F6, the highlight flash, the Windows *ding* when typing a new tag, losing focus on right-arrow — comes from fighting that state machine plus `RenderRecord()`, which does `Rows.Clear()` + full rebuild on every structural change and then hand-restores the cursor/edit (fragile: a blank field is subfield-index `-1`, but the instant it gains a subfield it's `0`, so the restore misses).

We've patched this ~4 times; it's whack-a-mole because the tool is wrong for a small, richly-interactive structured editor. The fix (user's own model, approved): make the grid **a table of real `TextBox` controls** — one box per element, each with its own `MaxLength` rule — laid out one **row per subfield line**. Real textboxes have native, deterministic focus/caret/selection and no edit-mode; `box.Focus()` cannot "fail to land," and an empty tag box simply accepts typing (no full placeholder → no ding). This is exactly how `FixedFieldForm` (Ctrl+F3) is already built, and it has none of these bugs.

**Scope guard:** only the editor's *view layer* changes. `EditorDocument`, `MarcRecord`, validation, push, authority linking, `.mrk`, the sidebar, search — all untouched. The 305 existing tests stay green.

## Row model (one row per subfield line)

Columns, aligned like today: **name | tag | ind | code | value**.

- **Leader row:** name="LDR" + one wide box (24 chars, `^`=space). Part=`Leader`.
- **Control field (00X):** name + tag box(3) + one wide control-data box (any length). Parts=`Tag`,`ControlData`.
- **Data field, first subfield line:** name + tag(3) + ind(2) + code(1) + value(∞). Parts=`Tag`,`Ind`,`Code`,`Value`.
- **Data field, continuation lines:** blank name/tag/ind + code(1) + value(∞). Parts=`Code`,`Value`.
- **Empty/new data field:** one line with tag+ind + an empty code/value pair (the "phantom" subfield, subfieldIndex `-1`) so a new field is typed through with **no rebuild**.
- **New/blank field renders as data shape** even though its placeholder tag `"   "` is technically control per `MarcField.IsControl` — the data shape is what lets F6→type→Tab flow without a rebuild. (No change to `MarcField`/`IsControl`; the shaping rule lives in the new pure layout function.)

## Design

Two new files in `src/Apud.App`, plus edits to `MainForm.cs`.

### 1. `RecordLayout.cs` — pure, unit-tested (replaces the editor's use of `RecordDisplay.Build`)
A pure function `IReadOnlyList<BoxSpec> RecordLayout.Build(MarcRecord)` where each `BoxSpec` carries everything the view needs and nothing WinForms:
```
record BoxSpec(int Row, BoxPart Part, int FieldIndex, int SubfieldIndex,
               string Text, int MaxLength, bool ReadOnly, string? Name /*maroon label, first box only*/)
enum BoxPart { Leader, Tag, Ind, Code, Value, ControlData }
```
This is where the row-shaping rules above live (control vs data vs blank-as-data, phantom subfield, `^`/`_` display conventions reused from `RecordDisplay`). Because it's pure, the tricky "which boxes, what rules" logic is fully testable without a GUI. `RecordDisplay.HeaderText` stays and is still used by `UpdateHeader`; `RecordDisplay.Build` stops being used by the editor.

### 2. `RecordGrid.cs` — the view (a `Panel`/`UserControl`, dumb)
Owns the textbox layout and focus; holds a reference to the current `EditorDocument`. Responsibilities:
- **`Rebuild()`** — clear child controls, run `RecordLayout.Build`, and create one real control per `BoxSpec` into a `TableLayoutPanel` (fixed absolute widths name140/tag42/ind34/code26, value=Fill; `RowStyle` AutoSize) inside an `AutoScroll` panel. Micro boxes get `MaxLength` = 3/2/1; value/control boxes unlimited. Each box stores its `BoxSpec` identity (in `Control.Tag`). ReadOnly boxes (blank continuation tag/ind, the maroon name labels) are `Label`s or read-only.
- **Commit** — each editable box commits to `EditorDocument` on `Leave` (and on demand via `CommitFocused()`), calling the SAME model methods already there: `SetLeader`/`SetTag`/`SetIndicators`/`SetSubfieldCode`/`SetSubfieldValue`/`SetControlData`. After a commit that changed structure (a tag that crosses control/data or creates the first subfield), raise `StructureChanged` → `Rebuild()` + focus the caller-intended box.
- **Focus/nav** — `FocusElement(fieldIndex, subfieldIndex, BoxPart)` (replaces `SelectCell`/`SelectFieldRow`; a plain `box.Focus()`, reliable, auto-scrolls into view). Micro boxes `SelectAll()` on `Enter` (real-textbox reliable — no timing). **Tab is handled explicitly** in `RecordGrid` (KeyDown): Tab walks the logical flow tag→ind→code→value→(next subfield's code…), committing as it goes, so a structural rebuild mid-Tab still lands focus on the intended box.
  - **⚠ This is the one spot where complexity moves INTO our code.** The `DataGridView` edit-mode state machine goes away, but "walk the Tab flow, commit as you go, and re-land focus on the intended box after a structural `Rebuild()`" is now ours to own. It is tractable *because* real-textbox focus is deterministic (`box.Focus()` cannot silently fail the way the old `BeginEdit`/`EditingControlShowing` race did) — but this is where any *new* whack-a-mole would appear if we're sloppy. Keep all traversal logic in one method (don't scatter per-key handlers), and treat every rebuild-then-focus path (F6/F7/Ctrl+F5/Ctrl+F7/copy-paste) as a mandatory line on the manual-drive checklist, not an afterthought.
- **`CurrentRef()`** — read the focused box's `BoxSpec` → `(FieldIndex, SubfieldIndex)` (replaces the `DataGridView.CurrentCell` version).
- **Events** — `EditCommitted` (→ MainForm updates header/sidebar/dirty), so MainForm keeps `UpdateHeader`/`UpdateSidebarItem`.

### 3. `MainForm.cs` — swap the control, delete the state-machine code
- Replace field `DataGridView _viewer` with `RecordGrid _grid`; add it to `_recordView`.
- **Delete** (DataGridView-only machinery): `ViewerEditingControlShowing`, `PrimeEditingBox`, `MicroAdvance`, `RenderRecord`/`CaptureCell`/`RestoreCell`/`_resumeEditAfterRender`, `ApplyCellEditability`, `ViewerCellEndEdit`, `NewColumn`, `_editCol`/`_caretAtStartNextEdit`, and every `_viewer.EndEdit()/BeginEdit()/CurrentCell/Rows` call.
- **Rewire the command handlers** (same logic, new verbs). Each currently does `_viewer.EndEdit(); …EditorDocument op…; RenderRecord(); SelectCell(...)`. New shape: `_grid.CommitFocused(); …same EditorDocument op…; _grid.Rebuild(); _grid.FocusElement(...)`. Applies to: `BeginEditCurrentCell`(Insert), `OrderFieldsCommand`(Enter), `NewField`(F6), `NewSubfield`(F7), `DeleteCurrentField`(Ctrl+F5), `DeleteCurrentSubfield`(Ctrl+F7), `CopyField`/`PasteField`/`CopySubfield`/`PasteSubfield`, `EditFixedField`(Ctrl+F3), `BrowseAndLinkHeading`(Ctrl+F4), `ValidateRecord`/`PushRecord`, `JumpToSelectedFinding`, `ShowSelectedOpenRecord`, undo/redo, delete-record.
  - F6 `NewField`: `InsertBlankFieldAfter` → `Rebuild()` → `FocusElement(newIdx, -1, Tag)`. Because the new row is data-shaped and static, the user types the tag and Tabs with no further rebuild until a control-tag or F7.
- **`ProcessCmdKey`/`ShouldDispatch`**: replace the `_viewer.Focused || _viewer.IsCurrentCellInEditMode` / `!_viewer.IsCurrentCellInEditMode` checks with "focus is inside `_grid`" (the existing `FocusedControl() is TextBoxBase` test already routes plain keystrokes to typing and modified/F-keys to commands — it keeps working because the boxes are real `TextBox`es). `field.order` (Enter) fires when focus is in `_grid`.

### Multi-field delete — via a row gutter (confirmed with user)
`DeleteSelectedFields` (Ctrl+Shift+F5) relied on `DataGridView` multi-cell selection. `RecordGrid` gets a **narrow clickable gutter cell at the left of every field's first row**: click selects that field (highlight), Shift+click extends a range, Ctrl+click toggles one. The gutter tracks a `HashSet<int>` of selected field indices; `DeleteSelectedFields` reads that set and calls the existing `EditorDocument.DeleteFields(indices)` (unchanged). Selection clears on Rebuild/edit. So the pruning workflow is preserved.

## Visual fidelity — HARD REQUIREMENT (must stay pixel-close to today / Aleph)

The user is very content that the current UI is very close to real Aleph. The current look must be reproduced exactly. It is nothing but fonts + colors + fixed widths + no borders, all of which a borderless textbox grid can match 1:1. `RecordGrid` reproduces every value below; the maroon header bar (`_recordHeader`) and the whole surrounding layout are untouched.

- **Borderless cells:** every `TextBox` gets `BorderStyle = None`; the `TableLayoutPanel` uses no cell borders and no gridlines; no column-header row. (This is what makes textboxes read as grid cells, not form fields.)
- **Background:** `SystemColors.Window` (white); rows tight — height ~17, `Padding = 0`.
- **Column widths (unchanged):** name 140, tag 42, ind 34, code 26, value = Fill (start 200).
- **Per-column fonts/colors (unchanged):**
  - name — `Segoe UI 8.25 Italic`, `Maroon`, read-only (a `Label`; first line of a field only).
  - tag — `Consolas 9.75 Bold Underline`, `Gray`.
  - ind — `Consolas 9.75` regular.
  - code — `Consolas 9.75 Bold Underline`.
  - value — `Consolas 9.75 Bold`, **word-wrapped** (multiline, auto-height row) so long fields wrap exactly like today's `WrapMode=True` + `AutoSizeRows`. **⚠ HIGHEST-RISK PARITY DETAIL — the one behaviour with no working precedent in this repo.** A multiline `TextBox` does *not* report a preferred height for its wrapped content, so a `RowStyle=AutoSize` row will **not** grow to fit it on its own. The row/box height must be computed explicitly each layout — measure the wrapped text at the value column's actual width (`TextRenderer.MeasureText` with `TextFormatFlags.WordBreak`, or a borderless auto-sizing `Label`/`RichTextBox`) and set the box + row height from that. `FixedFieldForm` never had to solve this (its boxes are single-line, fixed-width positions), so nothing in the codebase proves it yet. **Prototype this box first — see Build order below.**
- **"Apud-blue" current-box tint:** the focused editable box takes `BackColor = Color.FromArgb(225, 238, 250)` with black text and reverts to white on `Leave`, reproducing today's `SelectionBackColor`. The gutter's field selection uses the same tint.
- **Blank conventions reused unchanged:** `^` for blank in LDR/control data, `_` for blank in indicators (from `RecordLayout`, same as `RecordDisplay` today).

A side-by-side before/after screenshot is part of the verification step — it should be indistinguishable.

## Build order — de-risk the unknowns FIRST (before deleting any `DataGridView` code)

This is necessarily a **big-bang** swap of the editor's view layer — you can't run half a `DataGridView` and half a `RecordGrid`, so there's no incremental "strangler" path. The safety therefore comes from proving the two unproven assumptions in a **throwaway spike while the old editor still runs**, so a dead end is discovered before the old machinery is gone and you're committed:

1. **Spike the word-wrapped auto-height value box** (the Visual-fidelity ⚠ above). One multiline borderless `Consolas 9.75 Bold` box in a `RowStyle=AutoSize` `TableLayoutPanel` cell at the real value-column width; feed it a long 245/520; confirm the row grows and wraps pixel-for-pixel against today's grid. This is the single most likely thing to fight back — learn it now, not after the swap.
2. **Spike the `F6 → type tag → Tab → ind → code → value` flow** on the spike grid — the exact bug this whole rewrite exists to kill. Confirm the three things the `DataGridView` never got right: no *ding* typing into an empty tag box, `box.Focus()` always lands, and a structural `Rebuild()` mid-flow re-focuses the box the user meant to be in.

Only once **both** spikes hold: write `RecordLayout.Build` + its tests (pure, no GUI), then `RecordGrid`, then swap `MainForm` and delete the old machinery in **one** change. Keep the spike code as the seed for `RecordGrid` — don't throw away what you just proved.

### Non-risks — considered and dismissed (don't re-litigate these)
- **One real control per box vs. the virtual grid's single shared editor.** A book record is dozens of fields / ~100–200 boxes; the `FixedFieldForm` 008 editor already renders ~40 real boxes with zero lag. Fine at this scale. `Rebuild()` disposing + recreating all child controls on each structural change is also fine — just **dispose the old children** so window handles don't leak, and prefer a full rebuild over incremental diffing (simplicity beats an optimization a record this small will never need).
- **The multi-field-delete gutter.** A dedicated gutter + `HashSet<int>` is *cleaner* than today's abuse of `DataGridView` multi-cell selection to mean "selected fields" — it's a step up, not a workaround.

## Files

- **New:** `src/Apud.App/RecordLayout.cs`, `src/Apud.App/RecordGrid.cs`
- **New tests:** `tests/Apud.Tests/RecordLayoutTests.cs` (row-shaping: control vs data, blank-as-data, phantom subfield, continuation lines, `^`/`_` conventions, maxlengths)
- **Edit:** `src/Apud.App/MainForm.cs` (swap control, delete machinery, rewire handlers)
- **Keep as-is:** `EditorDocument.cs`, `RecordDisplay.cs` (only `HeaderText` still used), `Commands.cs`, `keymap.json`, all of `Marc.Core`/`Apud.Data`.

## Verification

Because the swap is big-bang (see Build order), these three checks **are** the safety net — and none is optional. The behaviour this rewrite fixes (focus, caret, the ding) lives in the view layer and cannot be unit-tested, so the parity gate + green tests + the hand-driven checklist together stand in for the tests we can't write. `RecordLayoutTests` prove the *shaping* logic; the *feel* is proven only by driving it.

1. **Look first:** capture a "before" screenshot of the current editor on a real record, and an "after" screenshot of the rewrite on the same record; they must be indistinguishable (fonts, colors, widths, wrapping, maroon names, header bar). This is a gating check — if it doesn't match, it isn't done.
2. `dotnet build` clean; `dotnet test` → 305 existing + new `RecordLayoutTests` all green (model untouched).
3. Publish self-contained, launch, and drive by hand (GUI can't be unit-tested):
   - **F6 → immediately type a tag** (no mouse, no ding), Tab → indicators → code → value; type straight through.
   - Right-arrow / Tab move box→box without deselecting; the code box is not skipped.
   - F7 adds a subfield line and lands in its code box; Ctrl+F5 deletes the field and lands on a neighbor; Ctrl+F7 deletes a subfield.
   - Ctrl+F3 on LDR and an 008 (unchanged dialog); Ctrl+F4 on a heading; Ctrl+Z/Ctrl+Y; copy/paste field & subfield; Ctrl+W/Ctrl+L; click a finding to jump.
   - Control fields and LDR show a single wide box.
