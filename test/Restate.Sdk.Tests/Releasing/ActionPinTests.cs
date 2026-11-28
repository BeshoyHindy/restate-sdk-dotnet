using System.Text.RegularExpressions;

namespace Restate.Sdk.Tests.Releasing;

/// <summary>
///     A tag such as <c>actions/checkout@v7</c> can be moved to different code at any time, and every
///     workflow picks the new code up on its next run, including the publish job that holds the NuGet
///     key. So each <c>uses:</c> names an action by the full commit SHA, with the release it came from
///     as a trailing comment that Dependabot reads and rewrites when it bumps the pin. Local actions
///     (<c>./</c>) and <c>docker://</c> images are not GitHub refs and are left alone. This test only
///     reads the workflow files.
/// </summary>
public sealed partial class ActionPinTests
{
    [Fact]
    public void Every_action_is_pinned_to_a_commit_sha_with_its_release_as_a_comment()
    {
        var root = RepositoryLayout.RepoRoot();
        var workflows = Directory.GetFiles(Path.Combine(root, ".github", "workflows"), "*.yml");
        Assert.NotEmpty(workflows);

        var offenders = new List<string>();
        foreach (var file in workflows)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var uses = UsesLine().Match(lines[i]);
                if (!uses.Success)
                    continue;

                var reference = uses.Groups["ref"].Value;
                if (reference.StartsWith("./", StringComparison.Ordinal) ||
                    reference.StartsWith("docker://", StringComparison.Ordinal))
                    continue;

                if (!PinnedReference().IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These actions are not pinned as 'owner/repo@<40-hex sha> # vX.Y.Z':" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [GeneratedRegex(@"^\s*(?:-\s+)?uses:\s*(?<ref>\S+)")]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"uses:\s*[\w.-]+/[\w./-]+@[0-9a-f]{40}\s+#\s*v\d+\.\d+\.\d+\s*$")]
    private static partial Regex PinnedReference();
}
