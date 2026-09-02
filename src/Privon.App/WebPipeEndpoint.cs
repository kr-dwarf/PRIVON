using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the single deterministic local named-pipe endpoint name derived
/// from the caller's SID and this installation's own canonical executable path (Gate 031F6H R4/R5,
/// frozen algorithm). Same SID + same canonical path always yields the same endpoint; a different
/// SID or a different executable path always yields a different one. No persistence, no randomness,
/// no secret, and never <see cref="System.Reflection.Assembly.Location"/> (which can diverge from the
/// real on-disk executable path for single-file/trimmed publishes) -- the caller supplies the real
/// executable path from the composition boundary instead.
/// </summary>
public static class WebPipeEndpoint
{
    private const string Prefix = "PRIVON-Web-";
    private const int EndpointHashBytes = 16;

    public static string Compute(string sid, string executableFullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sid);
        ArgumentException.ThrowIfNullOrEmpty(executableFullPath);

        string canonicalPath = Path.GetFullPath(executableFullPath).ToUpperInvariant();
        byte[] material = Encoding.UTF8.GetBytes(sid + "\0" + canonicalPath);
        byte[] hash = SHA256.HashData(material);
        string hex = Convert.ToHexStringLower(hash.AsSpan(0, EndpointHashBytes));

        return Prefix + hex;
    }
}
