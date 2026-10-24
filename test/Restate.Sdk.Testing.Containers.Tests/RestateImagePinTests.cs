using System.Text.RegularExpressions;

namespace Restate.Sdk.Testing.Containers.Tests;

/// <summary>
///     <c>docker-compose.yml</c> is the single source of truth for the Restate server image:
///     <see cref="RestateBuilder.RestateImage" /> is generated from its <c>image:</c> line at build
///     time, and <c>.github/scripts/integration-test.sh</c> greps the same line. These tests fail as
///     soon as a consumer carries a version of its own again. They only read files, so they need no
///     Docker daemon.
/// </summary>
public sealed class RestateImagePinTests
{
    /// <summary>
    ///     Matches a pinned Restate image; mirrors the pattern the CI script greps with.
    ///     <c>docker.restate.dev</c> is the registry the Restate docs and release notes name, and
    ///     Docker Hub mirrors the same tags, so an override to <c>docker.io</c> stays valid.
    /// </summary>
    private const string ImagePattern = "(docker\\.restate\\.dev|docker\\.io)/restatedev/restate:[^\"\\s]+";

    /// <summary>Matches a Restate image literal that names a version.</summary>
    private const string VersionedImagePattern = "restatedev/restate:[0-9]";

    [Fact]
    public void Builder_image_comes_from_docker_compose()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot(), "docker-compose.yml"));
        var match = Regex.Match(compose, ImagePattern);

        Assert.True(
            match.Success,
            "docker-compose.yml names no pinned docker.restate.dev/restatedev/restate image.");
        Assert.Equal(RestateBuilder.RestateImage, match.Value);
    }

    [Theory]
    [InlineData("docker.restate.dev/restatedev/restate:1.8")]
    [InlineData("docker.io/restatedev/restate:1.8")]
    public void Pin_pattern_accepts_both_registries(string image)
    {
        var match = Regex.Match($"    image: {image}\n", ImagePattern);

        Assert.True(match.Success, $"The pin pattern rejects {image}.");
        Assert.Equal(image, match.Value);
    }

    [Theory]
    [InlineData("docker.restate.dev/restatedev/restate")]
    [InlineData("docker.io/restatedev/restate")]
    public void Pin_pattern_rejects_an_untagged_image(string image)
    {
        Assert.False(
            Regex.IsMatch($"    image: {image}\n", ImagePattern),
            $"The pin pattern accepts the untagged {image}, so the server version would float.");
    }

    [Fact]
    public void IntegrationTestScript_reads_the_pin_from_docker_compose()
    {
        var script = File.ReadAllText(
            Path.Combine(RepoRoot(), ".github", "scripts", "integration-test.sh"));

        Assert.False(
            Regex.IsMatch(script, VersionedImagePattern),
            "integration-test.sh pins a Restate version of its own; it must read docker-compose.yml.");
        Assert.Contains("docker-compose.yml", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicApiListing_records_no_server_version()
    {
        var listing = Path.Combine(RepoRoot(), "src", "Restate.Sdk.Testing.Containers");
        var shipped = File.ReadAllText(Path.Combine(listing, "PublicAPI.Shipped.txt"));
        var unshipped = File.ReadAllText(Path.Combine(listing, "PublicAPI.Unshipped.txt"));

        // The analyzer records a const's value, so listing RestateImage would make every
        // server bump a public API edit and fail a compose-only Dependabot pull request.
        Assert.False(
            Regex.IsMatch(shipped + unshipped, VersionedImagePattern),
            "The public API listing records the server version; a compose bump would then have to edit it too.");
    }

    [Fact]
    public void Workflows_and_guides_name_no_version_of_their_own()
    {
        var root = RepoRoot();
        var files = Directory
            .EnumerateFiles(Path.Combine(root, ".github"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs", "guide"), "*", SearchOption.AllDirectories));

        var offenders = files
            .Where(file => Regex.IsMatch(File.ReadAllText(file), VersionedImagePattern))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These files name a Restate server version instead of deferring to the pin: {string.Join(", ", offenders)}");
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
