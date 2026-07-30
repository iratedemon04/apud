namespace Apud.App;

/// <summary>
/// Tag → display name for the viewer's field-name column, Aleph-fashion
/// (docs/ALEPH-WORKFLOW.md). English only in this version; localization is a
/// v2/v3 concern. Unknown tags display with an empty name — the tag itself is
/// always visible in its own column, so nothing is ever hidden.
/// </summary>
public static class TagNames
{
    public static string For(string tag)
    {
        if (Names.TryGetValue(tag, out var n)) return n;
        // 9XX block is reserved for local use — name them as such rather than blank.
        if (tag.Length == 3 && tag[0] == '9') return "Local";
        return "";
    }

    private static readonly Dictionary<string, string> Names = new()
    {
        ["LDR"] = "Leader",
        ["001"] = "Control No.",
        ["003"] = "Control No. Id",
        ["005"] = "Date and Time",
        ["006"] = "Fixed Data--Add'l",
        ["007"] = "Physical Desc.",
        ["008"] = "Fixed Data",
        ["010"] = "LC Control No.",
        ["020"] = "ISBN",
        ["022"] = "ISSN",
        ["035"] = "System Control No.",
        ["040"] = "Catalog. Source",
        ["041"] = "Language Code",
        ["043"] = "Geographic Area",
        ["050"] = "LC Call No.",
        ["082"] = "Dewey Class No.",
        ["083"] = "Dewey Class No.",
        ["084"] = "Other Class No.",
        ["090"] = "Local Call No.",
        ["100"] = "Main Entry--Pers.",
        ["110"] = "Main Entry--Corp.",
        ["111"] = "Main Entry--Meet.",
        ["130"] = "Main Entry--Unif.Title",
        ["150"] = "Topical Term",
        ["151"] = "Geographic Name",
        ["240"] = "Uniform Title",
        ["245"] = "Title Statement",
        ["246"] = "Varying Title",
        ["250"] = "Edition",
        ["260"] = "Publication",
        ["264"] = "Publication",
        ["300"] = "Physical Desc.",
        ["336"] = "Content Type",
        ["337"] = "Media Type",
        ["338"] = "Carrier Type",
        ["400"] = "See From--Pers.",
        ["410"] = "See From--Corp.",
        ["411"] = "See From--Meet.",
        ["430"] = "See From--Unif.Title",
        ["450"] = "See From--Topic",
        ["451"] = "See From--Geog.",
        ["490"] = "Series Statement",
        ["500"] = "General Note",
        ["504"] = "Bibliography Note",
        ["505"] = "Contents Note",
        ["520"] = "Summary",
        ["546"] = "Language Note",
        ["590"] = "Local Note",
        ["600"] = "Subject--Pers.",
        ["610"] = "Subject--Corp.",
        ["611"] = "Subject--Meet.",
        ["630"] = "Subject--Unif.Title",
        ["650"] = "Subject--Topical",
        ["651"] = "Subject--Geog.",
        ["655"] = "Genre/Form",
        ["670"] = "Source Found",
        ["675"] = "Source Not Found",
        ["700"] = "Added Entry--Pers.",
        ["710"] = "Added Entry--Corp.",
        ["711"] = "Added Entry--Meet.",
        ["730"] = "Added Entry--Unif.Title",
        ["740"] = "Added Entry--Title",
        ["773"] = "Host Item",
        ["776"] = "Other Form Entry",
        ["830"] = "Series--Unif.Title",
        ["852"] = "Location",
        ["856"] = "Electronic Access",
    };
}
