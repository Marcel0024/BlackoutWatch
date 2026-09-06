using BlackoutWatch.Configuration;
using BlackoutWatch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlackoutWatch.Services;

public sealed class OutageDetectionService(
    IDbContextFactory<BlackoutWatchDbContext> dbContextFactory,
    IOptions<BlackoutWatchOptions> options,
    TimeProvider timeProvider)
{
    private readonly BlackoutWatchOptions options = options.Value;

    public async Task DetectStartupOutageAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var state = await dbContext.HeartbeatStates
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        var restored = timeProvider.GetUtcNow();

        if (state is null || options.MinimumOutageSeconds <= 0)
        {
            return;
        }

        var durationSeconds = (int)(restored - state.LastHeartbeat).TotalSeconds;
        if (durationSeconds < options.MinimumOutageSeconds)
        {
            return;
        }

        var outage = new Outage
        {
            OutageStartUtc = state.LastHeartbeat,
            RestoredUtc = restored,
            DurationSeconds = durationSeconds
        };

        if (options.Webhook.Enabled)
        {
            outage.NotificationDeliveries.Add(new NotificationDelivery
            {
                Channel = NotificationChannel.Webhook,
                NextAttemptUtc = restored
            });
        }

        if (options.Mqtt.Enabled)
        {
            outage.NotificationDeliveries.Add(new NotificationDelivery
            {
                Channel = NotificationChannel.Mqtt,
                NextAttemptUtc = restored
            });
        }

        dbContext.Outages.Add(outage);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
