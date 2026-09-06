using BlackoutWatch.Configuration;
using BlackoutWatch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlackoutWatch.Services;

public sealed class NotificationDeliveryBackgroundService(
    IDbContextFactory<BlackoutWatchDbContext> dbContextFactory,
    OutageNotificationService notificationService,
    IOptions<BlackoutWatchOptions> options,
    TimeProvider timeProvider,
    ILogger<NotificationDeliveryBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);
    private readonly BlackoutWatchOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = await DeliverPendingNotificationsAsync(stoppingToken);
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Notification delivery loop failed. Retrying in {RetryDelay}.",
                    options.NotificationRetryInitialDelay);
                await Task.Delay(options.NotificationRetryInitialDelay, timeProvider, stoppingToken);
            }
        }
    }

    private async Task<TimeSpan> DeliverPendingNotificationsAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();

        var pendingDeliveries = await dbContext.NotificationDeliveries
            .Include(delivery => delivery.Outage)
            .Where(delivery => delivery.DeliveredUtc == null && delivery.FailedUtc == null)
            .ToListAsync(cancellationToken);

        var alreadyExhausted = pendingDeliveries
            .Where(delivery => delivery.Attempts >= options.NotificationMaxAttempts)
            .ToList();

        foreach (var delivery in alreadyExhausted)
        {
            delivery.FailedUtc = now;
        }

        if (alreadyExhausted.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var deliveries = pendingDeliveries
            .Where(delivery => delivery.FailedUtc == null && delivery.NextAttemptUtc <= now)
            .OrderBy(delivery => delivery.NextAttemptUtc)
            .ToList();

        foreach (var delivery in deliveries)
        {
            await DeliverAsync(dbContext, delivery, cancellationToken);
        }

        var nextAttempt = pendingDeliveries
            .Where(delivery => delivery.DeliveredUtc == null && delivery.FailedUtc == null)
            .Select(delivery => (DateTimeOffset?)delivery.NextAttemptUtc)
            .Min();

        if (nextAttempt is null)
        {
            return IdleDelay;
        }

        var remaining = nextAttempt.Value - timeProvider.GetUtcNow();
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : Min(remaining, IdleDelay);
    }

    private async Task DeliverAsync(
        BlackoutWatchDbContext dbContext,
        NotificationDelivery delivery,
        CancellationToken cancellationToken)
    {
        delivery.Attempts++;

        try
        {
            await notificationService.SendAsync(delivery.Channel, delivery.Outage, cancellationToken);

            delivery.DeliveredUtc = timeProvider.GetUtcNow();
            delivery.LastError = null;

            logger.LogInformation(
                "Delivered outage {OutageId} notification through {Channel} after {Attempts} attempt(s).",
                delivery.OutageId,
                delivery.Channel,
                delivery.Attempts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            delivery.LastError = exception.Message[..Math.Min(exception.Message.Length, 2_000)];
            var failedAt = timeProvider.GetUtcNow();

            if (delivery.Attempts >= options.NotificationMaxAttempts)
            {
                delivery.FailedUtc = failedAt;
                logger.LogError(
                    exception,
                    "Failed to deliver outage {OutageId} through {Channel} after {Attempts} attempts. No further retries will be made.",
                    delivery.OutageId,
                    delivery.Channel,
                    delivery.Attempts);
            }
            else
            {
                delivery.NextAttemptUtc = failedAt + GetRetryDelay(delivery.Attempts);
                logger.LogWarning(
                    exception,
                    "Failed to deliver outage {OutageId} through {Channel}. Attempt {Attempts} of {MaxAttempts}; next retry at {NextAttemptUtc}.",
                    delivery.OutageId,
                    delivery.Channel,
                    delivery.Attempts,
                    options.NotificationMaxAttempts,
                    delivery.NextAttemptUtc);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private TimeSpan GetRetryDelay(int attempts)
    {
        var exponent = Math.Min(attempts - 1, 30);
        var delayTicks = options.NotificationRetryInitialDelay.Ticks * Math.Pow(2, exponent);
        return TimeSpan.FromTicks((long)Math.Min(delayTicks, options.NotificationRetryMaxDelay.Ticks));
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
