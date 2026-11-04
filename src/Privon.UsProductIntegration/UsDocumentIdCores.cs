using Privon.Detection;
using Privon.UsMbiSevis;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// The value grammars of us.passport, us.a_number and us.i94 (frozen D2.2 / D3A.1 closed lists, read-only). Field binding is
// delegated to the shared FieldBinder through UsLabelledFields; the grammar runs on the UNMODIFIED raw text, so every span is
// an exact raw span and a fullwidth digit or letter is never folded into an ASCII one.
//
// Everything marked [EA] is an experiment assumption for a branch the frozen corpus does not decide. Where a branch is
// undecided the code emits NO candidate (it never guesses a success). A scan that fails throws: it is never "no candidates".

/// <summary>us.passport (rank 100, Level 3).</summary>
internal static class UsPassportCore
{
    private const int CurrentLength = 9;
    private const int LegacyMin = 6;
    private const int LegacyMax = 12;
    private const string UsPassportLabel = "U.S. Passport";

    public static IReadOnlyList<UsDocumentMatch> Detect(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0)
        {
            return [];
        }

        var jurisdiction = UsJurisdiction.Scan(raw);
        var matches = new List<UsDocumentMatch>();

        // Approved label + shared field grammar: current format (route B) or the legacy fallback.
        foreach (var field in UsLabelledFields.Read(raw, UsRecognizedLabels.Passport, UsRecognizedLabels.All))
        {
            if (!field.BindingAccepted)
            {
                continue;
            }

            var value = raw.AsSpan(field.ValueSpan.Start, field.ValueSpan.Length);
            bool j = jurisdiction.InRecordOf(field.LabelSpan.Start)
                || UsAscii.EqualsIgnoreCase(raw.AsSpan(field.LabelSpan.Start, field.LabelSpan.Length), UsPassportLabel);

            if (IsCurrentFormat(value))
            {
                matches.Add(Match(field.ValueSpan, value, UsDocumentEvidence.Structure | UsDocumentEvidence.Context, j));
            }
            else if (IsLegacyFormat(value))
            {
                matches.Add(Match(field.ValueSpan, value, UsDocumentEvidence.Context, j));
            }
        }

        // Route A: an exact current-format token plus J in the same logical record; no label needed.
        foreach (var token in Tokens(raw))
        {
            var value = raw.AsSpan(token.Start, token.Length);
            if (IsCurrentFormat(value) && jurisdiction.InRecordOf(token.Start))
            {
                matches.Add(Match(token, value, UsDocumentEvidence.Structure, jurisdiction: true));
            }
        }

        // One candidate per span: a labelled match (added first) is stronger than the same token found by route A.
        return matches
            .GroupBy(m => m.Span)
            .Select(g => g.First())
            .OrderBy(m => m.Span.Start).ThenBy(m => m.Span.Length)
            .ToList()
            .AsReadOnly();
    }

    // ownerClaim=true exactly when J is present: without J the span is only conservative cross-jurisdiction protection.
    private static UsDocumentMatch Match(RawSpan span, ReadOnlySpan<char> value, UsDocumentEvidence evidence, bool jurisdiction) =>
        new(
            span,
            jurisdiction ? evidence | UsDocumentEvidence.Jurisdiction : evidence,
            UsAscii.Upper(value),
            jurisdiction ? OwnerClaim.True : OwnerClaim.False);

    /// <summary>Exactly one ASCII letter followed by eight ASCII digits.</summary>
    private static bool IsCurrentFormat(ReadOnlySpan<char> value)
    {
        if (value.Length != CurrentLength || !UsAscii.IsLetter(value[0]))
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (!UsAscii.IsDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One 6-12 ASCII-alphanumeric token containing at least one digit.</summary>
    private static bool IsLegacyFormat(ReadOnlySpan<char> value)
    {
        if (value.Length < LegacyMin || value.Length > LegacyMax)
        {
            return false;
        }

        bool digit = false;
        foreach (char c in value)
        {
            if (!UsAscii.IsAlphanumeric(c))
            {
                return false;
            }

            digit |= UsAscii.IsDigit(c);
        }

        return digit;
    }

    // A token is a maximal run of letters and digits (ASCII or not): exactly the "exact token boundary" the contract requires,
    // and a non-ASCII neighbour counts as part of the token.
    internal static IEnumerable<RawSpan> Tokens(string raw)
    {
        int i = 0;
        while (i < raw.Length)
        {
            if (!char.IsLetterOrDigit(raw[i]))
            {
                i++;
                continue;
            }

            int end = i + 1;
            while (end < raw.Length && char.IsLetterOrDigit(raw[end]))
            {
                end++;
            }

            yield return new RawSpan(i, end - i);
            i = end;
        }
    }
}

/// <summary>us.a_number (rank 110, Level 3).</summary>
internal static class UsANumberCore
{
    private const int MinDigits = 7;
    private const int MaxDigits = 9;

    public static IReadOnlyList<UsDocumentMatch> Detect(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0)
        {
            return [];
        }

        var matches = new List<UsDocumentMatch>();

        // Labelled only: an unlabelled A-Number is intentionally out of 0.4.0.
        foreach (var field in UsLabelledFields.Read(raw, UsRecognizedLabels.ANumber, UsRecognizedLabels.All))
        {
            if (field.BindingAccepted
                && TryNormalize(raw.AsSpan(field.ValueSpan.Start, field.ValueSpan.Length), out string canonical))
            {
                matches.Add(new UsDocumentMatch(
                    field.ValueSpan, UsDocumentEvidence.Structure | UsDocumentEvidence.Context, canonical, OwnerClaim.NotApplicable));
            }
        }

        return matches
            .GroupBy(m => m.Span)
            .Select(g => g.First())
            .OrderBy(m => m.Span.Start)
            .ToList()
            .AsReadOnly();
    }

    // [A] ASCII digits{7,9}; the 'A' is optional and, when present, is followed by at most ONE ASCII space or hyphen (which
    // normalises away). [EA] the 'A' may be lower-case, and the canonical value always carries the 'A', so A-1234567, A 1234567
    // and a bare 1234567 under the label share one alias. A six-digit value is an intended miss.
    private static bool TryNormalize(ReadOnlySpan<char> value, out string canonical)
    {
        canonical = string.Empty;
        int i = 0;
        if (value.Length > 0 && (value[0] == 'A' || value[0] == 'a'))
        {
            i = 1;
            if (i < value.Length && (value[i] == ' ' || value[i] == '-'))
            {
                i++;
            }
        }

        var digits = value[i..];
        if (digits.Length < MinDigits || digits.Length > MaxDigits)
        {
            return false;
        }

        foreach (char c in digits)
        {
            if (!UsAscii.IsDigit(c))
            {
                return false;
            }
        }

        canonical = "A" + digits.ToString();
        return true;
    }
}

/// <summary>us.i94 (rank 120, Level 3).</summary>
internal static class UsI94Core
{
    private const int Length = 11;

    public static IReadOnlyList<UsDocumentMatch> Detect(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0)
        {
            return [];
        }

        var matches = new List<UsDocumentMatch>();

        // Only the approved I-94 labels bind a value; a generic "Admission Number" is not a label here.
        foreach (var field in UsLabelledFields.Read(raw, UsRecognizedLabels.I94, UsRecognizedLabels.All))
        {
            if (!field.BindingAccepted)
            {
                continue;
            }

            var value = raw.AsSpan(field.ValueSpan.Start, field.ValueSpan.Length);
            if (IsAsciiAlphanumeric11(value))
            {
                // [EA] ASCII letters are case-normalised, as for the MBI.
                matches.Add(new UsDocumentMatch(
                    field.ValueSpan, UsDocumentEvidence.Structure | UsDocumentEvidence.Context, UsAscii.Upper(value), OwnerClaim.NotApplicable));
            }
        }

        return matches
            .GroupBy(m => m.Span)
            .Select(g => g.First())
            .OrderBy(m => m.Span.Start)
            .ToList()
            .AsReadOnly();
    }

    private static bool IsAsciiAlphanumeric11(ReadOnlySpan<char> value)
    {
        if (value.Length != Length)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!UsAscii.IsAlphanumeric(c))
            {
                return false;
            }
        }

        return true;
    }
}
