namespace Restate.Sdk.Testing.Containers;

/// <summary>
///     Options for <see cref="RestateTestHarness.StartAsync" />.
/// </summary>
public sealed class RestateTestHarnessOptions
{
    private string _image = RestateBuilder.RestateImage;
    private TimeSpan _startupTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Gets or sets the Restate Docker image to run.
    ///     Defaults to <see cref="RestateBuilder.RestateImage" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    public string Image
    {
        get => _image;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _image = value;
        }
    }

    /// <summary>
    ///     Gets or sets the maximum time allowed for the whole harness startup:
    ///     starting the SDK endpoint, starting the Restate container, and registering
    ///     the deployment. Defaults to two minutes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    public TimeSpan StartupTimeout
    {
        get => _startupTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _startupTimeout = value;
        }
    }
}
