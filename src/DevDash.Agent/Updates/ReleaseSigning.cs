using System.Security.Cryptography;

namespace DevDash.Agent.Updates;

/// <summary>The public key a release must be signed with before this agent will install it.</summary>
/// <param name="PublicKeyPem">An ECDSA P-256 public key, PEM (<c>BEGIN PUBLIC KEY</c>).</param>
/// <param name="IsTestKey">True when the key was swapped in at build time for a test build.</param>
public sealed record ReleaseTrust(string PublicKeyPem, bool IsTestKey = false)
{
    /// <summary>False for the placeholder that ships until a real key is generated: such a build refuses every update.</summary>
    public bool IsConfigured => ReleaseVerifier.TryImportPublicKey(PublicKeyPem, out _);

    /// <summary>Short SHA-256 of the key, to compare builds by eye.</summary>
    public string Fingerprint
    {
        get
        {
            if (!ReleaseVerifier.TryImportPublicKey(PublicKeyPem, out var key))
            {
                return "none";
            }

            using (key)
            {
                return Convert.ToHexStringLower(SHA256.HashData(key!.ExportSubjectPublicKeyInfo()))[..16];
            }
        }
    }

    public string Describe() =>
        !IsConfigured ? "not set (this build cannot verify releases, so it will not update itself)"
        : IsTestKey ? $"TEST KEY {Fingerprint} (a test build: it does not trust real releases)"
        : Fingerprint;
}

/// <summary>
/// Releases are signed in the release workflow: <c>checksums.txt.sig</c> is an ECDSA P-256 / SHA-256
/// signature (DER, as <c>openssl dgst -sha256 -sign</c> writes it) over <c>checksums.txt</c>, made with
/// the private key in the <c>DEVDASH_RELEASE_SIGNING_KEY</c> repository secret. The matching public key
/// is the constant below: it is compiled into the agent and cannot be changed by configuration, by the
/// hub, or by anything downloaded.
/// </summary>
public static class ReleaseSigning
{
    /// <summary>Name of the embedded resource a test build carries its own key in (see DevDash.Agent.csproj).</summary>
    public const string TestKeyResource = "devdash-update-test-public-key.pem";

    /// <summary>
    /// The release public key. <c>scripts/release-signing-key.sh</c> generates the key pair and writes the
    /// public half between the two marker lines here and in the install scripts. Until then this is a
    /// placeholder, and a build with the placeholder refuses to update itself.
    /// </summary>
    // devdash-release-public-key:begin
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXxcXOJGHjR/atn+NdoPztGKT+yXc
        k0aXqWNBvb+qZtPvX3TI9qA4SAugIBET7+aTgkiY5g/1pS+Wq4C1LdERDg==
        -----END PUBLIC KEY-----
        """;
    // devdash-release-public-key:end

    /// <summary>
    /// The key this build trusts. Always <see cref="PublicKeyPem"/>, unless the build was made with
    /// <c>-p:DevDashUpdatePublicKeyFile=&lt;pem&gt;</c>, which embeds a test key in its place. That is a
    /// compile-time choice used by the update tests in CI: there is no runtime setting, environment
    /// variable or hub message that changes the trusted key.
    /// </summary>
    public static ReleaseTrust Embedded { get; } = Load();

    private static ReleaseTrust Load()
    {
        using var resource = typeof(ReleaseSigning).Assembly.GetManifestResourceStream(TestKeyResource);
        if (resource is null)
        {
            return new ReleaseTrust(PublicKeyPem);
        }

        using var reader = new StreamReader(resource);
        return new ReleaseTrust(reader.ReadToEnd(), IsTestKey: true);
    }
}
