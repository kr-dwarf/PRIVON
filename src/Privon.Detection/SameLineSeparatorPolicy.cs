namespace Privon.Detection;

/// <summary>D3B0-A A.9 §21.3 -- closed same-line separator policy for LogicalRecordRule.</summary>
public enum SameLineSeparatorPolicy
{
    Disallowed = 0,
    AllowSpacesCommaSemicolonTabPipe = 1,
}
