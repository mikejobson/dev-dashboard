using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Untracked files that look like secrets and that .gitignore does not cover.</summary>
public sealed class UntrackedSecretsRule : ICleanupRule
{
    public string Id => "untracked-secrets";
    public string Title => "Secret-looking files not in .gitignore";
    public string Description => "Untracked files named like secrets (.env, *.pem, *.key, id_rsa…) that nothing stops a `git add .` from committing. Found by name; DevDash never reads them.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var clone in context.Clones())
        {
            if (clone.Git.Safety is not { SecretFileCount: > 0 } facts)
            {
                continue;
            }

            var names = facts.SecretFiles;
            var more = facts.SecretFileCount - names.Count;
            var path = clone.Entry.Path;

            // A leading slash anchors each line to this one file, so the fix cannot hide anything else.
            var patterns = names.Select(n => "/" + n.Replace('\\', '/')).ToList();
            yield return this.ForClone(clone, FindingSeverity.Medium,
                $"{clone.Entry.Name}: {Plural(facts.SecretFileCount, "secret-looking file")} not in .gitignore",
                $"{string.Join(", ", names.Take(5))}{(facts.SecretFileCount > 5 ? $" and {facts.SecretFileCount - Math.Min(5, names.Count)} more" : "")} "
                + $"{(facts.SecretFileCount == 1 ? "is" : "are")} untracked and not ignored, so one `git add .` would commit {(facts.SecretFileCount == 1 ? "it" : "them")}. "
                + "Add the names to .gitignore, or move the files out of the repo."
                + (more > 0 ? $" Only the first {names.Count} are listed." : ""),
                [
                    Inspect("Show them in git status", GitCmd(path, "status --short -- " + string.Join(' ', names.Select(Quote)))),
                    Fix("Add them to .gitignore", $"printf '%s\\n' {string.Join(' ', patterns.Select(Quote))} >> {Quote(path.TrimEnd('/', '\\') + "/.gitignore")}"),
                ],
                [.. names.Order(StringComparer.Ordinal)]);
        }
    }
}
