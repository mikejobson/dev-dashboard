using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DevDash.Agent.Updates;

/// <summary>
/// The two checks every release must pass: <c>checksums.txt</c> carries a valid signature from the
/// trusted key, and each downloaded file has the SHA-256 that the signed <c>checksums.txt</c> lists.
/// </summary>
public static partial class ReleaseVerifier
{
    [GeneratedRegex(@"^([0-9a-fA-F]{64})[ \t]+\*?([A-Za-z0-9][A-Za-z0-9._-]{0,127})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLine();

    public static bool TryImportPublicKey(string? pem, out ECDsa? key)
    {
        key = null;
        // A public key only: a private key would import as well, and must never be what a build carries.
        if (string.IsNullOrWhiteSpace(pem) || !pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
        {
            return false;
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            // NIST P-256 only. Some platforms report the curve by name and leave the OID value empty.
            var curve = ecdsa.ExportParameters(includePrivateParameters: false).Curve.Oid;
            var p256 = curve?.Value == "1.2.840.10045.3.1.7"
                || (string.IsNullOrEmpty(curve?.Value) && curve?.FriendlyName is "nistP256" or "ECDSA_P256");
            if (ecdsa.KeySize != 256 || !p256)
            {
                ecdsa.Dispose();
                return false;
            }

            key = ecdsa;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or FormatException)
        {
            ecdsa.Dispose();
            return false;
        }
    }

    /// <summary>Why the signature over <paramref name="data"/> is not acceptable, or null when it is good.</summary>
    public static string? SignatureProblem(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReleaseTrust trust)
    {
        if (!TryImportPublicKey(trust.PublicKeyPem, out var key))
        {
            return "this build has no release signing key, so no release can be verified.";
        }

        using (key)
        {
            if (signature.IsEmpty)
            {
                return "the release has no signature (checksums.txt.sig is missing or empty).";
            }

            bool valid;
            try
            {
                valid = key!.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (CryptographicException)
            {
                valid = false;
            }

            return valid ? null : "the signature on checksums.txt is not valid for this agent's release key.";
        }
    }

    /// <summary>Reads <c>&lt;sha256&gt;  &lt;file name&gt;</c> lines. Lines in any other shape are ignored; a file listed twice with different hashes is dropped.</summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            if (ChecksumLine().Match(raw.Trim()) is not { Success: true } match)
            {
                continue;
            }

            var name = match.Groups[2].Value;
            var hash = match.Groups[1].Value.ToLowerInvariant();
            if (result.TryGetValue(name, out var existing) && existing != hash)
            {
                conflicted.Add(name);
            }

            result[name] = hash;
        }

        foreach (var name in conflicted)
        {
            result.Remove(name);
        }

        return result;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static async Task<string> Sha256HexAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    /// <summary>Why <paramref name="actualSha256"/> is not what the checksums list for <paramref name="name"/>, or null when it matches.</summary>
    public static string? ChecksumProblem(IReadOnlyDictionary<string, string> checksums, string name, string actualSha256)
    {
        if (!checksums.TryGetValue(name, out var expected))
        {
            return $"checksums.txt has no entry for {name}.";
        }

        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(actualSha256.ToLowerInvariant()))
            ? null
            : $"checksum mismatch for {name}: expected {expected}, got {actualSha256}.";
    }
}
