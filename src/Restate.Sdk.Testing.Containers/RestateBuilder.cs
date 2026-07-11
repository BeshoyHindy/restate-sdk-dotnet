using System.Net;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;

namespace Restate.Sdk.Testing.Containers;

/// <summary>
///     Fluent builder for <see cref="RestateContainer" /> instances, following the
///     Testcontainers module idiom (compare <c>Testcontainers.PostgreSql</c>).
///     Both the ingress port (8080) and the admin port (9070) are bound to random
///     free host ports, and the container is considered ready once the admin
///     <c>/health</c> endpoint returns <c>200 OK</c>.
/// </summary>
public sealed class RestateBuilder : ContainerBuilder<RestateBuilder, RestateContainer, RestateConfiguration>
{
    /// <summary>The default Restate Docker image (pinned to a minor version, not <c>latest</c>).</summary>
    public const string RestateImage = "docker.io/restatedev/restate:1.7";

    /// <summary>The Restate ingress port inside the container.</summary>
    public const ushort IngressPort = 8080;

    /// <summary>The Restate admin API port inside the container.</summary>
    public const ushort AdminPort = 9070;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateBuilder" /> class
    ///     using the default image <see cref="RestateImage" />.
    /// </summary>
    public RestateBuilder()
        : this(RestateImage)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateBuilder" /> class.
    /// </summary>
    /// <param name="image">
    ///     The full Docker image name, including the image repository and tag
    ///     (e.g., <c>docker.io/restatedev/restate:1.7</c>).
    /// </param>
    /// <remarks>
    ///     Docker image tags available at <see href="https://hub.docker.com/r/restatedev/restate/tags" />.
    /// </remarks>
    public RestateBuilder(string image)
        : this(new DockerImage(image))
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateBuilder" /> class.
    /// </summary>
    /// <param name="image">
    ///     An <see cref="IImage" /> instance that specifies the Docker image to be used
    ///     for the container builder configuration.
    /// </param>
    public RestateBuilder(IImage image)
        : this(new RestateConfiguration())
    {
        DockerResourceConfiguration = Init().WithImage(image).DockerResourceConfiguration;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateBuilder" /> class.
    /// </summary>
    /// <param name="resourceConfiguration">The Docker resource configuration.</param>
    private RestateBuilder(RestateConfiguration resourceConfiguration)
        : base(resourceConfiguration)
    {
        DockerResourceConfiguration = resourceConfiguration;
    }

    /// <inheritdoc />
    protected override RestateConfiguration DockerResourceConfiguration { get; }

    /// <inheritdoc />
    public override RestateContainer Build()
    {
        Validate();
        return new RestateContainer(DockerResourceConfiguration);
    }

    /// <inheritdoc />
    protected override RestateBuilder Init()
    {
        return base.Init()
            .WithPortBinding(IngressPort, true)
            .WithPortBinding(AdminPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request
                    .ForPort(AdminPort)
                    .ForPath("/health")
                    .ForStatusCode(HttpStatusCode.OK)));
    }

    /// <inheritdoc />
    protected override RestateBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
    {
        return Merge(DockerResourceConfiguration, new RestateConfiguration(resourceConfiguration));
    }

    /// <inheritdoc />
    protected override RestateBuilder Clone(IContainerConfiguration resourceConfiguration)
    {
        return Merge(DockerResourceConfiguration, new RestateConfiguration(resourceConfiguration));
    }

    /// <inheritdoc />
    protected override RestateBuilder Merge(RestateConfiguration oldValue, RestateConfiguration newValue)
    {
        return new RestateBuilder(new RestateConfiguration(oldValue, newValue));
    }
}
