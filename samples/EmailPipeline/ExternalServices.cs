namespace EmailPipeline;

/// <summary>
///     Stub for an email delivery provider (e.g., SendGrid, Mailgun, SES).
///     In production, use idempotency keys to prevent duplicate sends on retry.
/// </summary>
public static class EmailProvider
{
    public static Task<string> Send(string to, string subject, string htmlBody)
    {
        var messageId = $"msg-{Guid.NewGuid():N}";
        Console.WriteLine($"  [EmailProvider] Sending to {to}: '{subject}' (id={messageId})");
        return Task.FromResult(messageId);
    }

    public static void RegisterDeliveryWebhook(string messageId, string callbackId)
    {
        Console.WriteLine(
            $"  [EmailProvider] Registered webhook for {messageId} -> awakeable {callbackId}");
    }
}

/// <summary>
///     Stub for a CRM system (e.g., HubSpot, Salesforce).
///     Updates contact records when emails bounce.
/// </summary>
public static class CrmService
{
    public static void MarkEmailBounced(string email)
    {
        Console.WriteLine($"  [CRM] Marked {email} as bounced");
    }

    public static void MarkCampaignEmailBounced(string messageId)
    {
        Console.WriteLine($"  [CRM] Marked campaign message {messageId} as bounced (compensation)");
    }
}

/// <summary>
///     Stub for an analytics service (e.g., Mixpanel, Segment, BigQuery).
/// </summary>
public static class Analytics
{
    public static void Record(EmailTrackingEvent evt)
    {
        Console.WriteLine($"  [Analytics] {evt.MessageId}: {evt.Status} at {evt.Timestamp:O}");
    }
}
