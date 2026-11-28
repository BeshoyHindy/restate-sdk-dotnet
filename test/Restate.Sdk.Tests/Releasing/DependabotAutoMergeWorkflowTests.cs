using System.Text.RegularExpressions;

namespace Restate.Sdk.Tests.Releasing;

/// <summary>
///     <c>dependabot-auto-merge.yml</c> arms auto-merge with a credential that can write to
///     <c>main</c>, so two things are pinned here. The job runs only for pull requests Dependabot
///     opened, judged by the pull request's author rather than <c>github.actor</c>, which names
///     whoever caused the latest event and so can be Dependabot acting on a pull request someone
///     else opened. And a major bump waits for a maintainer in every
///     ecosystem, GitHub Actions included, since a new major of an action can change what it does
///     with the token it is handed. This test only reads the workflow file.
/// </summary>
public sealed class DependabotAutoMergeWorkflowTests
{
    private static string Workflow() => File.ReadAllText(
        Path.Combine(RepositoryLayout.RepoRoot(), ".github", "workflows", "dependabot-auto-merge.yml"));

    [Fact]
    public void Job_runs_only_for_pull_requests_authored_by_dependabot()
    {
        var condition = Regex.Match(Workflow(), @"^\s+if:\s*(?<condition>.+)$", RegexOptions.Multiline);
        Assert.True(condition.Success, "dependabot-auto-merge.yml no longer declares a job condition.");

        Assert.Equal(
            "github.event.pull_request.user.login == 'dependabot[bot]'",
            condition.Groups["condition"].Value.Trim());
    }

    [Theory]
    [InlineData("github_actions")]
    [InlineData("nuget")]
    [InlineData("docker_compose")]
    public void Only_minor_and_patch_updates_are_classified_low_risk(string ecosystem)
    {
        // Each ecosystem's branch of the classify step must gate on the update type.
        var branch = Regex.Match(
            Workflow(),
            $@"\[ ""\$ECOSYSTEM"" = ""{ecosystem}"" \](?<rest>[^\n]*\n[^\n]*\n[^\n]*)");
        Assert.True(branch.Success, $"The classify step no longer has a branch for {ecosystem}.");

        var gate = branch.Groups["rest"].Value;
        Assert.Contains("version-update:semver-minor", gate, StringComparison.Ordinal);
        Assert.Contains("version-update:semver-patch", gate, StringComparison.Ordinal);
    }
}
