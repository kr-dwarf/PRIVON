using Privon.Detection;
using Privon.UsMbiSevis;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// The value grammars of us.driver_state_id and us.bank_account (frozen D2.2 / D3A.1 closed lists, read-only). Field binding is
// the shared FieldBinder (unchanged); like the other cores, this runs on the UNMODIFIED raw text so every span is an exact raw
// span and no fullwidth character is ever folded. Where D2.2 leaves a branch open the code emits NO candidate and the branch is
// marked [EA] / left to the report; a scan that fails throws and is never "no candidates".

/// <summary>us.driver_state_id (rank 130, Level 3, evidence S+C+J).</summary>
internal static class UsDriverStateIdCore
{
    private const int MinLength = 4;
    private const int MaxLength = 25;

    private static readonly string[] CompanionValues = ["Driver License", "State ID"];

    public static IReadOnlyList<UsDocumentMatch> Detect(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0)
        {
            return [];
        }

        var jurisdiction = UsJurisdiction.Scan(raw);
        var view = NormalizedView.Build(raw);
        var lines = PhysicalLineIndex.Build(view);
        var strong = new List<string>(UsRecognizedLabels.DriverStateId) { UsRecognizedLabels.CustomerIdLabel };
        var matches = new List<UsDocumentMatch>();

        foreach (var field in UsLabelledFields.Read(raw, strong, UsRecognizedLabels.All))
        {
            // No J, no U.S. candidate: a bare state abbreviation is never J (UsJurisdiction requires a bound label).
            if (!field.BindingAccepted || !jurisdiction.InRecordOf(field.LabelSpan.Start))
            {
                continue;
            }

            bool customerId = UsAscii.EqualsIgnoreCase(
                raw.AsSpan(field.LabelSpan.Start, field.LabelSpan.Length), UsRecognizedLabels.CustomerIdLabel);
            if (customerId && CompanionCount(raw, lines[PhysicalLineIndex.LineIndexOf(lines, field.LabelSpan.Start)].Span) != 1)
            {
                continue; // needs explicit DL / State ID context as well as J
            }

            if (TryNormalize(raw.AsSpan(field.ValueSpan.Start, field.ValueSpan.Length), out string canonical))
            {
                matches.Add(new UsDocumentMatch(
                    field.ValueSpan,
                    UsDocumentEvidence.Structure | UsDocumentEvidence.Context | UsDocumentEvidence.Jurisdiction,
                    canonical,
                    OwnerClaim.NotApplicable));
            }
        }

        return matches
            .GroupBy(m => m.Span)
            .Select(g => g.First())
            .OrderBy(m => m.Span.Start)
            .ToList()
            .AsReadOnly();
    }

    // 4-25 ASCII alphanumerics after removing separators, at least one digit; only SINGLE internal ASCII spaces or hyphens, each
    // between two alphanumerics. [EA] the canonical value is upper-case.
    private static bool TryNormalize(ReadOnlySpan<char> value, out string canonical)
    {
        canonical = string.Empty;
        var chars = new List<char>(value.Length);
        bool digit = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (UsAscii.IsAlphanumeric(c))
            {
                chars.Add(UsAscii.Upper(c));
                digit |= UsAscii.IsDigit(c);
            }
            else if ((c == ' ' || c == '-') && i > 0 && i < value.Length - 1
                     && UsAscii.IsAlphanumeric(value[i - 1]) && UsAscii.IsAlphanumeric(value[i + 1]))
            {
                // a single internal separator: dropped by normalization
            }
            else
            {
                return false;
            }
        }

        if (chars.Count < MinLength || chars.Count > MaxLength || !digit)
        {
            return false;
        }

        canonical = new string(chars.ToArray());
        return true;
    }

    // How many "Document Type: Driver License" / "Document Type: State ID" companion fields the physical line holds. Parsed here
    // because the companion's value is itself a label ("Driver License"), which the binder reads as an empty Document Type field.
    private static int CompanionCount(string raw, RawSpan line)
    {
        int count = 0;
        int end = line.End;
        string label = UsRecognizedLabels.DocumentTypeLabel;

        for (int at = line.Start; at + label.Length <= end; at++)
        {
            if (!UsAscii.EqualsIgnoreCase(raw.AsSpan(at, label.Length), label)
                || (at > line.Start && UsAscii.IsAlphanumeric(raw[at - 1]))
                || (at + label.Length < end && UsAscii.IsAlphanumeric(raw[at + label.Length])))
            {
                continue;
            }

            int p = at + label.Length;
            p = SkipSpaces(raw, p, end);
            if (p < end && (raw[p] == ':' || raw[p] == '#' || raw[p] == '='))
            {
                p++;
            }

            p = SkipSpaces(raw, p, end);
            foreach (string value in CompanionValues)
            {
                int q = p + value.Length;
                if (q <= end && UsAscii.EqualsIgnoreCase(raw.AsSpan(p, value.Length), value)
                    && (q == end || !UsAscii.IsAlphanumeric(raw[q])) && FieldEndsAt(raw, SkipSpaces(raw, q, end), end))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private static int SkipSpaces(string raw, int p, int end)
    {
        while (p < end && raw[p] == ' ')
        {
            p++;
        }

        return p;
    }

    // The companion value must be the WHOLE field: the line ends, a field terminator follows, or another recognised label starts.
    private static bool FieldEndsAt(string raw, int p, int end)
    {
        if (p >= end || raw[p] == ';' || raw[p] == '|' || raw[p] == '\t')
        {
            return true;
        }

        foreach (string label in UsRecognizedLabels.All)
        {
            if (p + label.Length <= end && UsAscii.EqualsIgnoreCase(raw.AsSpan(p, label.Length), label))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>us.bank_account (rank 140, Level 3).</summary>
internal static class UsBankAccountCore
{
    private const int MinAccount = 4;
    private const int MaxAccount = 17;
    private const int MaxLabelDistance = 128;

    private static readonly HashSet<string> RoutingLabels = new(UsRecognizedLabels.RoutingNumber, StringComparer.Ordinal);
    private static readonly HashSet<string> BankSpecificLabels = new(UsRecognizedLabels.BankAccountSpecific, StringComparer.Ordinal);

    public static IReadOnlyList<UsDocumentMatch> Detect(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0)
        {
            return [];
        }

        var view = NormalizedView.Build(raw);
        var lines = PhysicalLineIndex.Build(view);
        var strong = new HashSet<string>(RoutingLabels, StringComparer.Ordinal);
        strong.UnionWith(BankSpecificLabels);
        strong.Add(UsRecognizedLabels.GenericAccountLabel);

        // Every recognised field, in text order: a "recognised field between" the pair is any entry of this list.
        var fields = UsFieldReader.ReadAll(view, UsRecognizedLabels.All, strong, measureAtTrimmedEnd: true);
        var lineOf = fields.Select(f => PhysicalLineIndex.LineIndexOf(lines, f.LabelSpan.Start)).ToArray();
        var financialPerLine = new Dictionary<int, int>();
        for (int i = 0; i < fields.Count; i++)
        {
            if (IsFinancial(fields[i].Label))
            {
                financialPerLine[lineOf[i]] = financialPerLine.GetValueOrDefault(lineOf[i]) + 1;
            }
        }

        var matches = new List<UsDocumentMatch>();
        for (int i = 0; i < fields.Count; i++)
        {
            var account = fields[i];
            bool bankSpecific = BankSpecificLabels.Contains(account.Label);
            if (!bankSpecific && account.Label != UsRecognizedLabels.GenericAccountLabel)
            {
                continue;
            }

            if (!account.Accepted || !TryAccountSpan(raw, account, out var span))
            {
                continue;
            }

            bool paired = PairsWithValidRouting(raw, fields, lineOf, financialPerLine, i, i - 1)
                || PairsWithValidRouting(raw, fields, lineOf, financialPerLine, i, i + 1);

            // A generic "Account Number" is nothing without a validated routing field. A bank-specific label stands alone (C);
            // [EA] a valid routing neighbour makes the tuple the strongest evidence (S+C+P). The protected span is the same.
            if (!paired && !bankSpecific)
            {
                continue;
            }

            var evidence = paired
                ? UsDocumentEvidence.Structure | UsDocumentEvidence.Context | UsDocumentEvidence.Paired
                : UsDocumentEvidence.Context;
            matches.Add(new UsDocumentMatch(
                span, evidence, UsAscii.Upper(raw.AsSpan(span.Start, span.Length)), OwnerClaim.NotApplicable));
        }

        return matches
            .GroupBy(m => m.Span)
            .Select(g => g.First())
            .OrderBy(m => m.Span.Start)
            .ToList()
            .AsReadOnly();
    }

    private static bool IsFinancial(string label) =>
        RoutingLabels.Contains(label) || BankSpecificLabels.Contains(label) || label == UsRecognizedLabels.GenericAccountLabel;

    private static bool PairsWithValidRouting(
        string raw, IReadOnlyList<UsReadField> fields, int[] lineOf, Dictionary<int, int> financialPerLine, int account, int other)
    {
        if (other < 0 || other >= fields.Count)
        {
            return false;
        }

        var routing = fields[other];
        if (!RoutingLabels.Contains(routing.Label) || !routing.Accepted || !TryValueSpan(raw, routing, out var span)
            || !IsValidAba(raw.AsSpan(span.Start, span.Length)))
        {
            return false;
        }

        // "within 128 raw characters": label start to label start, either order.
        if (Math.Abs(fields[account].LabelSpan.Start - routing.LabelSpan.Start) > MaxLabelDistance)
        {
            return false;
        }

        // Same physical line, or two IMMEDIATELY adjacent lines that each hold exactly one recognised financial field. (No
        // recognised field lies between the two: they are neighbours in the field list. A blank line makes the lines non-adjacent.)
        int a = lineOf[account];
        int b = lineOf[other];
        return a == b
            || (Math.Abs(a - b) == 1 && financialPerLine.GetValueOrDefault(a) == 1 && financialPerLine.GetValueOrDefault(b) == 1);
    }

    // The financial value: the bound value without trailing spaces and, when another field follows, without ONE separating comma
    // (D2.2: financial fields on a line may be separated by spaces, comma, semicolon, HT or '|').
    private static bool TryValueSpan(string raw, UsReadField field, out RawSpan span)
    {
        int start = field.ValueSpan.Start;
        int end = field.ValueSpan.End;
        if (field.Terminator == FieldBindingTerminator.NextRecognizedLabel && end > start && raw[end - 1] == ',')
        {
            end--;
            while (end > start && raw[end - 1] == ' ')
            {
                end--;
            }
        }

        span = new RawSpan(start, end - start);
        return end > start;
    }

    private static bool TryAccountSpan(string raw, UsReadField field, out RawSpan span)
    {
        if (!TryValueSpan(raw, field, out span) || span.Length < MinAccount || span.Length > MaxAccount)
        {
            return false;
        }

        for (int i = span.Start; i < span.End; i++)
        {
            if (!UsAscii.IsAlphanumeric(raw[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Exactly 9 ASCII digits, prefix 00 / 01-12 / 21-32 / 61-72 / 80, and the 3-7-1 modulus-10 checksum.</summary>
    internal static bool IsValidAba(ReadOnlySpan<char> value)
    {
        if (value.Length != 9)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!UsAscii.IsDigit(c))
            {
                return false;
            }
        }

        int prefix = (value[0] - '0') * 10 + (value[1] - '0');
        bool prefixOk = prefix == 0 || (prefix >= 1 && prefix <= 12) || (prefix >= 21 && prefix <= 32)
            || (prefix >= 61 && prefix <= 72) || prefix == 80;
        if (!prefixOk)
        {
            return false;
        }

        int sum = 3 * (Digit(value, 0) + Digit(value, 3) + Digit(value, 6))
            + 7 * (Digit(value, 1) + Digit(value, 4) + Digit(value, 7))
            + (Digit(value, 2) + Digit(value, 5) + Digit(value, 8));
        return sum % 10 == 0;
    }

    private static int Digit(ReadOnlySpan<char> value, int index) => value[index] - '0';
}
