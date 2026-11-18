using DevDash.Contracts;
using DevDash.Core.GitHub;

namespace DevDash.Hub.Services;

public sealed record RepoDescription(string Text, string Source);

public sealed record DescriptionRequest(string RepoKey, GitHubRepo? GitHub, IReadOnlyList<DevEntry> Clones);

/// <summary>
/// Produces the one-line description shown for a repo. This is the seam for phase 5, where a
/// Claude-backed provider can be put in front for repos that have neither a GitHub description nor a README.
/// </summary>
public interface IDescriptionProvider
{
    ValueTask<RepoDescription?> DescribeAsync(DescriptionRequest request, CancellationToken ct = default);
}

/// <summary>GitHub description first, then the README's first paragraph, then a manifest description.</summary>
public sealed class DefaultDescriptionProvider : IDescriptionProvider
{
    public ValueTask<RepoDescription?> DescribeAsync(DescriptionRequest request, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.GitHub?.Description))
        {
            return new(new RepoDescription(request.GitHub.Description.Trim(), "GitHub"));
        }

        var readme = request.Clones
            .Select(c => c.Description?.ReadmeExcerpt)
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
        if (readme is not null)
        {
            return new(new RepoDescription(readme, "README"));
        }

        var manifest = request.Clones
            .SelectMany(c => c.Description?.Manifests ?? [])
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Description));
        return new(manifest is null ? null : new RepoDescription(manifest.Description!, manifest.File));
    }
}
