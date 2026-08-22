namespace Privon.Detection;

/// <summary>
/// Phase 2T.1 -- memory-only, per-composition alias registry. A plain instance class, never a
/// singleton -- the design doc requires each window/composer/composition to own its own
/// AliasMap so mappings never leak across them ("각 창 또는 식별 가능한 입력 세션마다 별도의
/// 작성 상태, 캐시, AliasMap을 사용한다"). This type has no idea what a "composer" or
/// "revision" is; it is deliberately just the in-memory key->token registry. The owning
/// runtime layer (not built in this Detection-only phase) is responsible for creating one
/// instance per composition and discarding it on send-complete / composer reset / app exit /
/// window close / Windows lock, per the audit contract §5 discard triggers -- this class does
/// not attempt to know or enforce that lifecycle itself.
///
/// Numbering is per-PiiType and monotonically increasing for the lifetime of one instance:
/// once a canonical value is assigned a number, that number is never reused within this
/// instance, even if every candidate using it is later removed from the raw text (design doc:
/// "AliasMap이 살아 있는 동일 composition이라면... [전화번호1] 번호를 Phone C에게 재배정하지
/// 않는다"). A fresh AliasMap instance always starts numbering over from 1 -- that is exactly
/// what "discard and create a new instance" means.
///
/// Never persisted: no reference to Privon.Storage exists anywhere in this type or project.
/// </summary>
public sealed class AliasMap
{
    private readonly Dictionary<CanonicalValue, AliasToken> _assigned = [];
    private readonly Dictionary<PiiType, int> _nextNumber = [];

    /// <summary>
    /// Returns the existing token for <paramref name="value"/> if this exact typed canonical
    /// value (PiiType + Value together) has already been assigned one in this instance's
    /// lifetime, otherwise assigns and returns a new token with the next number for that
    /// PiiType. Never mutates any state before validating the PiiType has a defined label --
    /// an undefined PiiType throws before the counter is touched, so a failed call never
    /// leaves the per-type counter incremented without a corresponding assigned entry.
    /// </summary>
    public AliasToken GetOrAdd(CanonicalValue value)
    {
        if (_assigned.TryGetValue(value, out var existing)) return existing;

        var label = AliasLabelProvider.GetLabel(value.PiiType); // throws first -- see doc above
        int number = _nextNumber.GetValueOrDefault(value.PiiType, 0) + 1;
        _nextNumber[value.PiiType] = number;

        var token = new AliasToken(value.PiiType, number, $"[{label}{number}]");
        _assigned[value] = token;
        return token;
    }
}
