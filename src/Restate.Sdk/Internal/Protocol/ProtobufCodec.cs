using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Journal;
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

    /// <summary>The reserved built-in CANCEL signal id (protocol.proto BuiltInSignal.CANCEL = 1).</summary>
    public const uint CancelSignalId = 1;

    public static StartMessageFields ParseStartMessage(ReadOnlySpan<byte> payload)
    {
        var msg = Gen.StartMessage.Parser.ParseFrom(payload);

        // Always materialize the state map (commands re-read it even on partial start) and surface
        // the partial flag — Rust EagerState{ is_partial, values } (vm/context.rs:373-435).
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>?>(msg.StateMap.Count);
        foreach (var entry in msg.StateMap)
            eagerState[entry.Key.ToStringUtf8()] = entry.Value.Memory;

        return new StartMessageFields(
            msg.Id.ToByteArray(),
            msg.DebugId,
            msg.Key.Length > 0 ? msg.Key : null,
            msg.KnownEntries,
            msg.RandomSeed,
            eagerState,
            msg.PartialState);
    }

    /// <summary>
    ///     Preflight decoder: decodes one replayed COMMAND wire frame into a <see cref="ReplayCommand" />
    ///     using the generated protobuf parsers (AOT-safe, no reflection). The command's result is NOT
    ///     read here — completable results arrive in *CompletionNotification messages keyed by
    ///     result_completion_id; only the eager-state commands carry their result inline (Rust SysStateGet
    ///     journals the observed eager value, journal.rs:325-340).
    /// </summary>
    public static ReplayCommand ParseReplayCommand(MessageType type, ReadOnlySpan<byte> payload)
    {
        switch (type)
        {
            case MessageType.OutputCommand:
                return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.Output };
            case MessageType.GetLazyStateCommand:
                {
                    var m = Gen.GetLazyStateCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.GetState,
                        Name = m.Key.ToStringUtf8(),
                        ResultCompletionId = m.ResultCompletionId
                    };
                }
            case MessageType.GetEagerStateCommand:
                {
                    var m = Gen.GetEagerStateCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.GetState,
                        Name = m.Key.ToStringUtf8(),
                        HasEagerResult = true,
                        EagerIsVoid = m.ResultCase == Gen.GetEagerStateCommandMessage.ResultOneofCase.Void,
                        EagerValue = m.ResultCase == Gen.GetEagerStateCommandMessage.ResultOneofCase.Value
                            ? m.Value.Content.Memory : ReadOnlyMemory<byte>.Empty
                    };
                }
            case MessageType.GetLazyStateKeysCommand:
                {
                    var m = Gen.GetLazyStateKeysCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.GetStateKeys,
                        ResultCompletionId = m.ResultCompletionId
                    };
                }
            case MessageType.GetEagerStateKeysCommand:
                {
                    var m = Gen.GetEagerStateKeysCommandMessage.Parser.ParseFrom(payload);
                    // Re-encode keys as JSON string[] — same convention as the StateKeys notification.
                    var keyCount = m.Value?.Keys.Count ?? 0;
                    var keys = new string[keyCount];
                    for (var i = 0; i < keyCount; i++) keys[i] = m.Value!.Keys[i].ToStringUtf8();
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.GetStateKeys,
                        HasEagerResult = true,
                        EagerValue = (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(keys)
                    };
                }
            case MessageType.SetStateCommand:
                {
                    var m = Gen.SetStateCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.SetState,
                        Name = m.Key.ToStringUtf8()
                    };
                }
            case MessageType.ClearStateCommand:
                {
                    var m = Gen.ClearStateCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.ClearState,
                        Name = m.Key.ToStringUtf8()
                    };
                }
            case MessageType.ClearAllStateCommand:
                return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.ClearAllState };
            case MessageType.SleepCommand:
                {
                    var m = Gen.SleepCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.Sleep,
                        Name = m.Name,
                        ResultCompletionId = m.ResultCompletionId   // name = proto field 12
                    };
                }
            case MessageType.CallCommand:
                {
                    var m = Gen.CallCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.Call,
                        Name = m.Name,                                            // proto field 12
                        TargetService = m.ServiceName,
                        TargetHandler = m.HandlerName,
                        TargetKey = m.Key,
                        ResultCompletionId = m.ResultCompletionId,
                        InvocationIdNotificationIdx = m.InvocationIdNotificationIdx
                    };
                }
            case MessageType.OneWayCallCommand:
                {
                    var m = Gen.OneWayCallCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.OneWayCall,
                        Name = m.Name,
                        TargetService = m.ServiceName,
                        TargetHandler = m.HandlerName,
                        TargetKey = m.Key,
                        InvocationIdNotificationIdx = m.InvocationIdNotificationIdx
                    };
                }
            case MessageType.SendSignalCommand:
                {
                    var m = Gen.SendSignalCommandMessage.Parser.ParseFrom(payload);
                    // signal_id oneof (idx field 2 / name field 3) — exactly one variant per the proto,
                    // mirrored into ReplayCommand for the replay target/signal-identity check below.
                    var hasName = m.SignalIdCase == Gen.SendSignalCommandMessage.SignalIdOneofCase.Name;
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.SendSignal,
                        Name = m.EntryName,                                        // entry_name, proto field 12
                        SignalTargetInvocationId = m.TargetInvocationId,           // target_invocation_id, field 1
                        SignalIdx = hasName ? null : m.Idx,
                        SignalName = hasName ? m.Name : null
                    };
                }
            case MessageType.RunCommand:
                {
                    var m = Gen.RunCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.Run,
                        Name = m.Name,
                        ResultCompletionId = m.ResultCompletionId
                    };
                }
            case MessageType.GetPromiseCommand:
                {
                    var m = Gen.GetPromiseCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.GetPromise,
                        Name = m.Key,
                        ResultCompletionId = m.ResultCompletionId
                    };
                }
            case MessageType.PeekPromiseCommand:
                {
                    var m = Gen.PeekPromiseCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.PeekPromise,
                        Name = m.Key,
                        ResultCompletionId = m.ResultCompletionId
                    };
                }
            case MessageType.CompletePromiseCommand:
                {
                    var m = Gen.CompletePromiseCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.CompletePromise,
                        Name = m.Key,
                        ResultCompletionId = m.ResultCompletionId
                    };
                }
            case MessageType.AttachInvocationCommand:
                {
                    var m = Gen.AttachInvocationCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.AttachInvocation,
                        ResultCompletionId = m.ResultCompletionId
                    };
                }
            case MessageType.GetInvocationOutputCommand:
                {
                    var m = Gen.GetInvocationOutputCommandMessage.Parser.ParseFrom(payload);
                    return new ReplayCommand
                    {
                        MessageType = type,
                        EntryType = JournalEntryType.GetInvocationOutput,
                        ResultCompletionId = m.ResultCompletionId
                    };
                }
            case MessageType.CompleteAwakeableCommand:
                return new ReplayCommand { MessageType = type, EntryType = JournalEntryType.CompleteAwakeable };
            default:
                throw new ProtocolException($"Unknown replayed command type: {type}");
        }
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
    ///     Appends custom request <paramref name="headers" /> onto a journaled command's repeated
    ///     <c>headers</c> field (CallCommand field 4 / OneWayCall field 5), mirroring Rust's
    ///     <c>Target.headers: Vec&lt;Header&gt;</c> (vm/mod.rs:752-756, 824-828). The list is appended
    ///     in iteration order so the journal bytes are deterministic across replay — the caller passes
    ///     an already-ordered sequence. Null skips entirely (no empty repeated field is emitted).
    /// </summary>
    public static void AddHeaders(
        Google.Protobuf.Collections.RepeatedField<Gen.Header> target,
        IEnumerable<KeyValuePair<string, string>>? headers)
    {
        if (headers is null) return;
        foreach (var (name, value) in headers)
            target.Add(new Gen.Header { Key = name, Value = value });
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

    /// <summary>
    ///     Journals an eager-state hit (B7 — journal.rs:325-340). value == null → Void result
    ///     (known-absent/cleared); otherwise Value result.
    /// </summary>
    public static Gen.GetEagerStateCommandMessage CreateGetEagerStateCommand(string key, ReadOnlyMemory<byte>? value)
    {
        var msg = new Gen.GetEagerStateCommandMessage { Key = ByteString.CopyFromUtf8(key) };
        if (value is null) msg.Void = new Gen.Void();
        else msg.Value = new Gen.Value { Content = ByteString.CopyFrom(value.Value.Span) };
        return msg;
    }

    public static Gen.GetEagerStateKeysCommandMessage CreateGetEagerStateKeysCommand(IEnumerable<string> keys)
    {
        var sk = new Gen.StateKeys();
        foreach (var k in keys) sk.Keys.Add(ByteString.CopyFromUtf8(k));
        return new Gen.GetEagerStateKeysCommandMessage { Value = sk };
    }

    /// <summary>
    ///     Builds the SuspensionMessage (B8). waiting_named_signals (proto field 3) is populated from
    ///     parked named-signal waits — Rust fills it from NotificationId::SignalName
    ///     (terminal.rs:43-46) so the runtime resumes the invocation when a matching named signal is
    ///     delivered. Between the three lists there MUST be at least one element (proto:92); the caller
    ///     (TrySuspendAsync) enforces that before invoking this factory.
    /// </summary>
    public static Gen.SuspensionMessage CreateSuspensionMessage(
        IReadOnlyCollection<uint> waitingCompletions, IReadOnlyCollection<uint> waitingSignals,
        IReadOnlyCollection<string>? waitingNamedSignals = null)
    {
        var msg = new Gen.SuspensionMessage();
        foreach (var id in waitingCompletions) msg.WaitingCompletions.Add(id);
        foreach (var id in waitingSignals) msg.WaitingSignals.Add(id);
        if (waitingNamedSignals is not null)
            foreach (var name in waitingNamedSignals) msg.WaitingNamedSignals.Add(name);
        return msg;
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

    /// <summary>
    ///     Creates an AttachInvocationCommand whose <c>target</c> oneof is set from an
    ///     <see cref="AttachTarget" /> (vm/mod.rs:1199-1234): InvocationId, WorkflowTarget, or
    ///     IdempotentRequestTarget. Mirrors <see cref="CreateGetInvocationOutputCommand(AttachTarget, uint)" />.
    /// </summary>
    public static Gen.AttachInvocationCommandMessage CreateAttachInvocationCommand(
        AttachTarget target, uint completionId)
    {
        var msg = new Gen.AttachInvocationCommandMessage { ResultCompletionId = completionId };
        // Polymorphic dispatch on the sealed AttachTarget hierarchy (no switch/default): each variant
        // knows which target oneof to set, so there is no unreachable fallback arm to leave uncovered.
        target.ApplyTo(msg);
        return msg;
    }

    /// <summary>
    ///     Creates a GetInvocationOutputCommand whose <c>target</c> oneof is set from an
    ///     <see cref="AttachTarget" /> (vm/mod.rs:1238-1268) — the get-output twin of
    ///     <see cref="CreateAttachInvocationCommand(AttachTarget, uint)" />.
    /// </summary>
    public static Gen.GetInvocationOutputCommandMessage CreateGetInvocationOutputCommand(
        AttachTarget target, uint completionId)
    {
        var msg = new Gen.GetInvocationOutputCommandMessage { ResultCompletionId = completionId };
        target.ApplyTo(msg);
        return msg;
    }

    public static Gen.ErrorMessage CreateErrorMessage(uint code, string message,
        ulong? nextRetryDelayMs = null)
    {
        var error = new Gen.ErrorMessage
        {
            Code = code,
            Message = message
        };
        if (nextRetryDelayMs.HasValue)
            error.NextRetryDelay = nextRetryDelayMs.Value;
        return error;
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
            Idx = CancelSignalId, // BuiltInSignal.CANCEL = 1
            Void = new Gen.Void()
        };
    }

    /// <summary>
    ///     Creates a SendSignalCommandMessage carrying a NAMED signal with a success value (Rust
    ///     sys_complete_signal, mod.rs:955-979). signal_id = Name oneof (field 3); result = Value
    ///     oneof. Mirrors <see cref="CreateCancelInvocationCommand" /> but keyed by name + payload.
    /// </summary>
    public static Gen.SendSignalCommandMessage CreateSendNamedSignalSuccess(
        string targetInvocationId, string name, ReadOnlySpan<byte> value)
    {
        return new Gen.SendSignalCommandMessage
        {
            TargetInvocationId = targetInvocationId,
            Name = name,
            Value = new Gen.Value { Content = ByteString.CopyFrom(value) }
        };
    }

    /// <summary>
    ///     Creates a SendSignalCommandMessage carrying a NAMED signal with a terminal failure result
    ///     (the failure variant of <see cref="CreateSendNamedSignalSuccess" />).
    /// </summary>
    public static Gen.SendSignalCommandMessage CreateSendNamedSignalFailure(
        string targetInvocationId, string name, uint code, string message)
    {
        return new Gen.SendSignalCommandMessage
        {
            TargetInvocationId = targetInvocationId,
            Name = name,
            Failure = new Gen.Failure { Code = code, Message = message }
        };
    }

    /// <summary>
    ///     Creates a CallCommandMessage with an optional idempotency key and optional custom headers
    ///     (CallCommand fields 4/12-adjacent — see <see cref="AddHeaders" />).
    /// </summary>
    public static Gen.CallCommandMessage CreateCallCommandWithOptions(
        string service, string handler, string? key,
        ReadOnlySpan<byte> parameter, uint completionId, uint invocationIdNotificationIdx,
        string? idempotencyKey, IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var msg = CreateCallCommand(service, handler, key, parameter, completionId, invocationIdNotificationIdx);
        if (idempotencyKey is not null) msg.IdempotencyKey = idempotencyKey;
        AddHeaders(msg.Headers, headers);
        return msg;
    }

    /// <summary>
    ///     Creates a OneWayCallCommandMessage with optional custom headers (OneWayCall field 5),
    ///     mirroring <see cref="CreateCallCommandWithOptions" /> for the send path.
    /// </summary>
    public static Gen.OneWayCallCommandMessage CreateSendCommandWithOptions(
        string service, string handler, string? key,
        ReadOnlySpan<byte> parameter, ulong invokeTime, string? idempotencyKey, uint notificationIdx,
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var msg = CreateSendCommand(service, handler, key, parameter, invokeTime, idempotencyKey, notificationIdx);
        AddHeaders(msg.Headers, headers);
        return msg;
    }
}
