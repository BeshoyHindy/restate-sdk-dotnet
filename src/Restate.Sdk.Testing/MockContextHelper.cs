namespace Restate.Sdk.Testing;

/// <summary>
///     Internal composition helper that reduces duplication across keyed mock context classes.
///     Each keyed mock (MockObjectContext, MockSharedObjectContext, etc.) delegates common
///     operations to this helper, which wraps a <see cref="MockContext" /> instance.
/// </summary>
internal sealed class MockContextHelper
{
    public MockContextHelper(string key, string? invocationId)
    {
        Key = key;
        InnerMock = new MockContext(invocationId);
    }

    public MockContext InnerMock { get; }
    public string Key { get; }

    // Delegation properties
    public IReadOnlyList<RecordedCall> Calls => InnerMock.Calls;
    public IReadOnlyList<RecordedSend> Sends => InnerMock.Sends;
    public IReadOnlyList<RecordedSleep> Sleeps => InnerMock.Sleeps;
    public IReadOnlyList<string> Cancellations => InnerMock.Cancellations;

    // Delegation setup methods
    public void SetupCall<T>(string service, string handler, T result)
    {
        InnerMock.SetupCall(service, handler, result);
    }

    public void SetupCall<T>(string service, string key, string handler, T result)
    {
        InnerMock.SetupCall(service, key, handler, result);
    }

    public void SetupCallFailure(string service, string handler, TerminalException exception)
    {
        InnerMock.SetupCallFailure(service, handler, exception);
    }

    public void SetupCallFailure(string service, string key, string handler, TerminalException exception)
    {
        InnerMock.SetupCallFailure(service, key, handler, exception);
    }

    public void SetupAwakeable<T>(T result)
    {
        InnerMock.SetupAwakeable(result);
    }

    public void RegisterClient<TClient>(TClient client) where TClient : class
    {
        InnerMock.RegisterClient(client);
    }
}
