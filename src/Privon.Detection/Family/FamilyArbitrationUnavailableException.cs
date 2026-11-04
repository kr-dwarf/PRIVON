namespace Privon.Detection.Family;

/// <summary>
/// The opt-in <see cref="DetectionPipeline"/> needed a family arbiter and the provider resolved none
/// (<see cref="FamilyArbitrationState.ArbitrationUnavailable"/>). The scan did not complete: this is neither
/// "no candidates" nor an empty result, and it is raised for every input, including text with nothing to arbitrate.
///
/// Typed data is exactly <see cref="FamilyName"/>. The constructor is internal: only the pipeline raises it.
/// </summary>
public sealed class FamilyArbitrationUnavailableException : Exception
{
    internal FamilyArbitrationUnavailableException(string familyName)
        : base("FamilyArbitrationUnavailable(" + familyName + ")")
    {
        FamilyName = familyName;
    }

    public string FamilyName { get; }
}
