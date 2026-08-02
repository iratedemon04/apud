using System.Text.Json;

namespace Apud.App;

/// <summary>
/// Offline MARC21 help for the field at the caret (Module 10, F1). One paragraph
/// per tag — what the field is for and its common subfields — so a cataloguer
/// never has to leave Apud to remember a tag. English only in this version.
///
/// User-editable via <c>taghelp.json</c> next to the exe, the same config contract
/// as tagnames.json / keymap.json / profile-*.json: a missing file or missing entry
/// falls back to the built-in text; a bad entry is skipped and reported; a broken
/// file is reported and the built-ins stand; it never crashes. A tag with no help
/// (built-in or override) falls back to its <see cref="TagNames"/> name, so F1
/// always says something.
/// </summary>
public static class FieldHelp
{
    public const string FileName = "taghelp.json";

    private static Dictionary<string, string> _overrides = new();

    /// <summary>Help text for a tag: the override if present, else the built-in
    /// paragraph, else a one-line fallback naming the field. Never empty.</summary>
    public static string For(string tag)
    {
        if (_overrides.TryGetValue(tag, out var o)) return o;
        if (Defaults.TryGetValue(tag, out var d)) return d;

        string name = TagNames.For(tag);
        if (tag.Length == 3 && tag[0] == '9')
            return $"Field {tag} — reserved for local use. Institutions define {tag} for their own purposes; MARC21 assigns it no standard meaning.";
        return name.Length > 0
            ? $"Field {tag} — {name}. No detailed help is shipped for this tag; see the MARC21 documentation, or add an entry to {FileName}."
            : $"Field {tag}. No help is shipped for this tag; see the MARC21 documentation, or add an entry to {FileName}.";
    }

    /// <summary>Loads overrides from taghelp.json. Returns null when all is well (a
    /// missing file is well — built-ins apply), else a one-line report for the
    /// message bar.</summary>
    public static string? LoadFile(string path)
    {
        if (!File.Exists(path)) { _overrides = new(); return null; }
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex)
        {
            _overrides = new();
            return $"{FileName} not read ({ex.Message}) — using built-in field help.";
        }
        return ApplyJson(json);
    }

    /// <summary>Parses and applies override entries; file I/O split off so tests run
    /// headless. Returns null or a one-line report.</summary>
    internal static string? ApplyJson(string json)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        try
        {
            using var doc = JsonDocument.Parse(json, options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _overrides = new();
                return $"{FileName} ignored (expected an object of \"tag\": \"help\" pairs) — using built-in field help.";
            }

            var map = new Dictionary<string, string>();
            var skipped = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString()!;
                else
                    skipped.Add(prop.Name);
            }
            _overrides = map;
            return skipped.Count == 0
                ? null
                : $"{FileName}: skipped non-text entr{(skipped.Count == 1 ? "y" : "ies")} {string.Join(", ", skipped)} — built-in help used for those.";
        }
        catch (JsonException ex)
        {
            _overrides = new();
            return $"{FileName} ignored (line {ex.LineNumber + 1}: not valid JSON) — using built-in field help.";
        }
    }

    // Built-in paragraphs for the tags a book/serial cataloguer meets most often.
    // Extend or reword freely via taghelp.json; this dictionary is only the default.
    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["LDR"] = "Leader — 24 fixed positions describing the record itself: record status (05), type of record (06) and bibliographic level (07), which together pick the 008 layout, plus the encoding level (17). Apud recomputes the length and base-address positions on push; you set the coded meanings. Edit it position-by-position with Ctrl+F3.",
        ["001"] = "Control Number — the record's own number in this system (its accession/001). Apud fills it on push only when empty, as the current highest number in the base plus one; a number you type is kept forever and never renumbered. Not repeatable.",
        ["003"] = "Control Number Identifier — the MARC organization code of the agency in field 001 (e.g. MX-MxBAC). If you use it, put it in your template; Apud does not write it for you.",
        ["005"] = "Date and Time of Latest Transaction — yyyymmddhhmmss.f. Apud stamps this automatically on every push; you never type it.",
        ["006"] = "Fixed-Length Data — Additional Material Characteristics — a second fixed field for aspects a single 008 cannot capture (e.g. a book that is also a serial). Its first byte picks the layout, mirroring the 008.",
        ["007"] = "Physical Description Fixed Field — coded physical form (its first byte is the category of material: maps, sound recordings, electronic resources...). Position-coded like the 008.",
        ["008"] = "Fixed-Length Data Elements — 40 coded positions: date entered, type/date of publication (06–14), place (15–17), language (35–37) and material-specific codes whose layout comes from Leader/06–07. Edit it position-by-position with Ctrl+F3. Note that Apud shows the transcribed 260/264 $c as the display Year, not this coded date.",
        ["020"] = "International Standard Book Number. $a ISBN, $q qualifier (e.g. hardback), $c terms of availability, $z a cancelled/invalid ISBN. Repeatable.",
        ["022"] = "International Standard Serial Number. $a valid ISSN, $l linking ISSN, $y incorrect, $z cancelled. Repeatable.",
        ["040"] = "Cataloging Source — who made and edited the record. $a original cataloguing agency, $b language of cataloguing, $c transcribing agency, $d modifying agency, $e description convention (e.g. rda). Institution data — supply it from your template.",
        ["041"] = "Language Code — used when the item is multilingual or translated. $a language of the text, $h language of the original. Codes are the three-letter MARC language codes; coordinate with 008/35–37.",
        ["082"] = "Dewey Decimal Classification Number. $a the class number, $2 the edition of Dewey used. Ind1 gives the edition type (0 full, 1 abridged).",
        ["084"] = "Other Classification Number (a scheme named in $2, e.g. a local or national table). $a number, $b item/cutter, $2 scheme.",
        ["100"] = "Main Entry — Personal Name — the person chiefly responsible (author). $a name (surname, forename), $d dates, $e relator term, $4 relator code. Ind1: 0 forename, 1 surname. A controlled heading — Ctrl+F4 browses and links it to the authority base. Not repeatable (one main entry).",
        ["110"] = "Main Entry — Corporate Name. $a corporate/jurisdiction name, $b subordinate unit. A controlled heading (Ctrl+F4). Not repeatable.",
        ["111"] = "Main Entry — Meeting Name (a named conference/congress as author). $a meeting name, $n number, $d date, $c place. Controlled (Ctrl+F4). Not repeatable.",
        ["130"] = "Main Entry — Uniform Title (when the work is entered under a title, no personal/corporate author). Controlled (Ctrl+F4). Not repeatable.",
        ["240"] = "Uniform Title — the standardized title of a work that appears under a 1XX author main entry (e.g. the original title of a translation). Controlled.",
        ["245"] = "Title Statement — the transcribed title. $a title proper, $b remainder/subtitle, $c statement of responsibility, $n/$p part number/name. Ind1: 1 when a 1XX author is present. Ind2: number of leading characters to ignore in filing (e.g. 4 for \"The \"). Mandatory and not repeatable.",
        ["246"] = "Varying Form of Title — cover title, spine title, parallel title, earlier title. $a the varying title, $i display text. Repeatable.",
        ["250"] = "Edition Statement. $a edition (\"2nd ed.\", \"Ed. rev.\"). Not repeatable per manifestation.",
        ["260"] = "Publication, Distribution, etc. (Imprint) — AACR2 style. $a place, $b publisher, $c date. Ind1 for sequence. The transcribed $c date is Apud's display Year. See 264 for the RDA equivalent.",
        ["264"] = "Production, Publication, Distribution, Manufacture, Copyright (RDA). $a place, $b publisher/producer, $c date. Ind2 names the function: 0 production, 1 publication, 2 distribution, 3 manufacture, 4 copyright. The $c date is Apud's display Year.",
        ["300"] = "Physical Description. $a extent (pages/volumes), $b other details (illustrations), $c dimensions, $e accompanying material. Repeatable for multipart items.",
        ["336"] = "Content Type (RDA) — e.g. \"text\", with $b code (txt) and $2 rdacontent. Pairs with 337/338.",
        ["337"] = "Media Type (RDA) — e.g. \"unmediated\" ($b n, $2 rdamedia).",
        ["338"] = "Carrier Type (RDA) — e.g. \"volume\" ($b nc, $2 rdacarrier).",
        ["490"] = "Series Statement — the series exactly as it appears on the item. $a series title, $v volume/number. Ind1: 1 when the series is also traced in an 8XX. Not a controlled field itself; trace the series in 830.",
        ["500"] = "General Note — free-text note that fits no more specific 5XX. $a the note. Repeatable, and Apud keeps repeated notes in the order you write them.",
        ["504"] = "Bibliography, etc. Note — \"Includes bibliographical references (pages ...).\" $a the note.",
        ["505"] = "Formatted Contents Note — a table of contents. $a the contents; with Ind1/Ind2 set for enhanced form, $t title and $r responsibility repeat per part.",
        ["520"] = "Summary, etc. — abstract, annotation or scope note. $a the summary.",
        ["546"] = "Language Note — a free-text statement about the language of the item (\"Text in Spanish and English\").",
        ["600"] = "Subject Added Entry — Personal Name — a person as subject. Same subfields as 100, plus subject subdivisions $v form, $x general, $y chronological, $z geographic. Ind2 names the thesaurus (0 LCSH, 7 with $2). Controlled (Ctrl+F4). Repeatable.",
        ["610"] = "Subject Added Entry — Corporate Name. Controlled (Ctrl+F4). Repeatable.",
        ["611"] = "Subject Added Entry — Meeting Name. Controlled (Ctrl+F4). Repeatable.",
        ["630"] = "Subject Added Entry — Uniform Title (a work as subject). Controlled. Repeatable.",
        ["650"] = "Subject Added Entry — Topical Term — the workhorse subject heading. $a topic, $v form, $x general subdivision, $y period, $z place. Ind2 names the thesaurus (0 LCSH; 7 with $2 for another, e.g. embne). Controlled (Ctrl+F4). Repeatable — order carries meaning, so Apud never reshuffles your subjects.",
        ["651"] = "Subject Added Entry — Geographic Name — a place as subject. $a place, plus $v/$x/$y/$z subdivisions. Controlled (Ctrl+F4). Repeatable.",
        ["655"] = "Index Term — Genre/Form — what the item IS rather than what it is about (\"Diccionarios\", \"Novela\"). $a term, $2 source vocabulary. Repeatable.",
        ["700"] = "Added Entry — Personal Name — an additional author, editor, translator, illustrator. Same subfields as 100; $e/$4 relator states the role. Controlled (Ctrl+F4). Repeatable.",
        ["710"] = "Added Entry — Corporate Name (a body with a share of responsibility, or a publisher traced). Controlled (Ctrl+F4). Repeatable.",
        ["711"] = "Added Entry — Meeting Name. Controlled (Ctrl+F4). Repeatable.",
        ["730"] = "Added Entry — Uniform Title (a related work traced by title). Controlled. Repeatable.",
        ["740"] = "Added Entry — Uncontrolled Related/Analytical Title — a title traced without an authority. Repeatable.",
        ["830"] = "Series Added Entry — Uniform Title — the traced, controlled form of the series in 490. $a series title, $v volume. Controlled (Ctrl+F4). This is what makes a series findable regardless of how 490 was transcribed.",
        ["856"] = "Electronic Location and Access. $u the URI, $y link text, $z a public note. Ind1/Ind2 describe the access method and relationship. Repeatable.",
        // ----- Authority base (AUT) -----
        ["148"] = "Authority Heading — Chronological Term. The established form of a period heading in the authority record.",
        ["150"] = "Authority Heading — Topical Term — the established (authorized) form of a subject heading. This 1XX is the text Ctrl+F4 writes into a bib 650 and that ripples to every linked bib when you change it. One per authority record.",
        ["151"] = "Authority Heading — Geographic Name — the authorized form of a place. Ripples to linked 651s on change. One per record.",
        ["155"] = "Authority Heading — Genre/Form Term — the authorized form for 655.",
        ["400"] = "See From Tracing — Personal Name — a variant/unauthorized form of the 1XX heading (a pseudonym, a differently spelled name). In browse it shows as \"variant → see: authorized form\" and re-points to the 1XX. Repeatable.",
        ["410"] = "See From Tracing — Corporate Name — a variant form of a 110 authority heading. Repeatable.",
        ["411"] = "See From Tracing — Meeting Name — a variant form of a 111. Repeatable.",
        ["430"] = "See From Tracing — Uniform Title — a variant of a 130. Repeatable.",
        ["450"] = "See From Tracing — Topical Term — a variant form of the 150 subject (a synonym, an older spelling). Enter used-for references here. Repeatable.",
        ["451"] = "See From Tracing — Geographic Name — a variant form of the 151. Repeatable.",
        ["550"] = "See Also From Tracing — Topical Term — a link to a RELATED authorized heading (broader/narrower/related term), not a variant. Both headings stay valid. Repeatable.",
        ["670"] = "Source Data Found — where the cataloguer verified the heading (\"His El llano en llamas, 1953: t.p. (Juan Rulfo)\"). $a the source, $b the information found. The justification trail behind an authority record. Repeatable.",
        ["675"] = "Source Data Not Found — sources consulted that did NOT yield the heading. Repeatable.",
    };
}
