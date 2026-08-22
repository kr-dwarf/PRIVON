namespace Privon.Detection;

public interface IDetector
{
    string Name { get; }
    PiiType PiiType { get; }
    IReadOnlyList<DetectionCandidate> Detect(DetectionContext context);
}
