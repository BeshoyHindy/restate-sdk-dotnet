namespace Restate.Sdk.Internal.Protocol;

internal enum MessageType : ushort
{
    // Core
    Start = 0x0000,
    Suspension = 0x0001,
    Error = 0x0002,
    End = 0x0003,
    EntryAck = 0x0004,
    ProposeRunCompletion = 0x0005,
    AwaitingOn = 0x0006,
    ProposeRunCompletionAck = 0x0007,

    // Commands
    InputCommand = 0x0400,
    OutputCommand = 0x0401,
    GetLazyStateCommand = 0x0402,
    SetStateCommand = 0x0403,
    ClearStateCommand = 0x0404,
    ClearAllStateCommand = 0x0405,
    GetLazyStateKeysCommand = 0x0406,
    GetEagerStateCommand = 0x0407,
    GetEagerStateKeysCommand = 0x0408,
    GetPromiseCommand = 0x0409,
    PeekPromiseCommand = 0x040A,
    CompletePromiseCommand = 0x040B,
    SleepCommand = 0x040C,
    CallCommand = 0x040D,
    OneWayCallCommand = 0x040E,
    // Note: 0x040F is unassigned in the V4 protocol (awakeables use signals, not a command)
    SendSignalCommand = 0x0410,
    RunCommand = 0x0411,
    AttachInvocationCommand = 0x0412,
    GetInvocationOutputCommand = 0x0413,
    CompleteAwakeableCommand = 0x0414,

    // Completion notifications
    GetLazyStateCompletion = 0x8002,
    GetLazyStateKeysCompletion = 0x8006,
    GetPromiseCompletion = 0x8009,
    PeekPromiseCompletion = 0x800A,
    CompletePromiseCompletion = 0x800B,
    SleepCompletion = 0x800C,
    CallCompletion = 0x800D,
    CallInvocationIdCompletion = 0x800E,
    // Note: 0x800F is unassigned (awakeables use signals, not command completions)
    RunCompletion = 0x8011,
    AttachInvocationCompletion = 0x8012,
    GetInvocationOutputCompletion = 0x8013,

    // Signal notification (awakeable completions delivered via signals)
    SignalNotification = 0xFBFF
}

internal static class MessageTypeExtensions
{
    public static bool IsCommand(this MessageType type)
    {
        return ((ushort)type & 0x0400) != 0 && ((ushort)type & 0x8000) == 0;
    }

    public static bool IsNotification(this MessageType type)
    {
        return ((ushort)type & 0x8000) != 0;
    }

    public static bool IsControlMessage(this MessageType type)
    {
        return (ushort)type < 0x0400;
    }
}