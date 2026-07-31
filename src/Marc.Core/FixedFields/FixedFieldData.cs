namespace Marc.Core.FixedFields;

/// <summary>
/// A fixed field's characters, editable by meaning through its layout. The value
/// is always exactly the layout's length: shorter input is padded with spaces,
/// longer input is truncated (008 is 40 by spec; a malformed short one is fixed
/// on the way through). Blanks are spaces here — the dialog shows them as empty
/// boxes and this class turns empty boxes back into spaces.
/// </summary>
public sealed class FixedFieldData
{
    public FixedFieldLayout Layout { get; }
    private readonly char[] _chars;

    public FixedFieldData(FixedFieldLayout layout, string? current)
    {
        Layout = layout;
        _chars = new char[layout.Length];
        Array.Fill(_chars, ' ');
        if (!string.IsNullOrEmpty(current))
        {
            int n = Math.Min(current.Length, layout.Length);
            for (int i = 0; i < n; i++) _chars[i] = current[i];
        }
    }

    /// <summary>The characters currently at a position (always <c>p.Len</c> long).</summary>
    public string Slice(FixedFieldPosition p)
    {
        var chars = new char[p.Len];
        for (int i = 0; i < p.Len; i++)
        {
            int idx = p.Off + i;
            chars[i] = idx < _chars.Length ? _chars[idx] : ' ';
        }
        return new string(chars);
    }

    /// <summary>Writes text into a position, left-justified: extra characters are
    /// dropped, a short value is space-filled to the position width.</summary>
    public void Set(FixedFieldPosition p, string? text)
    {
        text ??= "";
        for (int i = 0; i < p.Len; i++)
        {
            int idx = p.Off + i;
            if (idx >= _chars.Length) break;
            _chars[idx] = i < text.Length ? text[i] : ' ';
        }
    }

    /// <summary>The assembled fixed-field string, exactly <see cref="FixedFieldLayout.Length"/> long.</summary>
    public override string ToString() => new(_chars);
}
