# Apud — STATE
*(Session-survival file. THIS repo copy is authoritative; a fresh session reading this + docs/PLAN.md can continue without re-explaining. Update at end of every session and module close.)*

## Now
- **Module 4 (Database) BUILT — 34 tests green — awaiting user "move on".**
- Next on approval: **Module 5 — Import/Export/Viewer** (first personally-useful build: import wizard + report, .mrk export, read-only record viewer, FTS search; he imports his own 654 records HIMSELF through the UI).
- Environment note: .NET 8 SDK 8.0.423 installed via winget (machine had runtime only). Git identity: repo-local iratedemon04 <iratedemon04@gmail.com>; history rewritten 2026-07-27 to purge old-account attribution (ranakamikaze), force-pushed by user; contributors list verified clean via API (graph cache may lag).

## Module sequence (user gates every transition — build ONLY current module)
1. Scaffold ✔ · 2. MARC model + .mrk · 3. ~~.mrc ISO 2709~~ **CUT 2026-07-27** (user: .mrk is THE file format; MarcEdit converts to .mrc effortlessly — numbering kept stable) · 4. Database · 5. Import/Export/Viewer · 6. Editor+keymap+templates · 7. F8 fixed-field dialogs · 8. AUT+F3 · 9. Ctrl+L pipeline · 10. Settings/i18n/help · 11. Sync (SFTP) · 12. Distribution (installer, manual, release tag)

## Done
- **Module 4 — Database** (2026-07-27): Microsoft.Data.Sqlite 8.0.11; ApudDatabase (open/create/in-memory, FK+WAL pragmas, user_version migrations, full v1 schema incl. heading_link/heading_index/record_fts created up front); StoredRecord/RecordSummary/RecordStatus; RecordRepository — Insert/Update (fields+links rewritten in ONE transaction; AuthLinkId round-trips), Load, List (245/1XX title, control-number order), Delete (cascades), CountLinksTo, NextControlNumber + BumpSequencePast (per-base), settings. Partial unique index on (base, control_number) WHERE NOT NULL → drafts without 001 coexist; duplicate 001 throws. Subfields packed with U+001F. 11 new tests; DB round-trip proved via .mrk byte equality. Module 2 dialect facts: HIS files are the spec; {dollar} literal-$ kept (LC-confirmed).
- **Module 2 — MARC model + .mrk** (2026-07-27): Marc.Core model (MarcRecord/MarcField/MarcSubfield, RecordKind from LDR/06, AuthLinkId slot on fields, order-preserving); Mrk namespace: MrkReader (permissive, line-numbered recoverable diagnostics — structural sins preserved as data for the Module-9 validator), MrkWriter (canonical UTF-8 no-BOM/LF via ToBytes), MrkDiagnostic/MrkReadResult. 23 tests: round-trips (diacritics, 008 trailing spaces, repeated fields, multi-record, CRLF, BOM, {dollar}), diagnostics (junk lines, $Preciado preserved+roundtripped, missing/short leader, missing separator, empty subfields, bad tags). Full live-catalog sweep: 654/654 clean.
- **Module 1 — Scaffold** (2026-07-27): repo `C:\Users\ACV\Projects\Apud\` (git, main branch); Apud.sln with Marc.Core / Apud.Data / Apud.Sync / Apud.App / Apud.Tests wired per dependency rule; MainForm shell (menu stub, message bar); MarcConstants + 1 wiring test green; publish.ps1 → self-contained `publish\Apud\Apud.exe` (AssemblyName=Apud, v0.1.0), smoke-launched OK; docs/PLAN.md, docs/STATE.md, docs/DEFERRED.md.
- Full technical plan: `docs/PLAN.md` (architecture, schema, keymap proposal, validation pipeline, milestones). Self-validated once; known verify-item: MarcEdit literal-$ convention (check in Module 2).

## Decisions
- Name: **Apud**. Repo root `C:\Users\ACV\Projects\Apud\`; projects Marc.Core / Apud.Data / Apud.Sync / Apud.App / Apud.Tests.
- Single-machine philosophy; hub-and-spoke for teams; live multi-user REJECTED.
- 001: never overwrite existing; sequence fills empty only; duplicates = plain validation error.
- Sync = SFTP backup/publish only (SSH.NET, atomic tmp+rename, VACUUM INTO).
- Auth links travel with fields in memory; fields+links rewritten in one transaction.
- Keymap is context-scoped config (keymap.json); menus render from command table.
- Working method: per-module loop = mini-spec list → build in order → user test-drives → user says "move on". Mid-build plan contradictions: stop, report, amend plan, continue. Discoveries not fixed now → docs/DEFERRED.md.
- STATE.md ritual: update before end-of-session summary, every session.
- **GitHub (plan change 2026-07-27, supersedes "Module 12" timing):** remote is `https://github.com/iratedemon04/apud.git`; push the whole repo at EVERY module close (and this initial wiring push). Repo created by user.
- **UI never displays dollar-sign subfield notation** (user decision): editor renders subfield codes as their own styled cells/boxes next to the text, Aleph-fashion; `$a` syntax lives only in .mrk files, never on screen. Applies from Module 6.
- **.mrk is the only file format** (import/export/templates); .mrc cut — MarcEdit handles conversion externally.
- Editor "spike" (throwaway UI prototype) proposed by Claude, REJECTED after discussion: user's option-value argument — core layers (Marc.Core/Apud.Data) are UI-independent, so a Module-6 UI surprise costs only thin-layer rework, not weeks; insurance not worth premium. Original 12-module sequence stands. Guiding frame: zero invention, 1960s solved problem, assembly not research; all durable logic lives below Apud.App.

## Next from user
- **"Move on" to close Module 4 → opens Module 5 (Import/Export/Viewer).**
- §6.2 keymap red-pen — due at START of Module 6, not before.
- GitHub repo creation — Module 12, I'll specify what I need then.
