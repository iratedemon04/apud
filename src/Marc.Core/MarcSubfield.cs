namespace Marc.Core;

/// <summary>One subfield of a MARC data field: a one-character code and its value.</summary>
public sealed class MarcSubfield
{
    public char Code { get; set; }
    public string Value { get; set; }

    public MarcSubfield(char code, string value)
    {
        Code = code;
        Value = value;
    }
}
