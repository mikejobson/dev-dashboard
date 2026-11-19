using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DevDash.Agent;
using DevDash.Agent.Service;
using DevDash.Agent.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Tests.Shared;

/// <summary>
/// A pretend GitHub Releases site, served from memory, signed with a key pair made for the test.
/// The "programs" in it are short text files; <see cref="FakeProbe"/> stands in for running them.
/// Shared by the agent tests and the hub's end-to-end test (linked into DevDash.Hub.Tests).
/// </summary>
public sealed class FakeRelease : HttpMessageHandler
{
    public const string BaseUrl = "https://releases.test/acme/releases";

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ReleaseTrust Trust => new(Key.ExportSubjectPublicKeyInfoPem());

    /// <summary>Every URL that was asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    public static byte[] Program(string version, string name = "devdash-agent") => Encoding.UTF8.GetBytes($"fake {name} {version}\n");

    /// <summary>
    /// Publishes a release for one platform, the way the release workflow does: the archive, version.txt,
    /// checksums.txt over both, and the signature over checksums.txt.
    /// </summary>
    /// <param name="tamper">Changes the files after they were signed (name to bytes), to play the attacker.</param>
    /// <param name="signWith">Sign with another key, to play a release from someone else.</param>
    /// <param name="servedAs">Serve the files under this version's URL instead (a replayed release).</param>
    public void Publish(string version, UpdateInstallation installation, bool latest = true, Action<Dictionary<string, byte[]>>? tamper = null,
        ECDsa? signWith = null, string? servedAs = null, Func<string, byte[]>? program = null)
    {
        program ??= name => Program(version, name);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [installation.AssetName] = Archive(installation, program),
            [AgentUpdater.VersionFile] = Encoding.ASCII.GetBytes(version + "\n"),
        };
        var checksums = string.Concat(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{ReleaseVerifier.Sha256Hex(f.Value)}  {f.Key}\n"));
        files[AgentUpdater.ChecksumsFile] = Encoding.ASCII.GetBytes(checksums);
        files[AgentUpdater.SignatureFile] = (signWith ?? Key).SignData(files[AgentUpdater.ChecksumsFile], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        tamper?.Invoke(files);

        foreach (var (name, bytes) in files)
        {
            _files[$"/acme/releases/download/v{servedAs ?? version}/{name}"] = bytes;
            if (latest)
            {
                _files[$"/acme/releases/latest/download/{name}"] = bytes;
            }
        }
    }

    public static byte[] Archive(UpdateInstallation installation, Func<string, byte[]> program)
    {
        using var buffer = new MemoryStream();
        if (installation.Platform == ServicePlatform.Windows)
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var name in installation.FileNames)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(program(name));
            }
        }
        else
        {
            using var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true);
            using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
            // Something that must be ignored: only the program itself is ever unpacked.
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../escape.sh") { DataStream = new MemoryStream("#!/bin/sh\n"u8.ToArray()) });
            foreach (var name in installation.FileNames)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(program(name)) });
            }
        }

        return buffer.ToArray();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.ToString());
        var response = request.RequestUri.Host == "releases.test" && _files.TryGetValue(request.RequestUri.AbsolutePath, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    /// <summary>An updater wired to this site, for an installation in a temporary folder.</summary>
    public AgentUpdater Updater(UpdateInstallation installation, string currentVersion, AgentOptions? options = null, IUpdateFiles? files = null,
        IBinaryProbe? probe = null, ReleaseTrust? trust = null, TimeProvider? time = null)
    {
        options ??= new AgentOptions();
        options.Updates.BaseUrl ??= BaseUrl;
        return new AgentUpdater(new HttpClient(this), new TestOptions(options), new UpdateContext(installation, null, currentVersion, trust ?? Trust),
            probe ?? new FakeProbe(), files ?? new UpdateFiles(), time ?? TimeProvider.System, NullLogger<AgentUpdater>.Instance);
    }

    /// <summary>A folder holding an "installed" agent of the given version.</summary>
    public static UpdateInstallation Install(string directory, string version, ServicePlatform platform = ServicePlatform.Linux)
    {
        var installation = new UpdateInstallation
        {
            Platform = platform,
            Directory = directory,
            Rid = platform switch { ServicePlatform.Windows => "win-x64", ServicePlatform.MacOS => "osx-arm64", _ => "linux-x64" },
        };
        Directory.CreateDirectory(directory);
        foreach (var name in installation.FileNames)
        {
            File.WriteAllBytes(installation.PathOf(name), Program(version, name));
        }

        return installation;
    }

    /// <summary>The version of the fake program at <paramref name="path"/>, as <see cref="Program"/> wrote it.</summary>
    public static string VersionAt(string path) => File.ReadAllText(path).Trim().Split(' ')[^1];
}

/// <summary>"Runs" a fake program: prints what a real one would for <c>--version</c>, or fails like a broken one.</summary>
public sealed class FakeProbe : IBinaryProbe
{
    public List<string> Ran { get; } = [];

    public Task<ProbeResult> VersionAsync(string path, CancellationToken ct)
    {
        Ran.Add(Path.GetFileName(path));
        var text = File.ReadAllText(path).Trim();
        return Task.FromResult(text.StartsWith("fake ", StringComparison.Ordinal)
            ? new ProbeResult(0, $"devdash-agent {text.Split(' ')[^1]}")
            : new ProbeResult(134, "Abort trap"));
    }
}

public sealed class TestOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
{
    public AgentOptions CurrentValue => value;

    public AgentOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
}

public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
