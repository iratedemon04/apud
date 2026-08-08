# DEFERRED triage — pre-v1 worksheet

> **Purpose.** One pass over `docs/DEFERRED.md` deciding, per item, **fix-now** (before v1.0.0) vs **ship-as-known** (document and move on). This file is the pre-digest so the session is fast: walk sections A→D with the user, get a yes/no each, then do the fix-nows, then go to `apud module 12 GO`.
>
> **Prepared 2026-08-06** right after the editor rewrite landed (`c0fb761`). Tree clean, 312 tests green, exe published. **This is a decision session — the user gates every call; the "lean" is a recommendation, not a done deal.**
>
> **Out of scope here:** the ~169 MB packaging fork (framework-dependent vs self-contained) is a **Module 12** decision, not triage — see DEFERRED.md 2026-08-02 (packaging) and STATE.md handoff. Don't decide it here.

---

## A. Recommend FIX before v1 (cheap; correctness / code-hygiene)

- [ ] **A1 — String-built SQL in `RecordRepository.CountLinksTo` (RecordRepository.cs:~411).** The lone query that interpolates its id (`$"... auth_record_id = {authRecordId}"`) instead of a parameter. No injection risk today (it's a `long`), but it's the only string-built SQL in the codebase and invites a future copy-paste with a string. **Lean: fix now** — one-line parameterization, ~5 min.
- [ ] **A2 — MARC_OUT mirror staleness (DEFERRED 2026-07-31 post-9).** Two small leaks: Ctrl+S demote-to-draft leaves the old `MARC_OUT\<001>.mrk` in place, and changing a record's 001 then re-pushing orphans the old-numbered file. Low-harm (DB is source of truth) but visible clutter. **Lean: fix now if quick** — delete the .mrk on demote-to-draft; remember the previous 001 to delete its file on 001 change. If it balloons, bump to B.

## B. DECIDE (judgment calls — my lean + why)

- [ ] **B1 — Ripple single-transaction (DEFERRED 2026-07-31 (8) scope D, APPROVED).** AUT push → `RewriteLinkedBibHeadings` rewrites each linked bib in its own `Update` transaction, not one ambient transaction. A mid-ripple crash could leave some linked bibs rewritten and others not. **Single-user, offline, tiny link counts → very low risk. Lean: ship-as-known for v1**, note in release notes; thread an ambient transaction post-v1 if ever multi-user.
- [ ] **B2 — `MainForm.cs` god-object (DEFERRED 2026-08-02 structural).** Was 2,425 lines at review; now **2,488**, but the editor (~648 lines) was extracted into `RecordGrid.cs` by the rewrite, so the worst chunk is already out. Still hosts search + sidebar + menus + dialogs + sync orchestration. **Lean: ship-as-known** — it's navigability debt, not a bug, and the heavy logic is tested below `Apud.App`. Optional ~1-day cleanup (extract a search controller / sync coordinator) is better spent *after* v1 unless the user wants it now.
- [ ] **B3 — `List(base)` whole-base loads on the export/backup path (ExportEngine.cs:~19, ISnapshotSource.cs:~63).** Search UI paths were already made to scale; export + backup still materialize the whole base in memory. Fine for the user's ~750 records; the design targets up to ~500k. **Lean: ship-as-known for v1** (export/backup inherently touch every row; his real data is small) — revisit only if a large catalogue is actually exported. Confirm the user isn't about to load a huge base.
- [ ] **B4 — MARC_OUT backfill (DEFERRED 2026-07-31 post-9).** Records pushed *before* mirroring existed (e.g. 758) have no `.mrk` until re-pushed. **Lean: ship-as-known**, but cheap to offer a one-shot "Export all pushed records to MARC_OUT" action if the user wants the folder complete for v1. His call.
- [ ] **B5 — Server sub-folder names `bib`/`aut` (DEFERRED, handoff note).** The per-record mirror uploads under `bib/` and `aut/` rather than matching the local `MARC_OUT`/`MARC_OUT_AUT` names. Purely cosmetic on the server. **Lean: ship-as-known** unless the user wants them aligned.

## C. SHIP AS KNOWN (already approved, or clearly fine — just confirm, don't rebuild)

These were decided already or are features/edge cases not needed for v1. Read them off; expect quick "yes, ship":

- **Module 9 scope calls A/B/E** (APPROVED 2026-08-01): fixed-field coded-value mismatches are warnings-not-errors; an *unlinked* controlled heading doesn't block a push (only rotted links error); post-push warnings clear + report a count (Ctrl+W re-shows with correct jumps).
- **Module 11 scope calls** (APPROVED / "keep it"): exports are `.mrk` not `.mrc` (MarcEdit converts); on-exit "back up first?" prompt stays; restore is download-then-open (never overwrites the working DB); TOFU host-key trust (no known_hosts/CA); one SFTP connection per operation; parallel upload deferred (incremental upload makes it moot).
- **Country/language lookup TABLE CONTENTS** (deferred M7→M9): framework ships, positions write correct bytes; membership validation is warning-only noise against the user's bracket/fill data. Ship without.
- **006/007 fixed-field layouts**: engine is generic, both are zero-code JSON additions later; a book catalogue rarely hand-edits them. (User already declined.)
- **F1 field help** covers ~55 common BIB+AUT tags, extendable via `taghelp.json`; never empty (falls back to the tag name). Ship.
- **`FirstRunDone` flag in ui.json** (M10 scope call): one-time onboarding state; the only remembered cross-session value besides `LastFolder`. Ship (or drop `MaybeShowIntro()` if the user objects — one line).
- **Authority extras**: AUT-side Ctrl+F4 browse; reciprocal "used for" tracing on the 1XX; reference-aware search in Apud's own box; MARC display punctuation in the browse list. All features, none needed for v1 (the OPAC owns patron-facing see-from).
- **Search-history cross-set operations** (AND/OR/NOT between past result sets): future, only if asked.
- **`005` uses `DateTime.Now`** (PushService.cs:~130): a testability seam, not a bug. Low priority.
- **N+1 loads in the authority stages** (PushService.cs:~80): a non-issue at catalogue scale; do NOT optimize speculatively.
- **FTS + heading_index kept in step by hand** in `RecordRepository.Update`: correct today (and commented); a future write-path could forget one. Consider a single write choke-point post-v1.

## D. Re-verify (rewrite may have changed these)

- [ ] **D1 — Blank-field tag edge case (DEFERRED 2026-07-31 (6)).** Original: a just-inserted field's `"   "` tag sorts as control until a real tag is typed, and typing a *value* before the tag writes ControlData, which then blocks the control→data conversion. The editor rewrite makes a blank field render as **data shape** (RecordLayout) and steers F6→type-tag-first, so this is now much harder to hit — but the underlying `EditorDocument`/model behavior is unchanged. **Action: confirm on the new editor whether it's still reachable; if effectively dead, close the item; if reachable, decide fix-vs-ship.**

---

## After the triage

1. Do the agreed **fix-now** items (A, plus any B promoted). `dotnet test` green, publish, user test-drive, commit + push.
2. Update `docs/DEFERRED.md`: mark shipped-as-known items as such (keep them for reference, per the user's standing request), delete/annotate anything fixed or closed.
3. Then **`apud module 12 GO`** — read STATE.md + `docs/PLAN.md` §11 (M8 row = installer) + §2 (Inno Setup); present the step list; build. Decide the packaging fork there. Then a testing round → tag `v1.0.0-rc.1` → fresh-Windows-install acceptance drive → re-tag the same commit `v1.0.0` on green. Bump `<Version>` 0.1.0 → 1.0.0.
