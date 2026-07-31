# Deferred
*(One line per discovered-but-not-fixed item. Reviewed at each module close.)*

- 2026-07-28 (5c): Aleph-style cross-set operations on the search history (AND/OR/NOT between past result sets) — history grid exists, combination doesn't; consider for a later version if he asks.
- 2026-07-31 (6): A just-inserted blank field has tag "   " (3 spaces), which sorts below "010" so the model treats it as a control field until a real tag is typed. Guided flow (F6 focuses the tag cell → type tag first) avoids trouble, but typing a *value* into a blank field before its tag writes ControlData, which then blocks the control→data conversion with a "empty the field first" message. Harmless, self-explaining; revisit if it annoys in practice.
- 2026-07-31 (7): 006 and 007 fixed-field layouts NOT shipped this module (scope call — a book catalogue rarely hand-edits them). The engine is generic (field+material/category → embedded JSON layout), so both are pure data additions later with ZERO code: author `007-<cat>.json` (keyed by 007/00) and `006-<mat>.json` (keyed by 006/00), embed, wire Ctrl+F3 to route 007/006 fields to them. Say the word and they're a short follow-on.
- 2026-07-31 (7): Country/language code TABLE CONTENTS (marc-countries, marc-languages) not shipped. The `lookup` attribute already travels in the layouts; positions 008/15-17 and /35-37 write correct bytes as free 3-char boxes. The actual tables + membership validation belong to the Module 9 validator that consumes them (dialog shows "(country code)"/"(language code)" hints for now).

