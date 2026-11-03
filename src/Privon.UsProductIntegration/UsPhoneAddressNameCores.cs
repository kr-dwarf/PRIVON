using Privon.Core;
using Privon.Detection;
using Privon.UsMbiSevis;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// The value grammars of us.phone, us.address and us.name (frozen D2.1 / D2.2 / D3A.1 closed lists, read-only). Field binding is the
// shared FieldBinder (unchanged); like the other cores these run on the UNMODIFIED raw text, so every span is an exact UTF-16 raw span
// and no fullwidth character is ever folded into an ASCII one. Where D2.2 leaves a branch open the code emits NO candidate and the
// branch is marked [EA] here and in the report. A scan that fails throws: it is never "no candidates".

/// <summary>us.phone (rank 310, Level 2). Needs a validated NPA snapshot: the detector never calls this without one.</summary>
internal static class UsPhoneCore
{
    private static readonly string[] Introducers = ["extension", "ext", "x"];

    public static IReadOnlyList<UsDocumentMatch> Detect(string raw, UsAuthoritySet authority)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(authority);
        if (raw.Length == 0)
        {
            return [];
        }

        var matches = new List<UsDocumentMatch>();

        // An approved label binds a value that may be a bare compact 10-digit number.
        foreach (var field in UsLabelledFields.Read(raw, UsRecognizedLabels.Phone, UsRecognizedLabels.All))
        {
            if (field.BindingAccepted
                && TryPhone(raw, field.ValueSpan.Start, field.ValueSpan.End, allowCompact: true, authority, out var span, out string canonical))
            {
                matches.Add(Match(span, UsDocumentEvidence.Structure | UsDocumentEvidence.Context, canonical));
            }
        }

        // Without a label only an approved formatting (or +1) is a phone; a compact 10-digit number alone is an intended miss.
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if ((c != '+' && c != '(' && !UsAscii.IsDigit(c)) || !LeftBoundary(raw, i))
            {
                continue;
            }

            if (TryPhone(raw, i, raw.Length, allowCompact: false, authority, out var span, out string canonical))
            {
                matches.Add(Match(span, UsDocumentEvidence.Structure, canonical));
                i = span.End - 1;
            }
        }

        // One candidate per span: the labelled reading (added first) is the stronger evidence.
        return matches
            .GroupBy(m => m.Span)
            .Select(g => g.First())
            .OrderBy(m => m.Span.Start).ThenBy(m => m.Span.Length)
            .ToList()
            .AsReadOnly();
    }

    private static UsDocumentMatch Match(RawSpan span, UsDocumentEvidence evidence, string canonical) =>
        new(span, evidence, canonical, OwnerClaim.NotApplicable, RiskLevel.Level2, DetectionConfidence.High);

    // [EA] a number glued into a longer digit / hyphen / dot / plus run is not a phone token.
    private static bool LeftBoundary(string raw, int at) =>
        at == 0 || (!char.IsLetterOrDigit(raw[at - 1]) && raw[at - 1] != '-' && raw[at - 1] != '+' && raw[at - 1] != '.');

    // The end of the bound field is a boundary by itself: a label that follows glued with its own separator already ended the field.
    private static bool BoundaryAfter(string raw, int end, int limit)
    {
        if (end >= limit)
        {
            return true;
        }

        char c = raw[end];
        if (char.IsLetterOrDigit(c) || c == '-')
        {
            return false;
        }

        return !(c == '.' && end + 1 < limit && UsAscii.IsDigit(raw[end + 1]));
    }

    private static bool TryPhone(
        string raw, int at, int limit, bool allowCompact, UsAuthoritySet authority, out RawSpan span, out string canonical)
    {
        span = default;
        canonical = string.Empty;

        if (!TryNumber(raw, at, limit, allowCompact, out int numberEnd, out string national)
            || national[0] < '2' || national[0] > '9'      // NPA leading digit
            || national[3] < '2' || national[3] > '9'      // NXX leading digit
            || !authority.IsKnownNpa(national.AsSpan(0, 3)))
        {
            return false;
        }

        int end = numberEnd;
        string suffix = string.Empty;
        if (TryExtension(raw, numberEnd, limit, out int extensionEnd, out string digits))
        {
            end = extensionEnd;
            suffix = "x" + digits;
        }
        else if (!BoundaryAfter(raw, numberEnd, limit))
        {
            return false;
        }

        span = new RawSpan(at, end - at);
        canonical = national + suffix;
        return true;
    }

    // The ten approved lexical forms: four domestic groupings (the compact one only with a label) and six country-code forms.
    private static bool TryNumber(string raw, int at, int limit, bool allowCompact, out int end, out string national)
    {
        end = 0;
        national = string.Empty;
        char c = raw[at];

        if (c == '+')
        {
            if (at + 2 >= limit || raw[at + 1] != '1')
            {
                return false;
            }

            char d = raw[at + 2];
            if (d == '-')
            {
                return Grouped(raw, at + 3, limit, '-', out end, out national);          // +1-NPA-NXX-XXXX
            }

            if (d == ' ')
            {
                return at + 3 < limit && raw[at + 3] == '('
                    ? Parenthesised(raw, at + 3, limit, out end, out national)           // +1 (NPA) NXX-XXXX
                    : Grouped(raw, at + 3, limit, ' ', out end, out national);           // +1 NPA NXX XXXX
            }

            return UsAscii.IsDigit(d) && Compact(raw, at + 2, limit, out end, out national); // +1NPANXXXXXX
        }

        if (c == '1')
        {
            if (at + 1 < limit && raw[at + 1] == '-')
            {
                return Grouped(raw, at + 2, limit, '-', out end, out national);          // 1-NPA-NXX-XXXX
            }

            return at + 2 < limit && raw[at + 1] == ' ' && raw[at + 2] == '('
                && Parenthesised(raw, at + 2, limit, out end, out national);            // 1 (NPA) NXX-XXXX
        }

        if (c == '(')
        {
            return Parenthesised(raw, at, limit, out end, out national);                 // (NPA) NXX-XXXX
        }

        if (!UsAscii.IsDigit(c))
        {
            return false;
        }

        if (at + 3 < limit && (raw[at + 3] == '-' || raw[at + 3] == '.' || raw[at + 3] == ' '))
        {
            return Grouped(raw, at, limit, raw[at + 3], out end, out national);         // NPA-NXX-XXXX, NPA.NXX.XXXX, NPA NXX XXXX
        }

        return allowCompact && Compact(raw, at, limit, out end, out national);
    }

    private static bool Digits(string raw, int at, int count, int limit)
    {
        if (at + count > limit)
        {
            return false;
        }

        for (int i = at; i < at + count; i++)
        {
            if (!UsAscii.IsDigit(raw[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Grouped(string raw, int at, int limit, char separator, out int end, out string national)
    {
        end = 0;
        national = string.Empty;
        if (!Digits(raw, at, 3, limit) || at + 3 >= limit || raw[at + 3] != separator
            || !Digits(raw, at + 4, 3, limit) || at + 7 >= limit || raw[at + 7] != separator
            || !Digits(raw, at + 8, 4, limit))
        {
            return false;
        }

        end = at + 12;
        national = string.Concat(raw.AsSpan(at, 3), raw.AsSpan(at + 4, 3), raw.AsSpan(at + 8, 4));
        return true;
    }

    private static bool Parenthesised(string raw, int at, int limit, out int end, out string national)
    {
        end = 0;
        national = string.Empty;
        if (at + 14 > limit || raw[at] != '(' || !Digits(raw, at + 1, 3, limit) || raw[at + 4] != ')' || raw[at + 5] != ' '
            || !Digits(raw, at + 6, 3, limit) || raw[at + 9] != '-' || !Digits(raw, at + 10, 4, limit))
        {
            return false;
        }

        end = at + 14;
        national = string.Concat(raw.AsSpan(at + 1, 3), raw.AsSpan(at + 6, 3), raw.AsSpan(at + 10, 4));
        return true;
    }

    private static bool Compact(string raw, int at, int limit, out int end, out string national)
    {
        end = 0;
        national = string.Empty;
        if (!Digits(raw, at, 10, limit))
        {
            return false;
        }

        end = at + 10;
        national = raw.Substring(at, 10);
        return true;
    }

    // [EA] After the number: at most one space, an introducer (x / ext / extension, ASCII-case-insensitive), at most one space, then
    // ASCII digits and a token boundary. "ext." and any other introducer are not extensions: the tail stays outside the span.
    private static bool TryExtension(string raw, int at, int limit, out int end, out string digits)
    {
        end = 0;
        digits = string.Empty;
        int q = at;
        if (q < limit && raw[q] == ' ')
        {
            q++;
        }

        string? introducer = null;
        foreach (string candidate in Introducers)
        {
            if (q + candidate.Length <= limit && UsAscii.EqualsIgnoreCase(raw.AsSpan(q, candidate.Length), candidate))
            {
                introducer = candidate;
                break;
            }
        }

        if (introducer is null)
        {
            return false;
        }

        q += introducer.Length;
        if (q < limit && raw[q] == ' ')
        {
            q++;
        }

        int start = q;
        while (q < limit && UsAscii.IsDigit(raw[q]))
        {
            q++;
        }

        if (q == start || !BoundaryAfter(raw, q, limit))
        {
            return false;
        }

        end = q;
        digits = raw.Substring(start, q - start);
        return true;
    }
}

/// <summary>us.address (rank 330, Level 2). Needs validated USPS suffix and state snapshots.</summary>
internal static class UsAddressCore
{
    private const int MaxPathBLength = 160; // D2.1: a full address span covers at most 160 raw characters

    private static readonly string[] Units = ["APT", "STE", "UNIT"];

    public static IReadOnlyList<UsDocumentMatch> Detect(string raw, UsAuthoritySet authority)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(authority);
        if (raw.Length == 0)
        {
            return [];
        }

        var matches = new List<UsDocumentMatch>();

        // Path A: an approved Address label bound by the shared field grammar. Per D2.2 the span ends at the logical-field boundary;
        // [EA] trailing sentence punctuation (. ? !) before it is left out, as for Path B.
        foreach (var field in UsLabelledFields.Read(raw, UsRecognizedLabels.Address, UsRecognizedLabels.All))
        {
            if (!field.BindingAccepted)
            {
                continue;
            }

            int start = field.ValueSpan.Start;
            int end = field.ValueSpan.End;
            while (end > start && (raw[end - 1] == '.' || raw[end - 1] == '?' || raw[end - 1] == '!'))
            {
                end--;
            }

            if (end > start && HeadIsAddress(raw, start, end, authority))
            {
                matches.Add(Match(new RawSpan(start, end - start), UsDocumentEvidence.Structure | UsDocumentEvidence.Context, raw));
            }
        }

        // Path B: no label, one physical line: street + suffix + city + state / possession + ZIP.
        int lineEnd = -1;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] < '1' || raw[i] > '9' || (i > 0 && (char.IsLetterOrDigit(raw[i - 1]) || raw[i - 1] == '-')))
            {
                continue;
            }

            if (i > lineEnd)
            {
                lineEnd = LineEnd(raw, i); // once per line: a scan per candidate start would be quadratic on one long line
            }

            if (TryPathB(raw, i, lineEnd, authority, out int end) && end - i <= MaxPathBLength)
            {
                matches.Add(Match(new RawSpan(i, end - i), UsDocumentEvidence.Structure | UsDocumentEvidence.Jurisdiction, raw));
                i = end - 1;
            }
        }

        return matches
            .GroupBy(m => m.Span)
            .Select(g => g.First())
            .OrderBy(m => m.Span.Start).ThenBy(m => m.Span.Length)
            .ToList()
            .AsReadOnly();
    }

    // The product's AliasAssigner gives an ABSENT canonical no alias (and AliasReplacer then throws), so the matched text itself is the
    // canonical value, exactly as typed. The frozen oracle writes `null` for this field; every other field of its record is kept.
    private static UsDocumentMatch Match(RawSpan span, UsDocumentEvidence evidence, string raw) =>
        new(span, evidence, raw.Substring(span.Start, span.Length), OwnerClaim.NotApplicable, RiskLevel.Level2, DetectionConfidence.High);

    private static bool HeadIsAddress(string raw, int start, int limit, UsAuthoritySet authority)
    {
        if (TryStreet(raw, start, limit, authority, out int end) || TryBox(raw, start, limit, out end))
        {
            return end >= limit || (!char.IsLetterOrDigit(raw[end]) && raw[end] != '-' && raw[end] != '\'');
        }

        return false;
    }

    private static bool IsNameChar(char c) => UsAscii.IsAlphanumeric(c) || c == '.' || c == '\'' || c == '-';

    // street-name token: [A-Za-z0-9][A-Za-z0-9.'-]{0,39}, a maximal run (a 41st character of the class makes it invalid).
    private static bool TryNameToken(string raw, int at, int limit, out int end)
    {
        end = at;
        if (at >= limit || !UsAscii.IsAlphanumeric(raw[at]))
        {
            return false;
        }

        int q = at + 1;
        while (q < limit && IsNameChar(raw[q]))
        {
            q++;
        }

        end = q;
        return q - at <= 40;
    }

    // primary street number [1-9][0-9]{0,5}[A-Za-z]? + one space + 1-4 street-name tokens + a recognised USPS suffix (+ one unit).
    private static bool TryStreet(string raw, int at, int limit, UsAuthoritySet authority, out int end)
    {
        end = 0;
        int q = at;
        if (q >= limit || raw[q] < '1' || raw[q] > '9')
        {
            return false;
        }

        int digits = 0;
        while (q < limit && UsAscii.IsDigit(raw[q]) && digits < 6)
        {
            q++;
            digits++;
        }

        if (q < limit && UsAscii.IsDigit(raw[q]))
        {
            return false;
        }

        if (q < limit && UsAscii.IsLetter(raw[q]))
        {
            q++;
        }

        if (q + 1 >= limit || raw[q] != ' ')
        {
            return false;
        }

        q++;
        var tokens = new List<(int Start, int End)>(5);
        while (tokens.Count < 5 && TryNameToken(raw, q, limit, out int tokenEnd))
        {
            tokens.Add((q, tokenEnd));
            if (tokenEnd + 1 < limit && raw[tokenEnd] == ' ' && raw[tokenEnd + 1] != ' ')
            {
                q = tokenEnd + 1;
            }
            else
            {
                break;
            }
        }

        for (int k = 1; k <= 4 && k < tokens.Count; k++)
        {
            if (authority.IsKnownSuffix(raw.AsSpan(tokens[k].Start, tokens[k].End - tokens[k].Start)))
            {
                end = tokens[k].End;
                TryUnit(raw, ref end, limit);
                return true;
            }
        }

        return false;
    }

    // optional secondary unit: " APT|STE|UNIT" + one space + [A-Za-z0-9-]{1,10}. A unit alone never makes an address.
    private static void TryUnit(string raw, ref int end, int limit)
    {
        int q = end;
        if (q >= limit || raw[q] != ' ')
        {
            return;
        }

        q++;
        foreach (string unit in Units)
        {
            if (q + unit.Length + 1 > limit || !UsAscii.EqualsIgnoreCase(raw.AsSpan(q, unit.Length), unit) || raw[q + unit.Length] != ' ')
            {
                continue;
            }

            int start = q + unit.Length + 1;
            int run = start;
            while (run < limit && (UsAscii.IsAlphanumeric(raw[run]) || raw[run] == '-'))
            {
                run++;
            }

            if (run > start && run - start <= 10)
            {
                end = run;
            }

            return;
        }
    }

    // PO Box | RR n Box | HC n Box, each followed by a [A-Za-z0-9-]{1,10} box id. The words are matched exactly as written.
    private static bool TryBox(string raw, int at, int limit, out int end)
    {
        end = 0;
        int q;
        if (StartsWith(raw, at, limit, "PO Box "))
        {
            q = at + "PO Box ".Length;
        }
        else if (StartsWith(raw, at, limit, "RR ") || StartsWith(raw, at, limit, "HC "))
        {
            q = at + 3;
            if (q >= limit || raw[q] < '1' || raw[q] > '9')
            {
                return false;
            }

            int digits = 0;
            while (q < limit && UsAscii.IsDigit(raw[q]) && digits < 5)
            {
                q++;
                digits++;
            }

            if (digits > 4 || !StartsWith(raw, q, limit, " Box "))
            {
                return false;
            }

            q += " Box ".Length;
        }
        else
        {
            return false;
        }

        int start = q;
        while (q < limit && (UsAscii.IsAlphanumeric(raw[q]) || raw[q] == '-'))
        {
            q++;
        }

        if (q == start || q - start > 10)
        {
            return false;
        }

        end = q;
        return true;
    }

    private static bool StartsWith(string raw, int at, int limit, string text) =>
        at + text.Length <= limit && string.CompareOrdinal(raw, at, text, 0, text.Length) == 0;

    private static int LineEnd(string raw, int at)
    {
        int q = at;
        while (q < raw.Length && raw[q] != '\r' && raw[q] != '\n')
        {
            q++;
        }

        return q;
    }

    // Path B separators: exactly one of one ASCII space, "," or ", ". Zero width and two or more spaces are rejected.
    private static bool TrySeparator(string raw, int at, int limit, out int end)
    {
        end = at;
        if (at >= limit)
        {
            return false;
        }

        if (raw[at] == ',')
        {
            end = at + 1 < limit && raw[at + 1] == ' ' ? at + 2 : at + 1;
            return end < limit && raw[end] != ' ';
        }

        if (raw[at] == ' ')
        {
            end = at + 1;
            return end < limit && raw[end] != ' ';
        }

        return false;
    }

    private static bool TryPathB(string raw, int at, int limit, UsAuthoritySet authority, out int end)
    {
        end = 0;
        if (!TryStreet(raw, at, limit, authority, out int streetEnd) || !TrySeparator(raw, streetEnd, limit, out int q))
        {
            return false;
        }

        // 1-4 city tokens [A-Za-z][A-Za-z.'-]{0,39} joined by single spaces; the state may itself look like a city token, so each
        // count is tried in turn (smallest first).
        var cities = new List<int>(4);
        int cp = q;
        while (cities.Count < 4 && cp < limit && UsAscii.IsLetter(raw[cp]))
        {
            int ce = cp + 1;
            while (ce < limit && (UsAscii.IsLetter(raw[ce]) || raw[ce] == '.' || raw[ce] == '\'' || raw[ce] == '-'))
            {
                ce++;
            }

            if (ce - cp > 40)
            {
                return false;
            }

            cities.Add(ce);
            if (ce + 1 < limit && raw[ce] == ' ' && raw[ce + 1] != ' ')
            {
                cp = ce + 1;
            }
            else
            {
                break;
            }
        }

        foreach (int cityEnd in cities)
        {
            if (!TrySeparator(raw, cityEnd, limit, out int sp)
                || sp + 3 > limit || raw[sp] < 'A' || raw[sp] > 'Z' || raw[sp + 1] < 'A' || raw[sp + 1] > 'Z' || raw[sp + 2] != ' '
                || !authority.IsKnownState(raw.AsSpan(sp, 2)))
            {
                continue;
            }

            if (TryZip(raw, sp + 3, limit, out end))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryZip(string raw, int at, int limit, out int end)
    {
        end = 0;
        for (int i = at; i < at + 5; i++)
        {
            if (i >= limit || !UsAscii.IsDigit(raw[i]))
            {
                return false;
            }
        }

        int q = at + 5;
        if (q < limit && raw[q] == '-')
        {
            for (int i = q + 1; i < q + 5; i++)
            {
                if (i >= limit || !UsAscii.IsDigit(raw[i]))
                {
                    return false;
                }
            }

            q += 5;
        }

        if (q < limit && (char.IsLetterOrDigit(raw[q]) || raw[q] == '-'))
        {
            return false;
        }

        end = q;
        return true;
    }
}

/// <summary>us.name (rank 350): Level 2 for the specific labels, Level 1 for the generic label and the approved honorific forms.</summary>
internal static class UsNameCore
{
    private const int MaxTokenLength = 40;

    // Longest first. Matched case-sensitively, exactly as D2.2 writes them ([EA] "mr" / "MR" are not honorifics).
    private static readonly string[] Honorifics = ["Mrs.", "Mrs", "Mr.", "Mr", "Ms.", "Ms", "Miss", "Dr.", "Dr"];

    public static IReadOnlyList<UsDocumentMatch> Detect(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0)
        {
            return [];
        }

        var matches = new List<UsDocumentMatch>();

        // Approved labels bind 1-4 name tokens at the value start.
        var strong = new List<string>(UsRecognizedLabels.NameSpecific) { UsRecognizedLabels.NameGeneric };
        foreach (var field in UsLabelledFields.Read(raw, strong, UsRecognizedLabels.All))
        {
            if (!field.BindingAccepted)
            {
                continue;
            }

            bool generic = UsAscii.EqualsIgnoreCase(raw.AsSpan(field.LabelSpan.Start, field.LabelSpan.Length), UsRecognizedLabels.NameGeneric);
            int start = field.ValueSpan.Start;
            int limit = field.ValueSpan.End;

            // "Name: Dr. Alice Doe" is the honorific form below; reading "Dr" as a one-token name would only shadow it.
            if (generic && HonorificAt(raw, start, limit) is not null)
            {
                continue;
            }

            if (TryTokens(raw, start, limit, minTokens: 1, maxTokens: 4, out int end, out string canonical))
            {
                matches.Add(generic
                    ? new UsDocumentMatch(new RawSpan(start, end - start), UsDocumentEvidence.Context, canonical, OwnerClaim.NotApplicable,
                        RiskLevel.Level1, DetectionConfidence.Medium)
                    : new UsDocumentMatch(new RawSpan(start, end - start), UsDocumentEvidence.Context, canonical, OwnerClaim.NotApplicable,
                        RiskLevel.Level2, DetectionConfidence.High));
            }
        }

        // Approved honorifics + exactly 2-4 name tokens, in one physical line. The span starts at the honorific.
        for (int i = 0; i < raw.Length; i++)
        {
            if ((raw[i] != 'M' && raw[i] != 'D') || (i > 0 && UsAscii.IsAlphanumeric(raw[i - 1])))
            {
                continue;
            }

            string? honorific = HonorificAt(raw, i, raw.Length);
            if (honorific is null)
            {
                continue;
            }

            int tokensAt = i + honorific.Length + 1;
            if (TryTokens(raw, tokensAt, raw.Length, minTokens: 2, maxTokens: 4, out int end, out string canonical))
            {
                matches.Add(new UsDocumentMatch(
                    new RawSpan(i, end - i), UsDocumentEvidence.Context, canonical, OwnerClaim.NotApplicable,
                    RiskLevel.Level1, DetectionConfidence.Medium));
                i = end - 1;
            }
        }

        return matches
            .GroupBy(m => m.Span)
            .Select(g => g.First())
            .OrderBy(m => m.Span.Start).ThenBy(m => m.Span.Length)
            .ToList()
            .AsReadOnly();
    }

    // The honorific that starts at `at` and is followed by exactly one space and a token start, or null.
    private static string? HonorificAt(string raw, int at, int limit)
    {
        foreach (string honorific in Honorifics)
        {
            int after = at + honorific.Length;
            if (after + 1 < limit && string.CompareOrdinal(raw, at, honorific, 0, honorific.Length) == 0
                && raw[after] == ' ' && UsAscii.IsLetter(raw[after + 1]))
            {
                return honorific;
            }
        }

        return null;
    }

    // 1-4 (or 2-4) tokens [A-Za-z][A-Za-z'-]{0,39} joined by single ASCII spaces; a one-letter initial may end in a period. After the
    // last token the text must end, or continue with one of the stop characters , ; | tab CR LF . ? ! -- anything else (a slash, a
    // non-ASCII letter, a digit, a second space, an unlisted punctuation mark) makes the whole candidate invalid, never a shorter one.
    private static bool TryTokens(string raw, int at, int limit, int minTokens, int maxTokens, out int end, out string canonical)
    {
        end = 0;
        canonical = string.Empty;
        var parts = new List<string>(4);
        int p = at;

        while (true)
        {
            if (p >= limit || !UsAscii.IsLetter(raw[p]))
            {
                return false;
            }

            int q = p + 1;
            while (q < limit && (UsAscii.IsLetter(raw[q]) || raw[q] == '\'' || raw[q] == '-'))
            {
                q++;
            }

            if (q - p > MaxTokenLength)
            {
                return false;
            }

            if (q - p == 1 && q < limit && raw[q] == '.')
            {
                q++; // an initial keeps its period
            }

            parts.Add(raw.Substring(p, q - p));
            if (parts.Count > maxTokens)
            {
                return false;
            }

            if (q < limit && raw[q] == ' ')
            {
                if (q + 1 < limit && UsAscii.IsLetter(raw[q + 1]))
                {
                    p = q + 1;
                    continue;
                }

                return false;
            }

            if (q < limit && !IsStop(raw[q]))
            {
                return false;
            }

            end = q;
            break;
        }

        if (parts.Count < minTokens)
        {
            return false;
        }

        canonical = string.Join(' ', parts);
        return true;
    }

    private static bool IsStop(char c) =>
        c == ',' || c == ';' || c == '|' || c == '\t' || c == '\r' || c == '\n' || c == '.' || c == '?' || c == '!';
}
