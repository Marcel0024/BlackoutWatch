using BlackoutWatch.Configuration;
using BlackoutWatch.Data;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BlackoutWatch.Services;

public sealed class HeartbeatService(IDbContextFactory<BlackoutWatchDbContext> dbContextFactory)
{
    public async Task RecordAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var state = await dbContext.HeartbeatStates.SingleOrDefaultAsync(cancellationToken);

        if (state is null)
        {
            dbContext.HeartbeatStates.Add(new HeartbeatState { Id = 1, LastHeartbeat = timestamp });
        }
        else
        {
            state.LastHeartbeat = timestamp;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

public sealed class HeartbeatBackgroundService(
    HeartbeatService heartbeatService,
    OutageDetectionService outageDetectionService,
    IOptions<BlackoutWatchOptions> options,
    TimeProvider timeProvider,
    ILogger<HeartbeatBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromDays(1);
    private readonly BlackoutWatchOptions options = options.Value;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await DetectStartupOutageOnceAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var schedule = CronSchedule.Parse(options.HeartbeatCron);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await heartbeatService.RecordAsync(timeProvider.GetUtcNow(), stoppingToken);

                await WaitForNextOccurrenceAsync(schedule, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "The heartbeat loop failed. Retrying in {RetryDelay}.",
                    options.HeartbeatRestartDelay);

                await DelayAfterFailureAsync(stoppingToken);
            }
        }
    }

    private async Task DetectStartupOutageOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await outageDetectionService.DetectStartupOutageAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Startup outage detection failed. Heartbeat recording will continue.");
        }
    }

    private async Task WaitForNextOccurrenceAsync(
        CronExpression schedule,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var now = timeProvider.GetUtcNow();
            var next = schedule.GetNextOccurrence(now.UtcDateTime)
                ?? throw new InvalidOperationException("The heartbeat cron has no next occurrence.");
            var delay = next - now.UtcDateTime;

            if (delay <= MaximumTimerDelay)
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
                return;
            }

            await Task.Delay(MaximumTimerDelay, timeProvider, cancellationToken);
        }
    }

    private async Task DelayAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(options.HeartbeatRestartDelay, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown while waiting to retry.
        }
    }
}
