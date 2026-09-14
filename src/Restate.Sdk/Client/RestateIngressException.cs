using System.Net;

namespace Restate.Sdk.Client;

/// <summary>
///     Thrown when a Restate ingress request returns a non-success status.
///     <para>
///         <see cref="ErrorSource" /> distinguishes a failure the ingress itself produced (for
///         example an unknown service, a rejected request, or an overload) from one produced by the
///         invocation it forwarded to — a handler that failed durably. It is read from the
///         <c>x-restate-error-source</c> response header, or from the <c>source</c> field of the
///         JSON error body, and is null when neither is present (restate-server before 1.7.4).
///     </para>
///     <para>
///         Derives from <see cref="HttpRequestException" />, so code that catches that (what
///         <see cref="HttpResponseMessage.EnsureSuccessStatusCode" /> used to throw here) keeps
///         working, and <see cref="HttpRequestException.StatusCode" /> carries the HTTP status.
///     </para>
/// </summary>
public sealed class RestateIngressException : HttpRequestException
{
    /// <summary>Creates an ingress exception carrying what the server reported about the failure.</summary>
    /// <param name="message">
    ///     The error message: the error body's <c>message</c> field, the raw body when it is not
    ///     JSON, or a description of the status code when the response had no body.
    /// </param>
    /// <param name="statusCode">The HTTP status code of the ingress response.</param>
    /// <param name="errorSource">The Restate error source, or null when the server did not report one.</param>
    /// <param name="errorCode">The Restate error code, or null when the server did not report one.</param>
    public RestateIngressException(string message, HttpStatusCode statusCode, string? errorSource, string? errorCode)
        : base(message, null, statusCode)
    {
        ErrorSource = errorSource;
        ErrorCode = errorCode;
    }

    /// <summary>
    ///     Where the failure came from — <c>ingress</c> for one the ingress produced itself,
    ///     and the invocation for one that came back from the handler. Null when the server did
    ///     not report a source. Named <c>ErrorSource</c> because <see cref="Exception.Source" />
    ///     already means the application or object that raised the exception.
    /// </summary>
    public string? ErrorSource { get; }

    /// <summary>
    ///     The Restate error code from the error body's <c>code</c> field, or null when the server
    ///     did not report one.
    /// </summary>
    public string? ErrorCode { get; }
}
