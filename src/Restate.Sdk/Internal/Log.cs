using Microsoft.Extensions.Logging;
using Restate.Sdk.Internal.Protocol;

namespace Restate.Sdk.Internal;

internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Invocation started: service={Service}, handler={Handler}, invocationId={InvocationId}")]
    public static partial void InvocationStarted(ILogger logger, string service, string handler, string invocationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Invocation completed: invocationId={InvocationId}")]
    public static partial void InvocationCompleted(ILogger logger, string invocationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Invocation failed: invocationId={InvocationId}")]
    public static partial void InvocationFailed(ILogger logger, Exception exception, string invocationId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "Replay started: invocationId={InvocationId}, knownEntries={KnownEntries}")]
    public static partial void ReplayStarted(ILogger logger, string invocationId, int knownEntries);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "Replay completed: invocationId={InvocationId}")]
    public static partial void ReplayCompleted(ILogger logger, string invocationId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "Side effect executed: name={Name}, invocationId={InvocationId}")]
    public static partial void SideEffectExecuted(ILogger logger, string name, string invocationId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "Terminal exception: invocationId={InvocationId}, code={Code}")]
    public static partial void TerminalException(ILogger logger, string invocationId, ushort code);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Protocol error: invocationId={InvocationId}")]
    public static partial void ProtocolError(ILogger logger, Exception exception, string invocationId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug,
        Message = "Invocation cancelled by client: invocationId={InvocationId}")]
    public static partial void InvocationCancelled(ILogger logger, string invocationId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Trace,
        Message = "Incoming message reader stopped: invocationId={InvocationId}")]
    public static partial void IncomingReaderStopped(ILogger logger, Exception exception, string invocationId);

    // --- Protocol diagnostics (Trace level, zero-overhead when disabled) ---

    [LoggerMessage(EventId = 100, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Reading message from request stream...")]
    public static partial void ReadingMessage(ILogger logger, string invocationId);

    [LoggerMessage(EventId = 101, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Read message: type={MessageType}, length={Length}")]
    public static partial void MessageRead(ILogger logger, string invocationId, MessageType messageType, uint length);

    [LoggerMessage(EventId = 102, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Stream ended (no more messages)")]
    public static partial void StreamEnded(ILogger logger, string invocationId);

    [LoggerMessage(EventId = 103, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Writing command: type={MessageType}, payloadLength={PayloadLength}")]
    public static partial void WritingCommand(ILogger logger, string invocationId, MessageType messageType, int payloadLength);

    [LoggerMessage(EventId = 104, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Flushing response stream...")]
    public static partial void Flushing(ILogger logger, string invocationId);

    [LoggerMessage(EventId = 105, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Flush completed")]
    public static partial void FlushCompleted(ILogger logger, string invocationId);

    [LoggerMessage(EventId = 106, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Awaiting completion for entry {EntryIndex}")]
    public static partial void AwaitingCompletion(ILogger logger, string invocationId, int entryIndex);

    [LoggerMessage(EventId = 107, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Completion received for entry {EntryIndex}")]
    public static partial void CompletionReceived(ILogger logger, string invocationId, int entryIndex);

    [LoggerMessage(EventId = 108, Level = LogLevel.Trace,
        Message = "[{InvocationId}] Notification: type={NotificationType}, entryIndex={EntryIndex}, isFailure={IsFailure}")]
    public static partial void NotificationReceived(ILogger logger, string invocationId, MessageType notificationType, int entryIndex, bool isFailure);

    [LoggerMessage(EventId = 109, Level = LogLevel.Trace,
        Message = "[{InvocationId}] State transition: {OldState} -> {NewState}")]
    public static partial void StateTransition(ILogger logger, string invocationId, string oldState, string newState);
}