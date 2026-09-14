using System.Text.RegularExpressions;

namespace Restate.Sdk.Testing.Containers.Tests;

/// <summary>
///     The Restate server image is pinned once, in <see cref="RestateBuilder.RestateImage" />.
///     <c>.github/scripts/integration-test.sh</c> reads that constant out of the source file;
///     <c>docker-compose.yml</c> cannot (it is consumed by <c>docker compose up</c> directly) and
///     repeats the literal. These tests fail as soon as either consumer drifts from the constant.
///     They only compare files, so they need no Docker daemon.
/// </summary>
public sealed class RestateImagePinTests
{
    /// <summary>Matches a pinned Restate image; mirrors the pattern the CI script greps with.</summary>
    private const string ImagePattern = "docker\\.io/restatedev/restate:[^\"\\s]+";

    [Fact]
    public void DockerCompose_names_the_pinned_image()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        var match = Regex.Match(compose, ImagePattern);

        Assert.True(match.Success, "docker-compose.yml names no restatedev/restate image.");
        Assert.Equal(RestateBuilder.RestateImage, match.Value);
    }

    [Fact]
    public void IntegrationTestScript_reads_the_pin_from_the_builder()
    {
        var script = File.ReadAllText(
            Path.Combine(RepoRoot(), ".github", "scripts", "integration-test.sh"));

        Assert.False(
            Regex.IsMatch(script, "restatedev/restate:[0-9]"),
            "integration-test.sh pins a Restate version of its own; it must read RestateBuilder.RestateImage.");
        Assert.Contains("RestateBuilder.cs", script, StringComparison.Ordinal);

        // The value the script greps for has to be the constant it claims to read.
        var builderSource = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Restate.Sdk.Testing.Containers", "RestateBuilder.cs"));
        var match = Regex.Match(builderSource, ImagePattern);

        Assert.True(match.Success, "RestateBuilder.cs no longer spells the image out; the script cannot read it.");
        Assert.Equal(RestateBuilder.RestateImage, match.Value);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Restate.Sdk.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
