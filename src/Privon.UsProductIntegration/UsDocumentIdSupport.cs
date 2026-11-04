using Privon.Core;
using Privon.Detection;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// Shared support for the passport, A-Number and I-94 detectors: the closed label catalog (D2.2 / D3A.1 "Closed lexical/context
// lists"), the match record, and the U.S. jurisdiction evidence (J). Field binding itself (label, separators, terminators,
// 96-raw-character limit, next-label termination) is the shared, grammar-free FieldBinder; nothing here re-implements it.

/// <summary>Frozen oracle evidence letters: S = structure, C = context (a strong label), J = explicit U.S. jurisdiction.</summary>
[Flags]
internal enum UsDocumentEvidence
{
    None = 0,
    Structure = 1,
    Context = 2,
    Jurisdiction = 4,

    /// <summary>The frozen "P": a separately validated paired field. It shares the J/P slot of the evidence order.</summary>
    Paired = 4,
}

/// <summary>One detected value. Sensitive: <see cref="ToString"/> prints nothing derived from the value.</summary>
internal sealed record UsDocumentMatch(
    RawSpan Span, UsDocumentEvidence Evidence, string Canonical, OwnerClaim OwnerClaim,
    RiskLevel Level = RiskLevel.Level3, DetectionConfidence Confidence = DetectionConfidence.High)
{
    public override string ToString() =>
        $"{nameof(UsDocumentMatch)} {{ Evidence = {Evidence}, OwnerClaim = {OwnerClaim} }}";
}

/// <summary>The closed label lists. A label is recognised only if it is listed here; nothing is inferred.</summary>
internal static class UsRecognizedLabels
{
    public static IReadOnlyList<string> Passport { get; } = Array.AsReadOnly(
        ["Passport", "Passport Number", "Passport No.", "Passport #", "U.S. Passport"]);

    public static IReadOnlyList<string> ANumber { get; } = Array.AsReadOnly(
        ["A-Number", "A Number", "Alien Registration Number", "USCIS Number"]);

    public static IReadOnlyList<string> I94 { get; } = Array.AsReadOnly(
        ["I-94", "I94", "I-94 Number", "I94 Number", "I-94 Admission Number", "Form I-94 Number"]);

    public static IReadOnlyList<string> RoutingNumber { get; } = Array.AsReadOnly(
        ["Routing Number", "ABA Routing Number", "ABA Number", "Routing Transit Number"]);

    /// <summary>The bank-specific account labels: each may establish the account side of a tuple on its own.</summary>
    public static IReadOnlyList<string> BankAccountSpecific { get; } = Array.AsReadOnly(
        ["Bank Account", "Bank Account Number", "Checking Account", "Checking Account Number", "Savings Account",
         "Savings Account Number"]);

    /// <summary>The generic account label: accepted only when paired with a separately validated routing field.</summary>
    public const string GenericAccountLabel = "Account Number";

    /// <summary>The driver license / state ID labels that need no companion.</summary>
    public static IReadOnlyList<string> DriverStateId { get; } = Array.AsReadOnly(
        ["Driver License", "Driver's License", "Driver License Number", "Driver's License Number", "DL Number", "DLN", "State ID",
         "State ID Number"]);

    /// <summary>Conditional: accepted only with an explicit companion <c>Document Type</c> field and J.</summary>
    public const string CustomerIdLabel = "Customer ID Number";

    public const string DocumentTypeLabel = "Document Type";

    /// <summary>Basis: the frozen D3A row US-BANK-3FIELD names it as the recognised nonfinancial field between a pair.</summary>
    public const string EmailLabel = "Email";

    /// <summary>The fields whose bound abbreviation counts as jurisdiction evidence.</summary>
    public static IReadOnlyList<string> Jurisdiction { get; } = Array.AsReadOnly(["State", "Issued by", "Jurisdiction"]);

    public static IReadOnlyList<string> Phone { get; } = Array.AsReadOnly(
    [
        "Phone", "Phone Number", "Telephone", "Telephone Number", "Tel", "Mobile", "Mobile Number", "Cell", "Cell Phone",
        "Cell Phone Number",
    ]);

    public static IReadOnlyList<string> Address { get; } = Array.AsReadOnly(
        ["Address", "Home Address", "Mailing Address", "Residential Address", "Street Address"]);

    /// <summary>The specific name labels: Level 2.</summary>
    public static IReadOnlyList<string> NameSpecific { get; } = Array.AsReadOnly(
        ["Full Name", "First Name", "Given Name", "Last Name", "Family Name", "Surname"]);

    /// <summary>The generic name label: Level 1.</summary>
    public const string NameGeneric = "Name";

    /// <summary>
    /// Every label that only ENDS another field's value: the other U.S. detectors' strong labels and the confirmed non-U.S.-ID
    /// labels. The own strong labels are included too (FieldBinder de-duplicates), so two fields on one line never swallow
    /// each other.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = Build();

    private static IReadOnlyList<string> Build()
    {
        var all = new List<string>();
        var tax = Privon.UsSsn.UsTaxpayerLabelSet.Default;
        all.AddRange(tax.Ssn);
        all.AddRange(tax.Itin);
        all.AddRange(tax.Atin);
        all.AddRange(tax.Others);
        all.AddRange(Privon.UsMbiSevis.UsMbiScanner.DefaultStrongLabels);
        all.AddRange(Privon.UsMbiSevis.UsSevisScanner.DefaultStrongLabels);
        all.AddRange(Passport);
        all.AddRange(ANumber);
        all.AddRange(I94);
        all.AddRange(Jurisdiction);
        all.AddRange(Phone);
        all.AddRange(Address);
        all.AddRange(NameSpecific);
        all.Add(NameGeneric);
        all.AddRange(DriverStateId);
        all.Add(CustomerIdLabel);
        all.Add(DocumentTypeLabel);
        all.AddRange(RoutingNumber);
        all.AddRange(BankAccountSpecific);
        all.Add(GenericAccountLabel);
        all.Add(EmailLabel);
        return Array.AsReadOnly(all.Distinct(StringComparer.Ordinal).ToArray());
    }
}

internal static class UsAscii
{
    public static bool IsDigit(char c) => c >= '0' && c <= '9';

    public static bool IsLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    public static bool IsAlphanumeric(char c) => IsDigit(c) || IsLetter(c);

    // ASCII-only folding: the dotless-i / Kelvin-sign problem must not be reachable from a grammar rule.
    public static char Upper(char c) => c >= 'a' && c <= 'z' ? (char)(c - 32) : c;

    public static string Upper(ReadOnlySpan<char> value)
    {
        var chars = new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            chars[i] = Upper(value[i]);
        }

        return new string(chars);
    }

    public static bool EqualsIgnoreCase(ReadOnlySpan<char> a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (Upper(a[i]) != Upper(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A boundary is any letter or digit, ASCII or not: a lookalike touching a token makes it a different token.</summary>
    public static bool IsBoundaryAlphanumeric(char c) => char.IsLetterOrDigit(c);
}

/// <summary>
/// D2.1 evidence vocabulary, J: <c>United States</c>, <c>USA</c> or <c>U.S.</c>; a full U.S. state / territory name; or a USPS
/// state / territory abbreviation BOUND by <c>State</c>, <c>Issued by</c> or <c>Jurisdiction</c> -- all in the same logical record
/// (one physical line). A bare two-letter token is never enough.
///
/// [EA] experiment assumptions for what the contract leaves open: "USA" and "U.S." are matched case-sensitively; a state or
/// territory name is matched ASCII-case-insensitively on word boundaries; the abbreviation must be upper-case; only the three
/// listed labels bind an abbreviation ("or equivalent label" is NOT guessed -- an open question).
/// </summary>
internal sealed class UsJurisdiction
{
    private static readonly string[] Names =
    [
        "United States", "Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut", "Delaware",
        "Florida", "Georgia", "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa", "Kansas", "Kentucky", "Louisiana", "Maine",
        "Maryland", "Massachusetts", "Michigan", "Minnesota", "Mississippi", "Missouri", "Montana", "Nebraska", "Nevada",
        "New Hampshire", "New Jersey", "New Mexico", "New York", "North Carolina", "North Dakota", "Ohio", "Oklahoma", "Oregon",
        "Pennsylvania", "Rhode Island", "South Carolina", "South Dakota", "Tennessee", "Texas", "Utah", "Vermont", "Virginia",
        "Washington", "West Virginia", "Wisconsin", "Wyoming", "District of Columbia", "Puerto Rico", "Guam", "American Samoa",
        "Virgin Islands", "Northern Mariana Islands",
    ];

    // USPS state / possession abbreviations (50 states, DC, PR, VI, GU, AS, MP).
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
        "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC", "PR", "VI", "GU", "AS", "MP",
    };

    private readonly IReadOnlyList<PhysicalLine> _lines;
    private readonly bool[] _hasJurisdiction;

    private UsJurisdiction(IReadOnlyList<PhysicalLine> lines, bool[] hasJurisdiction)
    {
        _lines = lines;
        _hasJurisdiction = hasJurisdiction;
    }

    public static UsJurisdiction Scan(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var view = NormalizedView.Build(raw);
        var lines = PhysicalLineIndex.Build(view);
        var has = new bool[lines.Count];

        for (int i = 0; i < lines.Count; i++)
        {
            has[i] = LineNamesJurisdiction(raw, lines[i].Span);
        }

        MarkBoundAbbreviations(view, lines, has);
        return new UsJurisdiction(lines, has);
    }

    /// <summary>True when the logical record (physical line) holding <paramref name="rawOffset"/> carries J.</summary>
    public bool InRecordOf(int rawOffset)
    {
        int index = PhysicalLineIndex.LineIndexOf(_lines, rawOffset);
        return index >= 0 && _hasJurisdiction[index];
    }

    private static bool LineNamesJurisdiction(string raw, RawSpan line)
    {
        var text = raw.AsSpan(line.Start, line.Length);

        foreach (var name in Names)
        {
            if (ContainsWord(text, name, ignoreCase: true, requireTrailingBoundary: true))
            {
                return true;
            }
        }

        return ContainsWord(text, "USA", ignoreCase: false, requireTrailingBoundary: true)
            || ContainsWord(text, "U.S.", ignoreCase: false, requireTrailingBoundary: false);
    }

    private static bool ContainsWord(ReadOnlySpan<char> text, string word, bool ignoreCase, bool requireTrailingBoundary)
    {
        for (int at = 0; at + word.Length <= text.Length; at++)
        {
            if (at > 0 && UsAscii.IsBoundaryAlphanumeric(text[at - 1]))
            {
                continue;
            }

            bool match = true;
            for (int k = 0; k < word.Length && match; k++)
            {
                char a = text[at + k];
                char b = word[k];
                match = ignoreCase ? UsAscii.Upper(a) == UsAscii.Upper(b) : a == b;
            }

            if (!match)
            {
                continue;
            }

            int end = at + word.Length;
            if (requireTrailingBoundary && end < text.Length && UsAscii.IsBoundaryAlphanumeric(text[end]))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    // The abbreviation is the FIRST token of the bound candidate ("State: NY P12345678" binds NY). The shared limit is "label
    // start through candidate end", so a long line after the abbreviation must not undo the binding.
    private static void MarkBoundAbbreviations(NormalizedView view, IReadOnlyList<PhysicalLine> lines, bool[] has)
    {
        string raw = view.RawText;
        var labels = new List<FieldLabel>();
        foreach (var text in UsRecognizedLabels.All)
        {
            if (FieldLabel.TryCreate(text, out var label))
            {
                labels.Add(label);
            }
        }

        foreach (var binding in FieldBinder.Bind(view, labels))
        {
            if (!IsJurisdictionLabel(binding.Label.Value)
                || binding.State is not (FieldBindingState.FieldAccepted or FieldBindingState.FieldRejectedOverMaxLength))
            {
                continue;
            }

            var labelSpan = binding.LabelSpan;
            if ((labelSpan.Start > 0 && UsAscii.IsAlphanumeric(raw[labelSpan.Start - 1]))
                || (labelSpan.End < raw.Length && UsAscii.IsAlphanumeric(raw[labelSpan.End])))
            {
                continue; // the label sits inside a word
            }

            int start = binding.CandidateSpan.Start;
            int end = start + 2;
            if (end > binding.CandidateSpan.End
                || end - labelSpan.Start > FieldBinder.MaxLogicalFieldRawLength
                || !Abbreviations.Contains(raw.Substring(start, 2))
                || (end < raw.Length && UsAscii.IsBoundaryAlphanumeric(raw[end])))
            {
                continue;
            }

            int line = PhysicalLineIndex.LineIndexOf(lines, labelSpan.Start);
            if (line >= 0)
            {
                has[line] = true;
            }
        }
    }

    private static bool IsJurisdictionLabel(string label)
    {
        foreach (var j in UsRecognizedLabels.Jurisdiction)
        {
            if (string.Equals(j, label, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
