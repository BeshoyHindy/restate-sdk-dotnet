namespace EmailPipeline;

public record SendEmailRequest(
    string To,
    string Subject,
    string HtmlBody,
    string? CampaignId = null);

public record EmailResult(
    string MessageId,
    EmailStatus Status,
    string? ProviderId = null);

public enum EmailStatus
{
    Queued,
    Sent,
    Delivered,
    Bounced,
    Opened,
    Clicked,
    Failed,
}

public record EmailBatchRequest(
    string BatchId,
    SendEmailRequest[] Emails);

public record EmailBatchResult(
    string BatchId,
    int Total,
    int Sent,
    int Failed,
    TimeSpan Duration);

public record EmailTrackingEvent(
    string MessageId,
    EmailStatus Status,
    DateTimeOffset Timestamp);
