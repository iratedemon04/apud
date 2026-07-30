# UI inspiration — the Aleph editor look

*(User, 2026-07-28: "the point of making an Aleph-style editor is that Aleph is
very very dumb compared to alternatives like Koha — it needs to feel like a FREE
EDITOR, not boxes that you fill out." This folder holds the reference. If the
original screenshots exist as files, drop them here as aleph-1.png … aleph-5.png —
Claude saw them in-chat 2026-07-28 but could not extract the files.)*

## The one that matters: screenshot 5 — the cataloguing editor (BRB10-192270)

What the record screen looks like in real Aleph v24, and what Apud's viewer/editor
must feel like:

- **White page, dense text, no boxes.** No cell borders, no gray separator lines,
  no visible grid. Rows sit tight together like lines in a text editor.
- Four visual columns, aligned but unboxed:
  1. Field name — *italic*, dark red/maroon, small ("Leader", "Control No.",
     "Fixed Data", "Topical Term", "SeeF.Trac.Topic").
  2. Tag — **bold, red, underlined** (`LDR` `001` `005` `008` `040` `083` `150` `450`).
  3. Indicators — plain, `___ _`-style underscores for blanks (e.g. `04` on 083).
  4. Content — **bold black monospace-ish text**, subfield codes as single
     **red underlined letters** (a, x, 2, 5) hanging in a narrow column left of
     each value line; one line per subfield, repeated subfields stack.
- Blanks in LDR/008 shown as `^` (`00000nz^^^22^^^^^n^^^^^^`), `|` literal.
- The whole thing reads top-to-bottom like a marked-up text document, not a form.
  THAT is the target feel — Module 5.9 took the first step (borders gone, tighter
  text); Module 6's editor must keep it: in-place editing on those lines, never
  input boxes.

## The other four (context)

1. **008 dialog (authority)** — two columns of labeled single-char boxes with
   position numbers; OK/Cancel. (The ONE place boxes are correct — Module 7.)
2. **Search screen** — advanced form + accumulating history grid w/ hit counts.
3. **Connect to... menu** — flat base list, radio mark on current.
4. **Import Records** — convert procedure list, output files, raw record pane
   (with visible mojibake — the pain Apud's UTF-8-only pipeline removes).
