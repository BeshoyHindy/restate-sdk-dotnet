using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal.Context;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.Serde;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal;

/// <summary>
///     Endpoint-wide invocation settings resolved once at startup and shared by every /invoke. Registered
///     as a singleton from the host's <c>RestateOptions</c> (composition root) so the per-request endpoint
///     handler can forward them to <see cref="InvocationHandler.HandleAsync" /> without the SM ever
///     touching the DI container. Today it carries only G13's global strict-payload flag; defaults preserve
///     historical behavior when no host opted in.
/// </summary>
internal sealed record RestateInvocationOptions(bool StrictPayloadChecks)
{
    /// <summary>The behavior-preserving default (strict payload checks OFF == PayloadChecksDisabled).</summary>
    public static readonly RestateInvocationOptions Default = new(StrictPayloadChecks: false);
}

internal sealed class InvocationHandler
{
    private static readonly ActivitySource ActivitySource = new("Restate.Sdk");
    private readonly ILoggerFactory _loggerFactory;

    public InvocationHandler(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task HandleAsync(
        PipeReader requestBodyReader,
        PipeWriter responseBodyWriter,
        ServiceDefinition service,
        HandlerDefinition handler,
        IServiceProvider serviceProvider,
        CancellationToken ct,
        int negotiatedProtocolVersion = Protocol.ProtocolVersion.MaximumSupported,
        bool strictPayloadChecks = false)
    {
        var logger = _loggerFactory.CreateLogger("Restate.Invocation");
        var jsonOptions = JsonSerde.SerializerOptions;
        using var reader = new ProtocolReader(requestBodyReader);
        using var writer = new ProtocolWriter(responseBodyWriter);

        // G12: the host validated the request Content-Type's protocol version is within [V5,V6] and
        // passes the negotiated number here so V6-gated features (Failure.metadata) can check it.
        using var sm = new InvocationStateMachine(reader, writer, jsonOptions, logger)
        {
            NegotiatedProtocolVersion = negotiatedProtocolVersion,
            // G13: the host fills this from RestateOptions.PayloadReplayChecks == Strict. Default false
            // keeps the composition root at the edge — business logic stays unaware of the knob.
            StrictPayloadChecks = strictPayloadChecks
        };
        using var incomingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? incomingTask = null;
        Activity? activity = null;

        try
        {
            var startInfo = await sm.StartAsync(ct).ConfigureAwait(false);

            Log.InvocationStarted(logger, service.Name, handler.Name, startInfo.InvocationId);

            activity = StartActivity(service, handler, sm.Headers, startInfo.InvocationId);

            incomingTask = sm.ProcessIncomingMessagesAsync(incomingCts.Token);

            // Use the linked token so the handler cancels when ANY of:
            // (a) the external caller cancels, (b) the incoming reader detects connection close, or
            // (c) an inbound CANCEL signal (idx=1) fires sm.CancelToken. (c) is the durable-cancel
            // path: a parked await unwinds via its faulted TCS (TerminalException 409), while
            // non-awaiting (CPU/loop) handler code observes cancel through this linked token and stops
            // cooperatively — both converge on the same terminal cancel Output.
            using var handlerCts = CancellationTokenSource.CreateLinkedTokenSource(incomingCts.Token, sm.CancelToken);
            var handlerToken = handlerCts.Token;
            var context = CreateContext(sm, service.Type, handler.IsShared, logger, handlerToken);

            object? input = null;
            if (handler.InputDeserializer is not null)
                input = handler.InputDeserializer(new ReadOnlySequence<byte>(startInfo.Input));

            var serviceInstance = service.Factory(serviceProvider);
            object? result;
            try
            {
                result = await handler.Invoker(serviceInstance, context, input, handlerToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sm.IsCancellationRequested)
            {
                // SM-INTERNAL cancel (inbound CANCEL, not the external ct): non-awaiting handler code
                // (a CPU loop observing handlerToken) threw OCE rather than a TerminalException.
                // Translate it into the SAME 409 terminal cancel signal the parked-await and Run paths
                // already use, so it unwinds through the single proven `catch (TerminalException)` arm
                // below — one terminal cancel path, no separate wire-writing arm to cover.
                throw new TerminalException("cancelled", InvocationStateMachine.CancelledStatusCode);
            }

            // The result is serialized INSIDE CompleteAsync's _commandLock so it can no longer be
            // torn by a straggler Run proposal sharing _serializeBuffer (2.7.5/2.8).
            await sm.CompleteAsync(result, ct).ConfigureAwait(false);

            Log.InvocationCompleted(logger, sm.InvocationId);
        }
        catch (SuspendedException)
        {
            // The state machine already wrote the SuspensionMessage and closed the output
            // (protocol.proto:88-97). No Output/Error frame may follow. This arm MUST precede every
            // wire-writing arm so a suspension never gets an Error frame written after it.
            Log.InvocationSuspended(logger, sm.InvocationId);
        }
        catch (TerminalException ex)
        {
            // A parked durable await faulted with TerminalException unwinds here. Inbound CANCEL uses
            // the SAME arm (TerminalException 409 "cancelled" — from a faulted parked await, a Run-path
            // OCE→Terminal translation, or the CPU-loop OCE translation above), so an inbound-cancelled
            // handler emits its terminal OutputCommand{failure:409} + End through this exact proven
            // path. If user code instead CATCHES the 409 and returns normally, CompleteAsync already
            // wrote a normal Output — cooperative-cancellation parity with Rust.
            Log.TerminalException(logger, sm.InvocationId, ex.Code);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // G11: thread the terminal error's V6 metadata onto the OutputCommand failure (the SM
            // gates emission on the negotiated protocol version).
            try { await sm.FailTerminalAsync(ex.Code, ex.Message, CancellationToken.None, ex.Metadata).ConfigureAwait(false); }
            catch { /* Stream already broken — nothing more we can do */ }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.InvocationCancelled(logger, sm.InvocationId);
        }
        catch (ProtocolException ex)
        {
            // G9: surface the protocol-violation code — 570 (JOURNAL_MISMATCH) for a journal/command
            // mismatch, 571 (PROTOCOL_VIOLATION) for any other violation — instead of collapsing to
            // 500. Matches shared-core's per-error code mapping (vm/errors.rs impl_error_code!).
            Log.ProtocolError(logger, ex, sm.InvocationId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // G21/G22: pass the exception stacktrace so the ErrorMessage carries it (Rust sets
            // ErrorMessage.stacktrace), alongside the related_command_index the SM derives.
            try { await sm.FailAsync(ex.Code, ex.Message, CancellationToken.None, stacktrace: ex.StackTrace).ConfigureAwait(false); }
            catch { /* Stream already broken */ }
        }
        catch (RestateRetryableException ex)
        {
            // Retryable failure with an optional next-retry-delay override — the runtime re-invokes.
            Log.InvocationFailed(logger, ex, sm.InvocationId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            try { await sm.FailAsync(ex.Code, ex.Message, CancellationToken.None, ex.NextRetryDelay, ex.StackTrace).ConfigureAwait(false); }
            catch { /* Stream already broken */ }
        }
        catch (RunRedriveException ex)
        {
            // G15/G16/G17 — a non-terminal ctx.Run failure under an unbounded (Infinite) policy asks the
            // RUNTIME to re-drive the invocation (shared-core ProposeRunCompletion → Err(error), the
            // journal then replays). Emit a retryable Error frame carrying the policy-derived next-retry-
            // delay (null = defer to the invoker), mirroring error.next_retry_delay (journal.rs:757-758).
            Log.InvocationFailed(logger, ex, sm.InvocationId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            try { await sm.FailAsync(500, ex.Reason, CancellationToken.None, ex.NextRetryDelay, ex.StackTrace).ConfigureAwait(false); }
            catch { /* Stream already broken */ }
        }
        catch (Exception ex)
        {
            Log.InvocationFailed(logger, ex, sm.InvocationId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            try { await sm.FailAsync(500, ex.Message, CancellationToken.None, stacktrace: ex.StackTrace).ConfigureAwait(false); }
            catch { /* Stream already broken */ }
        }
        finally
        {
            activity?.Dispose();

            // Each cleanup operation is wrapped in try-catch because the stream may already
            // be in a broken state. Without this, a failure in any cleanup step would prevent
            // the remaining cleanup from executing AND propagate an exception to Kestrel.
            try { writer.Complete(); } catch { /* already completed or broken */ }

            // Cancel and await the incoming reader task BEFORE completing the reader.
            // This avoids a "Concurrent reads or writes are not supported" race between
            // PipeReader.Complete() and ProcessIncomingMessagesAsync's pending ReadAsync().
            try { await incomingCts.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
            if (incomingTask is not null)
                try
                {
                    await incomingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    Log.IncomingReaderStopped(logger, ex, sm.InvocationId);
                }
                catch (Exception ex)
                {
                    // Pump fault (ProtocolException, parse/IO error) already surfaced to the handler
                    // through the faulted TCSs (FailAll) and was reported via the catch arms above —
                    // log only, never rethrow from finally into Kestrel after the response ended.
                    Log.IncomingReaderFaulted(logger, ex, sm.InvocationId);
                }

            try { reader.Complete(); } catch { /* already completed or broken */ }
        }
    }

    private static Activity? StartActivity(
        ServiceDefinition service, HandlerDefinition handler,
        IReadOnlyDictionary<string, string> headers, string invocationId)
    {
        ActivityContext parentContext = default;

        if (headers.TryGetValue("traceparent", out var traceparent))
        {
            headers.TryGetValue("tracestate", out var tracestate);
            ActivityContext.TryParse(traceparent, tracestate, out parentContext);
        }

        var activity = ActivitySource.StartActivity(
            $"{service.Name}/{handler.Name}",
            ActivityKind.Server,
            parentContext);

        if (activity is not null)
        {
            activity.SetTag("restate.invocation.id", invocationId);
            activity.SetTag("rpc.service", service.Name);
            activity.SetTag("rpc.method", handler.Name);
            activity.SetTag("rpc.system", "restate");
        }

        return activity;
    }

    private static Restate.Sdk.Context CreateContext(
        InvocationStateMachine sm, ServiceType serviceType, bool isShared, ILogger logger, CancellationToken ct)
    {
        return serviceType switch
        {
            ServiceType.Service => new DefaultContext(sm, logger, ct),
            ServiceType.VirtualObject => isShared
                ? new DefaultSharedObjectContext(sm, logger, ct)
                : new DefaultObjectContext(sm, logger, ct),
            ServiceType.Workflow => isShared
                ? new DefaultSharedWorkflowContext(sm, logger, ct)
                : new DefaultWorkflowContext(sm, logger, ct),
            _ => new DefaultContext(sm, logger, ct)
        };
    }
}