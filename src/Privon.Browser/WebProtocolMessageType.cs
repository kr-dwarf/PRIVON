namespace Privon.Browser;

/// <summary>
/// PRIVON 0.3.1 Gate 031F1R -- the six frozen Native Messaging protocol message families (Gate
/// 031E2 PROTOCOL / Gate 031F1R MESSAGE_TYPE). The wire discriminator is the exact, case-sensitive
/// JSON string matching each member's own name (e.g. <c>"type": "Hello"</c>) -- never a numeric
/// opcode. This type carries no product/browser policy: it is a closed set of MECHANICAL message
/// shapes, never compared against any supported origin or browser identity anywhere in this
/// assembly.
/// </summary>
public enum WebProtocolMessageType
{
    Hello,
    HelloAck,
    StateInvalidate,
    StateAssert,
    ChallengeRequest,
    ChallengeResponse,
}
