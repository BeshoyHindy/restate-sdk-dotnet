namespace Restate.Sdk.Testing.Containers;

/// <summary>
///     Options for <see cref="RestateTestHarness.StartAsync" />.
/// </summary>
public sealed class RestateTestHarnessOptions
{
    /// <summary>
    ///     Gets or sets the Restate Docker image to run.
    ///     Defaults to <see cref="RestateBuilder.RestateImage" />.
    /// </summary>
    public string Image { get; set; } = RestateBuilder.RestateImage;

    /// <summary>
    ///     Gets or sets the maximum time allowed for the whole harness startup:
    ///     starting the SDK endpoint, starting the Restate container, and registering
    ///     the deployment. Defaults to two minutes.
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
