using BlackoutWatch.Configuration;
using BlackoutWatch.Data;
using BlackoutWatch.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<BlackoutWatchOptions>()
    .BindConfiguration(BlackoutWatchOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("BlackoutWatch")
    ?? throw new InvalidOperationException("ConnectionStrings:BlackoutWatch is required.");

builder.Services.AddDbContextFactory<BlackoutWatchDbContext>(dbOptions =>
    dbOptions.UseSqlite(connectionString));

builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<HostOptions>(hostOptions =>
    hostOptions.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
builder.Services.AddSingleton<OutageNotificationService>();
builder.Services.AddSingleton<OutageDetectionService>();
builder.Services.AddSingleton<HeartbeatService>();
builder.Services.AddHostedService<HeartbeatBackgroundService>();
builder.Services.AddHostedService<NotificationDeliveryBackgroundService>();

var app = builder.Build();

await using (var dbContext = await app.Services
    .GetRequiredService<IDbContextFactory<BlackoutWatchDbContext>>()
    .CreateDbContextAsync())
{
    await dbContext.Database.MigrateAsync();
}

app.MapGet("/health", () => TypedResults.Ok(new { status = "healthy" }));
app.MapGet("/api/outages", async (
    BlackoutWatchDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    return TypedResults.Ok(await dbContext.Outages
        .AsNoTracking()
        .OrderByDescending(outage => outage.Id)
        .Select(outage => new OutageResponse(
            outage.OutageStartUtc,
            outage.RestoredUtc,
            outage.DurationSeconds))
        .ToListAsync(cancellationToken));
});

await app.RunAsync();

internal sealed record OutageResponse(
    DateTimeOffset OutageStart,
    DateTimeOffset Restored,
    int DurationSeconds)
{
    public string Duration => OutageDuration.Format(DurationSeconds);
}
