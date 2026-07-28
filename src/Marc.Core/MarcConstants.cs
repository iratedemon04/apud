namespace Marc.Core;

/// <summary>MARC21 structural constants (ISO 2709). The record model arrives in Module 2.</summary>
public static class MarcConstants
{
    public const int LeaderLength = 24;

    public const char FieldTerminator   = '\u001e'; // RS
    public const char SubfieldDelimiter = '\u001f'; // US
    public const char RecordTerminator  = '\u001d'; // GS
}