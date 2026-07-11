using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;

namespace Restate.Sdk.Testing.Containers;

/// <inheritdoc cref="ContainerConfiguration" />
public sealed class RestateConfiguration : ContainerConfiguration
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateConfiguration" /> class.
    /// </summary>
    public RestateConfiguration()
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateConfiguration" /> class.
    /// </summary>
    /// <param name="resourceConfiguration">The Docker resource configuration.</param>
    public RestateConfiguration(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        : base(resourceConfiguration)
    {
        // Passes the configuration upwards to the base implementations to create an updated immutable copy.
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateConfiguration" /> class.
    /// </summary>
    /// <param name="resourceConfiguration">The Docker resource configuration.</param>
    public RestateConfiguration(IContainerConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
        // Passes the configuration upwards to the base implementations to create an updated immutable copy.
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateConfiguration" /> class.
    /// </summary>
    /// <param name="resourceConfiguration">The Docker resource configuration.</param>
    public RestateConfiguration(RestateConfiguration resourceConfiguration)
        : this(new RestateConfiguration(), resourceConfiguration)
    {
        // Passes the configuration upwards to the base implementations to create an updated immutable copy.
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateConfiguration" /> class.
    /// </summary>
    /// <param name="oldValue">The old Docker resource configuration.</param>
    /// <param name="newValue">The new Docker resource configuration.</param>
    public RestateConfiguration(RestateConfiguration oldValue, RestateConfiguration newValue)
        : base(oldValue, newValue)
    {
        // Passes the configuration upwards to the base implementations to create an updated immutable copy.
    }
}
