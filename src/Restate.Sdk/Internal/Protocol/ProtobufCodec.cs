using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Internal.Protocol;

/// <summary>
///     Adapter between Google.Protobuf generated classes and the SDK's internal types.
///     All protobuf encoding/decoding goes through this single class.
/// </summary>
[UnconditionalSuppressMessage("AOT",
    "IL2026:RequiresUnreferencedCode",
    Justification = "StateKeys JSON serialization uses string[] which is always safe for AOT.")]
[UnconditionalSuppressMessage("AOT",
    "IL3050:RequiresDynamicCode",
    Justification = "StateKeys JSON serialization uses string[] which is always safe for AOT.")]
[SkipLocalsInit]
internal static class ProtobufCodec
{
    // ── Serialization helpers ──────────────────────────────────────────

    /// <summary>
    ///     Calculates the serialized size of a protobuf message.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalculateSize(IMessage message) => message.CalculateSize();

    /// <summary>
    ///     Serializes a protobuf message into the given span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteTo(IMessage message, Span<byte> destination)
    {
        message.WriteTo(destination);
    }

    // ── Parsing (incoming messages → SDK types) ───────────────────────

    public static StartMessageFields ParseStartMessage(ReadOnlySpan<byte> payload)
    {
        var msg = Gen.StartMessage.Parser.ParseFrom(payload);

        Dictionary<string, ReadOnlyMemory<byte>>? eagerState = null;
        if (!msg.PartialState)
        {
            eagerState = new Dictionary<string, ReadOnlyMemory<byte>>(msg.StateMap.Count);
            foreach (var entry in msg.StateMap)
                eagerState[entry.Key.ToStringUtf8()] = entry.Value.Memory;
        }

        return new StartMessageFields(
            msg.Id.ToByteArray(),
            msg.DebugId,
            msg.Key.Length > 0 ? msg.Key : null,
            msg.KnownEntries,
            msg.RandomSeed,
            eagerState);
    }

    public static (ReadOnlyMemory<byte> Input, Dictionary<string, string>? Headers) ParseInputCommand(ReadOnlySpan<byte> payload)
    {
        var msg = Gen.InputCommandMessage.Parser.ParseFrom(payload);

        ReadOnlyMemory<byte> input = msg.Value is not null ? msg.Value.Content.Memory : ReadOnlyMemory<byte>.Empty;

        Dictionary<string, string>? headers = null;
        if (msg.Headers.Count > 0)
        {
            headers = new Dictionary<string, string>(msg.Headers.Count);
            foreach (var h in msg.Headers)
                headers[h.Key] = h.Value;
        }

        return (input, headers);
    }

    public static CompletionNotification ParseCompletionNotification(ReadOnlySpan<byte> payload)
    {
        // Use NotificationTemplate for unified parsing of all notification types.
        var n = Gen.NotificationTemplate.Parser.ParseFrom(payload);

        ReadOnlyMemory<byte>? value = null;
        ushort? failureCode = null;
        string? failureMessage = null;
        var isVoid = false;
        string? invocationId = null;

        switch (n.ResultCase)
        {
            case Gen.NotificationTemplate.ResultOneofCase.Void:
                isVoid = true;
                break;
            case Gen.NotificationTemplate.ResultOneofCase.Value:
                value = n.Value.Content.Memory;
                break;
            case Gen.NotificationTemplate.ResultOneofCase.Failure:
                failureCode = (ushort)n.Failure.Code;
                failureMessage = n.Failure.Message;
                break;
            case Gen.NotificationTemplate.ResultOneofCase.InvocationId:
                invocationId = n.InvocationId;
                break;
            case Gen.NotificationTemplate.ResultOneofCase.StateKeys:
                // BUG 4 FIX: Handle field 17 (StateKeys) — previously only field 5 (Value) was checked.
                // Convert protobuf StateKeys (repeated bytes) to JSON string[] for SDK consumption.
                var keys = new string[n.StateKeys.Keys.Count];
                for (var i = 0; i < keys.Length; i++)
                    keys[i] = n.StateKeys.Keys[i].ToStringUtf8();
                value = (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(keys);
                break;
        }

        return new CompletionNotification(n.CompletionId, value, failureCode, failureMessage, isVoid, invocationId);
    }

    public static SignalNotification ParseSignalNotification(ReadOnlySpan<byte> payload)
    {
        var msg = Gen.SignalNotificationMessage.Parser.ParseFrom(payload);

        uint? idx = null;
        string? name = null;
        ReadOnlyMemory<byte>? value = null;
        ushort? failureCode = null;
        string? failureMessage = null;
        var isVoid = false;

        if (msg.SignalIdCase == Gen.SignalNotificationMessage.SignalIdOneofCase.Idx)
            idx = msg.Idx;
        else if (msg.SignalIdCase == Gen.SignalNotificationMessage.SignalIdOneofCase.Name)
            name = msg.Name;

        switch (msg.ResultCase)
        {
            case Gen.SignalNotificationMessage.ResultOneofCase.Void:
                isVoid = true;
                break;
            case Gen.SignalNotificationMessage.ResultOneofCase.Value:
                value = msg.Value.Content.Memory;
                break;
            case Gen.SignalNotificationMessage.ResultOneofCase.Failure:
                failureCode = (ushort)msg.Failure.Code;
                failureMessage = msg.Failure.Message;
                break;
        }

        return new SignalNotification(idx, name, value, failureCode, failureMessage, isVoid);
    }

    // ── Factory methods for outgoing commands ─────────────────────────

    public static Gen.RunCommandMessage CreateRunCommand(string name, uint completionId)
    {
        var msg = new Gen.RunCommandMessage { ResultCompletionId = completionId };
        if (!string.IsNullOrEmpty(name)) msg.Name = name;
        return msg;
    }

    public static Gen.ProposeRunCompletionMessage CreateRunProposal(uint completionId, ReadOnlySpan<byte> value)
    {
        return new Gen.ProposeRunCompletionMessage
        {
            ResultCompletionId = completionId,
            Value = ByteString.CopyFrom(value)
        };
    }

    public static Gen.ProposeRunCompletionMessage CreateRunProposalFailure(uint completionId, uint code, string message)
    {
        return new Gen.ProposeRunCompletionMessage
        {
            ResultCompletionId = completionId,
            Failure = new Gen.Failure { Code = code, Message = message }
        };
    }

    /// <summary>
    ///     Creates a CallCommandMessage with all required fields including invocation_id_notification_idx.
    ///     BUG 1 FIX: Previously this field was missing, defaulting to 0.
    /// </summary>
    public static Gen.CallCommandMessage CreateCallCommand(
        string service, string handler, string? key,
        ReadOnlySpan<byte> parameter, uint completionId, uint invocationIdNotificationIdx)
    {
        var msg = new Gen.CallCommandMessage
        {
            ServiceName = service,
            HandlerName = handler,
            ResultCompletionId = completionId,
            InvocationIdNotificationIdx = invocationIdNotificationIdx
        };
        if (!parameter.IsEmpty) msg.Parameter = ByteString.CopyFrom(parameter);
        if (key is not null) msg.Key = key;
        return msg;
    }

    public static Gen.OneWayCallCommandMessage CreateSendCommand(
        string service, string handler, string? key,
        ReadOnlySpan<byte> parameter, ulong invokeTime, string? idempotencyKey, uint notificationIdx)
    {
        var msg = new Gen.OneWayCallCommandMessage
        {
            ServiceName = service,
            HandlerName = handler,
            InvocationIdNotificationIdx = notificationIdx
        };
        if (!parameter.IsEmpty) msg.Parameter = ByteString.CopyFrom(parameter);
        if (invokeTime > 0) msg.InvokeTime = invokeTime;
        if (key is not null) msg.Key = key;
        if (idempotencyKey is not null) msg.IdempotencyKey = idempotencyKey;
        return msg;
    }

    /// <summary>
    ///     Creates an OutputCommandMessage. Always sets the Value oneof even when content is empty.
    ///     BUG 2 FIX: Previously, empty content caused the result oneof to be absent entirely.
    /// </summary>
    public static Gen.OutputCommandMessage CreateOutputCommand(ReadOnlySpan<byte> content)
    {
        return new Gen.OutputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(content) }
        };
    }

    public static Gen.OutputCommandMessage CreateOutputFailure(uint code, string message)
    {
        return new Gen.OutputCommandMessage
        {
            Failure = new Gen.Failure { Code = code, Message = message }
        };
    }

    public static Gen.SleepCommandMessage CreateSleepCommand(ulong wakeUpTime, uint completionId)
    {
        return new Gen.SleepCommandMessage
        {
            WakeUpTime = wakeUpTime,
            ResultCompletionId = completionId
        };
    }

    public static Gen.GetLazyStateCommandMessage CreateGetStateCommand(string key, uint completionId)
    {
        return new Gen.GetLazyStateCommandMessage
        {
            Key = ByteString.CopyFromUtf8(key),
            ResultCompletionId = completionId
        };
    }

    public static Gen.SetStateCommandMessage CreateSetStateCommand(string key, ReadOnlySpan<byte> value)
    {
        return new Gen.SetStateCommandMessage
        {
            Key = ByteString.CopyFromUtf8(key),
            Value = new Gen.Value { Content = ByteString.CopyFrom(value) }
        };
    }

    public static Gen.ClearStateCommandMessage CreateClearStateCommand(string key)
    {
        return new Gen.ClearStateCommandMessage
        {
            Key = ByteString.CopyFromUtf8(key)
        };
    }

    public static Gen.ClearAllStateCommandMessage CreateClearAllStateCommand()
    {
        return new Gen.ClearAllStateCommandMessage();
    }

    public static Gen.GetLazyStateKeysCommandMessage CreateGetStateKeysCommand(uint completionId)
    {
        return new Gen.GetLazyStateKeysCommandMessage
        {
            ResultCompletionId = completionId
        };
    }

    public static Gen.CompleteAwakeableCommandMessage CreateCompleteAwakeableSuccess(string id, ReadOnlySpan<byte> value)
    {
        return new Gen.CompleteAwakeableCommandMessage
        {
            AwakeableId = id,
            Value = new Gen.Value { Content = ByteString.CopyFrom(value) }
        };
    }

    public static Gen.CompleteAwakeableCommandMessage CreateCompleteAwakeableFailure(string id, uint code, string reason)
    {
        return new Gen.CompleteAwakeableCommandMessage
        {
            AwakeableId = id,
            Failure = new Gen.Failure { Code = code, Message = reason }
        };
    }

    public static Gen.GetPromiseCommandMessage CreateGetPromiseCommand(string name, uint completionId)
    {
        return new Gen.GetPromiseCommandMessage
        {
            Key = name,
            ResultCompletionId = completionId
        };
    }

    public static Gen.PeekPromiseCommandMessage CreatePeekPromiseCommand(string name, uint completionId)
    {
        return new Gen.PeekPromiseCommandMessage
        {
            Key = name,
            ResultCompletionId = completionId
        };
    }

    public static Gen.CompletePromiseCommandMessage CreateCompletePromiseSuccess(
        string name, ReadOnlySpan<byte> value, uint completionId)
    {
        return new Gen.CompletePromiseCommandMessage
        {
            Key = name,
            CompletionValue = new Gen.Value { Content = ByteString.CopyFrom(value) },
            ResultCompletionId = completionId
        };
    }

    public static Gen.CompletePromiseCommandMessage CreateCompletePromiseFailure(
        string name, uint code, string reason, uint completionId)
    {
        return new Gen.CompletePromiseCommandMessage
        {
            Key = name,
            CompletionFailure = new Gen.Failure { Code = code, Message = reason },
            ResultCompletionId = completionId
        };
    }

    public static Gen.AttachInvocationCommandMessage CreateAttachInvocationCommand(string invocationId, uint completionId)
    {
        return new Gen.AttachInvocationCommandMessage
        {
            InvocationId = invocationId,
            ResultCompletionId = completionId
        };
    }

    public static Gen.GetInvocationOutputCommandMessage CreateGetInvocationOutputCommand(string invocationId, uint completionId)
    {
        return new Gen.GetInvocationOutputCommandMessage
        {
            InvocationId = invocationId,
            ResultCompletionId = completionId
        };
    }

    public static Gen.ErrorMessage CreateErrorMessage(uint code, string message)
    {
        return new Gen.ErrorMessage
        {
            Code = code,
            Message = message
        };
    }

    /// <summary>
    ///     Creates a SendSignalCommandMessage with the CANCEL built-in signal
    ///     to cancel a running invocation identified by its invocation ID.
    /// </summary>
    public static Gen.SendSignalCommandMessage CreateCancelInvocationCommand(string targetInvocationId)
    {
        return new Gen.SendSignalCommandMessage
        {
            TargetInvocationId = targetInvocationId,
            Idx = 1, // BuiltInSignal.CANCEL = 1
            Void = new Gen.Void()
        };
    }

    /// <summary>
    ///     Creates a CallCommandMessage with an optional idempotency key.
    /// </summary>
    public static Gen.CallCommandMessage CreateCallCommandWithOptions(
        string service, string handler, string? key,
        ReadOnlySpan<byte> parameter, uint completionId, uint invocationIdNotificationIdx,
        string? idempotencyKey)
    {
        var msg = CreateCallCommand(service, handler, key, parameter, completionId, invocationIdNotificationIdx);
        if (idempotencyKey is not null) msg.IdempotencyKey = idempotencyKey;
        return msg;
    }
}
