using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace IntuneWipePortal.Services;

// Queue inspection and operator actions (depth probe, purge, peek).
public sealed partial class CruscottoTelemetryService
{
    private async Task<IReadOnlyList<QueueStatus>> LoadQueuesAsync(CancellationToken ct)
    {
        if (_sbAdmin is null)
        {
            return FlowQueues.Select(n => new QueueStatus(n, 0, 0, 0, null, NodeHealth.Unknown, "SB admin client not configured")).ToArray();
        }
        var results = new List<QueueStatus>(FlowQueues.Length);
        foreach (var name in FlowQueues)
        {
            try
            {
                var props = await _sbAdmin.GetQueueRuntimePropertiesAsync(name, ct);
                var active = props.Value.ActiveMessageCount;
                var dlq = props.Value.DeadLetterMessageCount;
                results.Add(new QueueStatus(
                    Name: name,
                    Active: active,
                    DeadLetter: dlq,
                    Scheduled: props.Value.ScheduledMessageCount,
                    AccessedAt: props.Value.AccessedAt,
                    Status: dlq > 0      ? NodeHealth.Red
                          : active >= 50 ? NodeHealth.Red
                          : active >= 10 ? NodeHealth.Yellow
                          :                NodeHealth.Green,
                    Error: null));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                results.Add(new QueueStatus(name, 0, 0, 0, null, NodeHealth.Unknown, "queue not provisioned"));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cruscotto: queue {Queue} runtime lookup failed", ForLog(name));
                results.Add(new QueueStatus(name, 0, 0, 0, null, NodeHealth.Unknown, ex.GetType().Name));
            }
        }
        return results;
    }

    public async Task<QueuePurgeResult> PurgeQueueAsync(string queueName, int maxMessages, bool deadLetterQueue, CancellationToken ct)
    {
        if (_sbClient is null)
            throw new InvalidOperationException("Service Bus data client is not configured.");

        if (string.IsNullOrWhiteSpace(queueName) || !FlowQueues.Contains(queueName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Queue '{queueName}' is not allowed.", nameof(queueName));

        var max = Math.Clamp(maxMessages, 1, 2_000);
        var drained = 0;

        await using var receiver = _sbClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            SubQueue = deadLetterQueue ? SubQueue.DeadLetter : SubQueue.None,
        });

        while (drained < max)
        {
            var chunk = Math.Min(100, max - drained);
            var messages = await receiver.ReceiveMessagesAsync(
                maxMessages: chunk,
                maxWaitTime: TimeSpan.FromSeconds(2),
                cancellationToken: ct);

            if (messages.Count == 0) break;

            foreach (var message in messages)
            {
                await receiver.CompleteMessageAsync(message, ct);
                drained++;
            }
        }

        return new QueuePurgeResult(
            QueueName: queueName,
            IsDeadLetterQueue: deadLetterQueue,
            DrainedMessages: drained,
            LimitReached: drained >= max,
            PerformedAt: DateTimeOffset.UtcNow);
    }

    public async Task<QueuePeekResult> PeekQueueAsync(string queueName, int top, bool deadLetterQueue, CancellationToken ct)
    {
        if (_sbClient is null)
            throw new InvalidOperationException("Service Bus data client is not configured.");

        if (string.IsNullOrWhiteSpace(queueName) || !FlowQueues.Contains(queueName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Queue '{queueName}' is not allowed.", nameof(queueName));

        var take = Math.Clamp(top, 1, 25);
        var messages = new List<QueuePeekMessage>(take);

        await using var receiver = _sbClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = deadLetterQueue ? SubQueue.DeadLetter : SubQueue.None,
        });

        long? fromSequenceNumber = null;
        while (messages.Count < take)
        {
            var chunk = Math.Min(10, take - messages.Count);
            var peeked = await receiver.PeekMessagesAsync(chunk, fromSequenceNumber, ct);
            if (peeked.Count == 0)
                break;

            foreach (var message in peeked)
            {
                messages.Add(MapPeekMessage(message));
                fromSequenceNumber = message.SequenceNumber + 1;
                if (messages.Count >= take)
                    break;
            }
        }

        return new QueuePeekResult(
            QueueName: queueName,
            IsDeadLetterQueue: deadLetterQueue,
            RequestedMessages: take,
            RetrievedMessages: messages.Count,
            PerformedAt: DateTimeOffset.UtcNow,
            Messages: messages.ToArray());
    }

    private static QueuePeekMessage MapPeekMessage(ServiceBusReceivedMessage message)
    {
        var applicationProperties = message.ApplicationProperties.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : message.ApplicationProperties.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        return new QueuePeekMessage(
            SequenceNumber: message.SequenceNumber,
            MessageId: message.MessageId,
            CorrelationId: NullIfEmpty(message.CorrelationId),
            Subject: NullIfEmpty(message.Subject),
            SessionId: NullIfEmpty(message.SessionId),
            EnqueuedTimeUtc: message.EnqueuedTime == DateTimeOffset.MinValue ? null : message.EnqueuedTime,
            DeliveryCount: message.DeliveryCount,
            ContentType: NullIfEmpty(message.ContentType),
            BodyPreview: TruncateBody(message.Body.ToString(), 2000),
            BodyLength: message.Body.ToString().Length,
            DeadLetterReason: NullIfEmpty(message.DeadLetterReason),
            DeadLetterErrorDescription: NullIfEmpty(message.DeadLetterErrorDescription),
            ApplicationProperties: applicationProperties);
    }

    private static string TruncateBody(string? body, int maxChars)
    {
        if (string.IsNullOrEmpty(body))
            return string.Empty;

        return body.Length <= maxChars
            ? body
            : body[..maxChars] + "…";
    }
}
