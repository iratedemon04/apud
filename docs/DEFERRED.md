# Deferred
*(One line per discovered-but-not-fixed item. Reviewed at each module close.)*

- 2026-07-28 (5c): Aleph-style cross-set operations on the search history (AND/OR/NOT between past result sets) — history grid exists, combination doesn't; consider for a later version if he asks.
- 2026-07-31 (6): A just-inserted blank field has tag "   " (3 spaces), which sorts below "010" so the model treats it as a control field until a real tag is typed. Guided flow (F6 focuses the tag cell → type tag first) avoids trouble, but typing a *value* into a blank field before its tag writes ControlData, which then blocks the control→data conversion with a "empty the field first" message. Harmless, self-explaining; revisit if it annoys in practice.

