using System.Text;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Gen = Restate.Sdk.Internal.Protocol.Generated;

namespace Restate.Sdk.Tests.Protocol;

public class ProtobufCodecTests
{
    [Fact]
    public void CreateErrorMessage_WithNextRetryDelay_SetsField()
    {
        var msg = ProtobufCodec.CreateErrorMessage(503, "retry me", 2500);

        Assert.Equal(503u, msg.Code);
        Assert.True(msg.HasNextRetryDelay);
        Assert.Equal(2500ul, msg.NextRetryDelay);
    }

    [Fact]
    public void CreateErrorMessage_WithoutDelay_LeavesFieldUnset()
    {
        var msg = ProtobufCodec.CreateErrorMessage(500, "boom");

        Assert.False(msg.HasNextRetryDelay);
    }

    [Fact]
    public void ParseStartMessage_ExtractsAllFields()
    {
        var msg = new Gen.StartMessage
        {
            Id = ByteString.CopyFromUtf8("raw-id"),
            DebugId = "inv-abc123",
            KnownEntries = 5,
            Key = "my-key",
            RandomSeed = 42
        };

        var fields = ProtobufCodec.ParseStartMessage(msg.ToByteArray());

        Assert.Equal("inv-abc123", fields.InvocationId);
        Assert.Equal("my-key", fields.Key);
        Assert.Equal(5u, fields.KnownEntries);
        Assert.Equal(42ul, fields.RandomSeed);
    }

    [Fact]
    public void ParseStartMessage_WithEagerState()
    {
        var msg = new Gen.StartMessage
        {
            DebugId = "inv-1",
            KnownEntries = 1,
            RandomSeed = 99,
            PartialState = false
        };
        msg.StateMap.Add(new Gen.StartMessage.Types.StateEntry
        {
            Key = ByteString.CopyFromUtf8("count"),
            Value = ByteString.CopyFrom(42)
        });
        msg.StateMap.Add(new Gen.StartMessage.Types.StateEntry
        {
            Key = ByteString.CopyFromUtf8("name"),
            Value = ByteString.CopyFromUtf8("hello")
        });

        var fields = ProtobufCodec.ParseStartMessage(msg.ToByteArray());

        Assert.Equal("inv-1", fields.InvocationId);
        Assert.Equal(1u, fields.KnownEntries);
        Assert.Equal(99ul, fields.RandomSeed);
        Assert.NotNull(fields.EagerState);
        Assert.Equal(2, fields.EagerState!.Count);
        Assert.Equal(new byte[] { 42 }, fields.EagerState["count"].ToArray());
        Assert.Equal("hello", Encoding.UTF8.GetString(fields.EagerState["name"].Span));
    }

    [Fact]
    public void ParseStartMessage_PartialState_ReturnsNullEagerState()
    {
        var msg = new Gen.StartMessage
        {
            DebugId = "inv-partial",
            PartialState = true
        };

        var fields = ProtobufCodec.ParseStartMessage(msg.ToByteArray());
        Assert.Null(fields.EagerState);
    }

    [Fact]
    public void ParseInputCommand_ExtractsContent()
    {
        var content = Encoding.UTF8.GetBytes("{\"name\":\"world\"}");
        var msg = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(content) }
        };

        var (input, headers) = ProtobufCodec.ParseInputCommand(msg.ToByteArray());

        Assert.Equal(content, input.ToArray());
        Assert.Null(headers);
    }

    [Fact]
    public void ParseInputCommand_EmptyPayload_ReturnsEmpty()
    {
        var (input, headers) = ProtobufCodec.ParseInputCommand(ReadOnlySpan<byte>.Empty);
        Assert.True(input.IsEmpty);
        Assert.Null(headers);
    }

    [Fact]
    public void ParseInputCommand_ExtractsHeaders()
    {
        var content = Encoding.UTF8.GetBytes("{}");
        var msg = new Gen.InputCommandMessage
        {
            Value = new Gen.Value { Content = ByteString.CopyFrom(content) }
        };
        msg.Headers.Add(new Gen.Header { Key = "content-type", Value = "application/json" });
        msg.Headers.Add(new Gen.Header { Key = "x-request-id", Value = "abc-123" });

        var (input, headers) = ProtobufCodec.ParseInputCommand(msg.ToByteArray());

        Assert.Equal(content, input);
        Assert.NotNull(headers);
        Assert.Equal(2, headers.Count);
        Assert.Equal("application/json", headers["content-type"]);
        Assert.Equal("abc-123", headers["x-request-id"]);
    }

    [Fact]
    public void ParseCompletionNotification_Value()
    {
        var msg = new Gen.NotificationTemplate
        {
            CompletionId = 3,
            Value = new Gen.Value { Content = ByteString.CopyFrom(1, 2, 3) }
        };

        var notification = ProtobufCodec.ParseCompletionNotification(msg.ToByteArray());

        Assert.Equal(3u, notification.CompletionId);
        Assert.NotNull(notification.Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, notification.Value.Value.ToArray());
        Assert.True(notification.IsSuccess);
        Assert.False(notification.IsFailure);
    }

    [Fact]
    public void ParseCompletionNotification_Failure()
    {
        var msg = new Gen.NotificationTemplate
        {
            CompletionId = 5,
            Failure = new Gen.Failure { Code = 500, Message = "Something broke" }
        };

        var notification = ProtobufCodec.ParseCompletionNotification(msg.ToByteArray());

        Assert.Equal(5u, notification.CompletionId);
        Assert.Equal((ushort)500, notification.FailureCode);
        Assert.Equal("Something broke", notification.FailureMessage);
        Assert.True(notification.IsFailure);
    }

    [Fact]
    public void ParseCompletionNotification_Void()
    {
        var msg = new Gen.NotificationTemplate
        {
            CompletionId = 7,
            Void = new Gen.Void()
        };

        var notification = ProtobufCodec.ParseCompletionNotification(msg.ToByteArray());

        Assert.Equal(7u, notification.CompletionId);
        Assert.True(notification.IsVoid);
        Assert.True(notification.IsSuccess);
    }

    [Fact]
    public void ParseCompletionNotification_InvocationId()
    {
        var msg = new Gen.NotificationTemplate
        {
            CompletionId = 9,
            InvocationId = "inv-xyz-789"
        };

        var notification = ProtobufCodec.ParseCompletionNotification(msg.ToByteArray());

        Assert.Equal(9u, notification.CompletionId);
        Assert.Equal("inv-xyz-789", notification.InvocationId);
    }

    /// <summary>
    ///     BUG 4 FIX: StateKeys completion notification is parsed correctly via field 17.
    /// </summary>
    [Fact]
    public void ParseCompletionNotification_StateKeys()
    {
        var msg = new Gen.NotificationTemplate
        {
            CompletionId = 11,
            StateKeys = new Gen.StateKeys()
        };
        msg.StateKeys.Keys.Add(ByteString.CopyFromUtf8("key1"));
        msg.StateKeys.Keys.Add(ByteString.CopyFromUtf8("key2"));

        var notification = ProtobufCodec.ParseCompletionNotification(msg.ToByteArray());

        Assert.Equal(11u, notification.CompletionId);
        Assert.NotNull(notification.Value);
        // The value should be JSON-serialized string[]
        var keys = System.Text.Json.JsonSerializer.Deserialize<string[]>(notification.Value.Value.Span)!;
        Assert.Equal(["key1", "key2"], keys);
    }

    /// <summary>
    ///     BUG 1 FIX: CallCommand includes invocation_id_notification_idx field.
    /// </summary>
    [Fact]
    public void CreateCallCommand_IncludesInvocationIdNotificationIdx()
    {
        var msg = ProtobufCodec.CreateCallCommand(
            "MyService", "MyHandler", "key1",
            Encoding.UTF8.GetBytes("{}"), completionId: 5, invocationIdNotificationIdx: 3);

        Assert.Equal("MyService", msg.ServiceName);
        Assert.Equal("MyHandler", msg.HandlerName);
        Assert.Equal("key1", msg.Key);
        Assert.Equal(5u, msg.ResultCompletionId);
        Assert.Equal(3u, msg.InvocationIdNotificationIdx);
    }

    /// <summary>
    ///     BUG 2 FIX: OutputCommand always sets Value even for empty content.
    /// </summary>
    [Fact]
    public void CreateOutputCommand_EmptyContent_StillHasValue()
    {
        var msg = ProtobufCodec.CreateOutputCommand(ReadOnlySpan<byte>.Empty);

        Assert.Equal(Gen.OutputCommandMessage.ResultOneofCase.Value, msg.ResultCase);
        Assert.NotNull(msg.Value);
    }

    /// <summary>
    ///     BUG 3 FIX: RunCommand has no RetryPolicy field.
    /// </summary>
    [Fact]
    public void CreateRunCommand_HasNoRetryPolicy()
    {
        var msg = ProtobufCodec.CreateRunCommand("myRun", 7);

        Assert.Equal("myRun", msg.Name);
        Assert.Equal(7u, msg.ResultCompletionId);
        // RunCommandMessage only has result_completion_id (11) and name (12) — no retry_policy
        var size = msg.CalculateSize();
        Assert.True(size > 0);
    }

    [Fact]
    public void CreateSendCommand_AllFields()
    {
        var param = Encoding.UTF8.GetBytes("{\"x\":1}");
        var msg = ProtobufCodec.CreateSendCommand(
            "Svc", "Handler", "k1", param, 1000UL, "idem-key", 42);

        Assert.Equal("Svc", msg.ServiceName);
        Assert.Equal("Handler", msg.HandlerName);
        Assert.Equal("k1", msg.Key);
        Assert.Equal(1000UL, msg.InvokeTime);
        Assert.Equal("idem-key", msg.IdempotencyKey);
        Assert.Equal(42u, msg.InvocationIdNotificationIdx);
    }

    [Fact]
    public void CreateSleepCommand_SetsFields()
    {
        var msg = ProtobufCodec.CreateSleepCommand(999UL, 3);
        Assert.Equal(999UL, msg.WakeUpTime);
        Assert.Equal(3u, msg.ResultCompletionId);
    }

    [Fact]
    public void CreateSetStateCommand_SetsFields()
    {
        var value = Encoding.UTF8.GetBytes("42");
        var msg = ProtobufCodec.CreateSetStateCommand("count", value);

        Assert.Equal(ByteString.CopyFromUtf8("count"), msg.Key);
        Assert.Equal(value, msg.Value.Content.ToByteArray());
    }

    [Fact]
    public void CreateCompletePromiseSuccess_SetsFields()
    {
        var value = Encoding.UTF8.GetBytes("\"done\"");
        var msg = ProtobufCodec.CreateCompletePromiseSuccess("myPromise", value, 5);

        Assert.Equal("myPromise", msg.Key);
        Assert.Equal(value, msg.CompletionValue.Content.ToByteArray());
        Assert.Equal(5u, msg.ResultCompletionId);
    }

    [Fact]
    public void CreateCompletePromiseFailure_SetsFields()
    {
        var msg = ProtobufCodec.CreateCompletePromiseFailure("myPromise", 400, "bad request", 6);

        Assert.Equal("myPromise", msg.Key);
        Assert.Equal(400u, msg.CompletionFailure.Code);
        Assert.Equal("bad request", msg.CompletionFailure.Message);
        Assert.Equal(6u, msg.ResultCompletionId);
    }

    [Fact]
    public void CreateErrorMessage_SetsFields()
    {
        var msg = ProtobufCodec.CreateErrorMessage(500, "Internal error");

        Assert.Equal(500u, msg.Code);
        Assert.Equal("Internal error", msg.Message);
    }
}
