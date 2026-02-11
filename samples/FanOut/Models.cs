namespace FanOut;

/// <summary>Request to process a batch of data items in parallel.</summary>
public record BatchRequest(string BatchId, string[] Items);

/// <summary>Result of processing a batch.</summary>
public record BatchResult(string BatchId, ItemResult[] Results, TimeSpan TotalDuration);

/// <summary>Result of processing a single item.</summary>
public record ItemResult(string Item, string Result, long DurationMs);
