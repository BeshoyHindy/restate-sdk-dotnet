using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Restate.Sdk.Hosting;

/// <summary>
///     Extension methods on <see cref="IWebHostBuilder" /> for configuring Kestrel
///     to host Restate services. Use this when building an ASP.NET Core app manually
///     instead of using <see cref="RestateHostBuilder" />.
/// </summary>
public static class RestateWebHostBuilderExtensions
{
    /// <summary>
    ///     Configures Kestrel for hosting Restate services: HTTP/2, disabled rate limits,
    ///     and increased flow-control windows for bidirectional streaming.
    /// </summary>
    /// <param name="webHostBuilder">The web host builder.</param>
    /// <param name="port">The port to listen on. Default is 9080.</param>
    public static IWebHostBuilder ConfigureRestate(this IWebHostBuilder webHostBuilder, int port = 9080)
    {
        webHostBuilder.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listenOptions => { listenOptions.Protocols = HttpProtocols.Http2; });

            // Restate uses bidirectional streaming: the runtime sends the
            // StartMessage then pauses while the SDK processes and sends commands.
            // Kestrel's default MinRequestBodyDataRate would abort these pauses.
            options.Limits.MinRequestBodyDataRate = null;

            // Replay journals can be large for long-running workflows.
            // Remove the default 30 MB limit to avoid truncation.
            options.Limits.MaxRequestBodySize = null;

            // Increase HTTP/2 flow control windows for faster streaming throughput.
            options.Limits.Http2.InitialConnectionWindowSize = 1024 * 1024; // 1 MB
            options.Limits.Http2.InitialStreamWindowSize = 512 * 1024; // 512 KB
        });

        return webHostBuilder;
    }
}
