using Restate.Sdk;

namespace EmailPipeline;

/// <summary>
///     Email sending pipeline demonstrating multiple Restate patterns:
///     - Single email with retry + delivery tracking via awakeables
///     - Batch fan-out with parallel sends and result aggregation
///     - Saga-style compensation (mark failed recipients as bounced in CRM)
///     - Delayed follow-up emails via durable timers
/// </summary>
[Service]
public sealed class EmailPipelineService
{
    /// <summary>
    ///     Sends a single email with durable retry, tracks delivery via an awakeable,
    ///     and records the result in analytics. The awakeable pauses execution until
    ///     the email provider calls back with a delivery webhook — zero compute while waiting.
    /// </summary>
    [Handler]
    public async Task<EmailResult> SendEmail(Context ctx, SendEmailRequest request)
    {
        ctx.Console.Log($"Sending email to {request.To} with subject '{request.Subject}'");

        var messageId = await ctx.Run("send",
            () => EmailProvider.Send(request.To, request.Subject, request.HtmlBody),
            RetryPolicy.FixedAttempts(3));

        var awakeable = ctx.Awakeable<EmailTrackingEvent>();

        await ctx.Run("register-webhook",
            () =>
            {
                EmailProvider.RegisterDeliveryWebhook(messageId, awakeable.Id);
                return Task.CompletedTask;
            });

        var trackingEvent = await awakeable.Value;

        await ctx.Run("record-analytics",
            () =>
            {
                Analytics.Record(new EmailTrackingEvent(
                    messageId,
                    trackingEvent.Status,
                    trackingEvent.Timestamp));
                return Task.CompletedTask;
            });

        if (trackingEvent.Status == EmailStatus.Bounced)
        {
            await ctx.Run("mark-bounced",
                () =>
                {
                    CrmService.MarkEmailBounced(request.To);
                    return Task.CompletedTask;
                });
        }

        ctx.Console.Log($"Email {messageId} to {request.To}: {trackingEvent.Status}");

        return new EmailResult(messageId, trackingEvent.Status);
    }

    /// <summary>
    ///     Sends a batch of emails in parallel using fan-out, then aggregates results.
    ///     Each email is sent via ctx.RunAsync for non-blocking concurrent execution.
    ///     Uses ctx.All to gather all results before returning.
    /// </summary>
    [Handler]
    public async Task<EmailBatchResult> SendBatch(Context ctx, EmailBatchRequest request)
    {
        ctx.Console.Log(
            $"Sending batch {request.BatchId} with {request.Emails.Length} email(s)");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var futures = new IDurableFuture<EmailResult>[request.Emails.Length];
        for (var i = 0; i < request.Emails.Length; i++)
        {
            var email = request.Emails[i];
            futures[i] = ctx.RunAsync<EmailResult>($"send-{email.To}-{i}",
                () => SendSingleEmail(ctx, email));
        }

        var results = await ctx.All(futures);

        sw.Stop();

        var sent = results.Count(r => r.Status != EmailStatus.Failed);
        var failed = results.Length - sent;

        ctx.Console.Log(
            $"Batch {request.BatchId} complete: {sent} sent, {failed} failed in {sw.ElapsedMilliseconds}ms");

        return new EmailBatchResult(request.BatchId, results.Length, sent, failed, sw.Elapsed);
    }

    /// <summary>
    ///     Sends a campaign email to a list with saga-style compensation.
    ///     If a critical failure occurs after some emails are sent, compensates by
    ///     recording bounce events for all sent emails so the CRM stays consistent.
    ///     Uses ctx.Send with a delay to schedule a follow-up reminder email.
    /// </summary>
    [Handler]
    public async Task<EmailBatchResult> SendCampaign(Context ctx, EmailBatchRequest request)
    {
        ctx.Console.Log(
            $"Starting campaign {request.BatchId} for {request.Emails.Length} recipient(s)");

        var sentMessageIds = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            for (var i = 0; i < request.Emails.Length; i++)
            {
                var email = request.Emails[i];
                var messageId = await ctx.Run($"campaign-send-{email.To}-{i}",
                    () => EmailProvider.Send(email.To, email.Subject, email.HtmlBody),
                    RetryPolicy.FixedAttempts(3));

                sentMessageIds.Add(messageId);

                ctx.Console.Log($"Campaign {request.BatchId}: sent to {email.To} as {messageId}");

                await ctx.Run($"record-campaign-analytics-{messageId}",
                    () =>
                    {
                        Analytics.Record(new EmailTrackingEvent(
                            messageId, EmailStatus.Sent, DateTimeOffset.UtcNow));
                        return Task.CompletedTask;
                    });
            }

            var firstEmail = request.Emails.FirstOrDefault();
            if (firstEmail is not null)
            {
                await ctx.Send(
                    "EmailPipelineService",
                    "SendFollowUp",
                    new SendEmailRequest(
                        firstEmail.To,
                        $"Reminder: {firstEmail.Subject}",
                        "<p>This is a friendly reminder about our recent email.</p>",
                        request.BatchId),
                    delay: TimeSpan.FromDays(3));
            }

            sw.Stop();

            return new EmailBatchResult(
                request.BatchId,
                request.Emails.Length,
                sentMessageIds.Count,
                0,
                sw.Elapsed);
        }
        catch (TerminalException)
        {
            ctx.Console.Log(
                $"Campaign {request.BatchId} failed after {sentMessageIds.Count} send(s). Compensating...");

            for (var i = sentMessageIds.Count - 1; i >= 0; i--)
            {
                var msgId = sentMessageIds[i];
                await ctx.Run($"compensate-bounce-{msgId}",
                    () =>
                    {
                        CrmService.MarkCampaignEmailBounced(msgId);
                        return Task.CompletedTask;
                    });
            }

            sw.Stop();
            throw;
        }
    }

    /// <summary>
    ///     Handler for scheduled follow-up emails. Invoked via ctx.Send with a delay.
    ///     Demonstrates durable delayed messaging — the follow-up is guaranteed even
    ///     if the originating process crashes.
    /// </summary>
    [Handler]
    public async Task<EmailResult> SendFollowUp(Context ctx, SendEmailRequest request)
    {
        ctx.Console.Log($"Sending follow-up to {request.To}");

        var messageId = await ctx.Run("send-followup",
            () => EmailProvider.Send(request.To, request.Subject, request.HtmlBody));

        return new EmailResult(messageId, EmailStatus.Sent);
    }

    private static async Task<EmailResult> SendSingleEmail(Context ctx, SendEmailRequest email)
    {
        try
        {
            var messageId = await ctx.Run($"send-{email.To}",
                () => EmailProvider.Send(email.To, email.Subject, email.HtmlBody),
                RetryPolicy.FixedAttempts(2));

            return new EmailResult(messageId, EmailStatus.Sent);
        }
        catch (TerminalException)
        {
            return new EmailResult($"failed-{Guid.NewGuid():N}", EmailStatus.Failed);
        }
    }
}
