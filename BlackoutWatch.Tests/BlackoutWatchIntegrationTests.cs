using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BlackoutWatch.Configuration;
using BlackoutWatch.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlackoutWatch.Tests;

public class BlackoutWatchIntegrationTests
{
    [Fact]
    public async Task Api_ExposesHealthAndOutagesEndpoints()
    {
        using var factory = new BlackoutWatchApplicationFactory();
        using var client = factory.CreateClient();

        var healthResponse = await client.GetAsync("/health");
        var outagesResponse = await client.GetAsync("/api/outages");
        var outages = await outagesResponse.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, outagesResponse.StatusCode);
        Assert.Empty(outages!);
    }

    [Fact]
    public async Task Api_Returns404ForUnknownRoute()
    {
        using var factory = new BlackoutWatchApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Startup_RecordsExistingDowntimeBeforeFirstHeartbeat()
    {
        using var factory = new BlackoutWatchApplicationFactory(minimumOutageSeconds: 60);
        await SeedHeartbeatAsync(factory, DateTimeOffset.UtcNow.AddMinutes(-5));
        using var client = factory.CreateClient();

        var outages = await (await client.GetAsync("/api/outages"))
            .Content
            .ReadFromJsonAsync<JsonElement[]>();

        var outage = Assert.Single(outages!);
        var durationSeconds = outage.GetProperty("durationSeconds").GetInt32();
        var durationParts = outage.GetProperty("duration")
            .GetString()!
            .Split(':')
            .Select(int.Parse)
            .ToArray();

        Assert.True(durationSeconds >= 300);
        Assert.Equal(
            durationSeconds,
            (durationParts[0] * 60 * 60) + (durationParts[1] * 60) + durationParts[2]);
    }

    [Fact]
    public async Task Startup_DoesNotRecordDowntimeWhenDetectionIsDisabled()
    {
        using var factory = new BlackoutWatchApplicationFactory(minimumOutageSeconds: 0);
        await SeedHeartbeatAsync(factory, DateTimeOffset.UtcNow.AddMinutes(-5));
        using var client = factory.CreateClient();

        await WaitForHeartbeatAfterAsync(factory, DateTimeOffset.UtcNow.AddMinutes(-1));
        var outages = await (await client.GetAsync("/api/outages"))
            .Content
            .ReadFromJsonAsync<JsonElement[]>();

        Assert.Empty(outages!);
    }

    [Fact]
    public async Task CronSchedule_WritesRecurringHeartbeats()
    {
        using var factory = new BlackoutWatchApplicationFactory(
            heartbeatCron: "* * * * * *",
            minimumOutageSeconds: 0);
        using var client = factory.CreateClient();

        var firstHeartbeat = await WaitForHeartbeatAfterAsync(factory, DateTimeOffset.MinValue);
        var nextHeartbeat = await WaitForHeartbeatAfterAsync(factory, firstHeartbeat);

        Assert.True(nextHeartbeat > firstHeartbeat);
    }

    [Fact]
    public void DefaultConfiguration_UsesExpectedSchedulesAndRetryDelays()
    {
        using var factory = new BlackoutWatchApplicationFactory(heartbeatCron: string.Empty);
        using var client = factory.CreateClient();

        var options = factory.Services
            .GetRequiredService<IOptions<BlackoutWatchOptions>>()
            .Value;

        Assert.Equal("*/5 * * * * *", options.HeartbeatCron);
        Assert.Equal(TimeSpan.FromSeconds(5), options.NotificationRetryInitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), options.NotificationRetryMaxDelay);
        Assert.Equal(10, options.NotificationMaxAttempts);
        Assert.Equal(
            BackgroundServiceExceptionBehavior.StopHost,
            factory.Services.GetRequiredService<IOptions<HostOptions>>()
                .Value
                .BackgroundServiceExceptionBehavior);
    }

    [Fact]
    public void DefaultLogging_SuppressesSuccessfulDatabaseCommands()
    {
        using var factory = new BlackoutWatchApplicationFactory();
        using var client = factory.CreateClient();
        var logger = factory.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("*/5 * * * * *")]
    public void CronSchedule_AcceptsFiveAndSixFieldExpressions(string expression)
    {
        using var factory = new BlackoutWatchApplicationFactory(
            heartbeatCron: expression,
            minimumOutageSeconds: 0);
        using var client = factory.CreateClient();

        var configuredCron = factory.Services
            .GetRequiredService<IOptions<BlackoutWatchOptions>>()
            .Value
            .HeartbeatCron;

        Assert.Equal(expression, configuredCron);
    }

    [Fact]
    public void InvalidCron_PreventsApplicationStartup()
    {
        using var factory = new BlackoutWatchApplicationFactory(heartbeatCron: "not a cron");

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("cron", exception.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task HeartbeatLoop_RecoversAfterDatabaseWriteFailure()
    {
        using var factory = new BlackoutWatchApplicationFactory(
            heartbeatCron: "* * * * * *",
            minimumOutageSeconds: 0,
            heartbeatRestartDelay: "00:00:00.100");
        using var client = factory.CreateClient();
        var initialHeartbeat = await WaitForHeartbeatAfterAsync(factory, DateTimeOffset.MinValue);

        using (var lockConnection = new SqliteConnection(factory.ConnectionString))
        {
            lockConnection.Open();
            Execute(lockConnection, "BEGIN EXCLUSIVE;");
            await Task.Delay(TimeSpan.FromMilliseconds(1_200));
            Execute(lockConnection, "ROLLBACK;");
        }

        var recoveredHeartbeat = await WaitForHeartbeatAfterAsync(factory, initialHeartbeat);

        Assert.True(recoveredHeartbeat > initialHeartbeat);
    }

    [Fact]
    public async Task NotificationDelivery_RetriesUntilReceiverRecovers()
    {
        var receiver = new RecoveringWebhookHandler();
        using var factory = new BlackoutWatchApplicationFactory(
            minimumOutageSeconds: 60,
            webhookHandler: receiver,
            notificationRetryInitialDelay: "00:00:00.100",
            notificationRetryMaxDelay: "00:00:00.200");
        await SeedHeartbeatAsync(factory, DateTimeOffset.UtcNow.AddHours(-26));
        using var client = factory.CreateClient();

        await receiver.Delivered.WaitAsync(TimeSpan.FromSeconds(5));
        var delivery = await WaitForDeliveredNotificationAsync(factory);
        Assert.Equal(2, delivery.Attempts);
        Assert.NotNull(delivery.DeliveredUtc);
        Assert.Null(delivery.LastError);
        Assert.True(receiver.FirstEventId > 0);
        Assert.Equal(receiver.FirstEventId, receiver.DeliveredEventId);
        var durationParts = receiver.DeliveredDuration.Split(':').Select(int.Parse).ToArray();
        Assert.Equal(3, durationParts.Length);
        Assert.True(durationParts[0] >= 26);
        Assert.Equal(
            receiver.DeliveredDurationSeconds,
            (durationParts[0] * 60 * 60) + (durationParts[1] * 60) + durationParts[2]);
    }

    [Fact]
    public async Task Webhook_DeliversJsonObjectWithJsonContentType()
    {
        var receiver = new RecordingWebhookHandler();
        using var factory = new BlackoutWatchApplicationFactory(
            minimumOutageSeconds: 60,
            webhookHandler: receiver);
        await SeedHeartbeatAsync(factory, DateTimeOffset.UtcNow.AddMinutes(-5));
        using var client = factory.CreateClient();

        await receiver.Delivered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("application/json", receiver.ContentType);
        Assert.Equal(JsonValueKind.Object, receiver.Payload.ValueKind);
        Assert.Equal("power_restored", receiver.Payload.GetProperty("eventType").GetString());
        Assert.True(receiver.Payload.GetProperty("eventId").GetInt32() > 0);
        Assert.True(receiver.Payload.GetProperty("durationSeconds").GetInt32() >= 300);
    }

    [Fact]
    public async Task NotificationDelivery_StopsAfterMaximumAttempts()
    {
        var receiver = new UnavailableWebhookHandler();
        using var factory = new BlackoutWatchApplicationFactory(
            minimumOutageSeconds: 60,
            webhookHandler: receiver,
            notificationRetryInitialDelay: "00:00:00.050",
            notificationRetryMaxDelay: "00:00:00.050",
            notificationMaxAttempts: 3);
        await SeedHeartbeatAsync(factory, DateTimeOffset.UtcNow.AddMinutes(-5));
        using var client = factory.CreateClient();

        var delivery = await WaitForFailedNotificationAsync(factory);

        Assert.Equal(3, delivery.Attempts);
        Assert.Equal(3, receiver.Attempts);
        Assert.NotNull(delivery.FailedUtc);
        Assert.Null(delivery.DeliveredUtc);

        await Task.Delay(200);
        Assert.Equal(3, receiver.Attempts);
    }

    [Fact]
    public async Task PendingNotification_SurvivesApplicationRestart()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"blackoutwatch-restart-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "blackoutwatch.db");
        Directory.CreateDirectory(testDirectory);

        try
        {
            var unavailableReceiver = new UnavailableWebhookHandler();
            using (var firstFactory = new BlackoutWatchApplicationFactory(
                       minimumOutageSeconds: 60,
                       webhookHandler: unavailableReceiver,
                       notificationRetryInitialDelay: "00:00:00.100",
                       notificationRetryMaxDelay: "00:00:00.200",
                       databasePath: databasePath))
            {
                await SeedHeartbeatAsync(firstFactory, DateTimeOffset.UtcNow.AddMinutes(-5));
                using var client = firstFactory.CreateClient();
                await unavailableReceiver.Attempted.WaitAsync(TimeSpan.FromSeconds(5));
                var pending = await WaitForNotificationAttemptAsync(firstFactory);
                Assert.Null(pending.DeliveredUtc);
            }

            var recoveredReceiver = new SuccessfulWebhookHandler();
            using (var secondFactory = new BlackoutWatchApplicationFactory(
                       minimumOutageSeconds: 0,
                       webhookHandler: recoveredReceiver,
                       notificationRetryInitialDelay: "00:00:00.100",
                       notificationRetryMaxDelay: "00:00:00.200",
                       databasePath: databasePath))
            {
                using var client = secondFactory.CreateClient();
                await recoveredReceiver.Delivered.WaitAsync(TimeSpan.FromSeconds(5));
                var delivered = await WaitForDeliveredNotificationAsync(secondFactory);
                Assert.True(delivered.Attempts >= 2);
            }
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static async Task SeedHeartbeatAsync(
        BlackoutWatchApplicationFactory factory,
        DateTimeOffset heartbeat)
    {
        await using var dbContext = CreateDbContext(factory);
        await dbContext.Database.MigrateAsync();
        dbContext.HeartbeatStates.Add(new HeartbeatState { Id = 1, LastHeartbeat = heartbeat });
        await dbContext.SaveChangesAsync();
    }

    private static BlackoutWatchDbContext CreateDbContext(
        BlackoutWatchApplicationFactory factory) =>
        new(new DbContextOptionsBuilder<BlackoutWatchDbContext>()
            .UseSqlite(factory.ConnectionString)
            .Options);

    private static async Task<NotificationDelivery> WaitForDeliveredNotificationAsync(
        BlackoutWatchApplicationFactory factory)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);

        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var dbContext = CreateDbContext(factory);
            var delivery = await dbContext.NotificationDeliveries
                .AsNoTracking()
                .SingleAsync();

            if (delivery.DeliveredUtc is not null)
            {
                return delivery;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The notification was not marked delivered within five seconds.");
    }

    private static async Task<NotificationDelivery> WaitForNotificationAttemptAsync(
        BlackoutWatchApplicationFactory factory)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);

        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var dbContext = CreateDbContext(factory);
            var delivery = await dbContext.NotificationDeliveries
                .AsNoTracking()
                .SingleAsync();

            if (delivery.Attempts > 0)
            {
                return delivery;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The notification was not attempted within five seconds.");
    }

    private static async Task<NotificationDelivery> WaitForFailedNotificationAsync(
        BlackoutWatchApplicationFactory factory)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);

        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var dbContext = CreateDbContext(factory);
            var delivery = await dbContext.NotificationDeliveries
                .AsNoTracking()
                .SingleAsync();

            if (delivery.FailedUtc is not null)
            {
                return delivery;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The notification did not reach its attempt limit within five seconds.");
    }

    private static async Task<DateTimeOffset> WaitForHeartbeatAfterAsync(
        BlackoutWatchApplicationFactory factory,
        DateTimeOffset previousHeartbeat)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        var dbOptions = new DbContextOptionsBuilder<BlackoutWatchDbContext>()
            .UseSqlite(factory.ConnectionString)
            .Options;

        while (DateTimeOffset.UtcNow < timeout)
        {
            await using var dbContext = new BlackoutWatchDbContext(dbOptions);
            var heartbeat = await dbContext.HeartbeatStates
                .AsNoTracking()
                .Select(state => (DateTimeOffset?)state.LastHeartbeat)
                .SingleOrDefaultAsync();

            if (heartbeat is { } value && value > previousHeartbeat)
            {
                return value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("A new heartbeat was not persisted within five seconds.");
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}

public sealed class BlackoutWatchApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? ownedTestDirectory;
    private readonly string heartbeatCron;
    private readonly int minimumOutageSeconds;
    private readonly string heartbeatRestartDelay;
    private readonly HttpMessageHandler? webhookHandler;
    private readonly string notificationRetryInitialDelay;
    private readonly string notificationRetryMaxDelay;
    private readonly int? notificationMaxAttempts;

    public BlackoutWatchApplicationFactory(
        string heartbeatCron = "0 0 0 1 1 *",
        int minimumOutageSeconds = 60,
        string heartbeatRestartDelay = "00:00:00.100",
        HttpMessageHandler? webhookHandler = null,
        string notificationRetryInitialDelay = "",
        string notificationRetryMaxDelay = "",
        int? notificationMaxAttempts = null,
        string? databasePath = null)
    {
        this.heartbeatCron = heartbeatCron;
        this.minimumOutageSeconds = minimumOutageSeconds;
        this.heartbeatRestartDelay = heartbeatRestartDelay;
        this.webhookHandler = webhookHandler;
        this.notificationRetryInitialDelay = notificationRetryInitialDelay;
        this.notificationRetryMaxDelay = notificationRetryMaxDelay;
        this.notificationMaxAttempts = notificationMaxAttempts;
        ownedTestDirectory = databasePath is null ? CreateTestDirectory() : null;
        DatabasePath = databasePath ?? Path.Combine(ownedTestDirectory!, "blackoutwatch.db");
    }

    public string DatabasePath { get; }

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        DefaultTimeout = 0,
        Pooling = false
    }.ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:BlackoutWatch", ConnectionString);
        builder.UseSetting(
            "BlackoutWatch:MinimumOutageSeconds",
            minimumOutageSeconds.ToString());
        builder.UseSetting("BlackoutWatch:HeartbeatRestartDelay", heartbeatRestartDelay);

        if (!string.IsNullOrWhiteSpace(notificationRetryInitialDelay))
        {
            builder.UseSetting(
                "BlackoutWatch:NotificationRetryInitialDelay",
                notificationRetryInitialDelay);
        }

        if (!string.IsNullOrWhiteSpace(notificationRetryMaxDelay))
        {
            builder.UseSetting(
                "BlackoutWatch:NotificationRetryMaxDelay",
                notificationRetryMaxDelay);
        }

        if (notificationMaxAttempts is not null)
        {
            builder.UseSetting(
                "BlackoutWatch:NotificationMaxAttempts",
                notificationMaxAttempts.Value.ToString());
        }

        if (webhookHandler is not null)
        {
            builder.UseSetting("BlackoutWatch:Webhook:Enabled", "true");
            builder.UseSetting("BlackoutWatch:Webhook:Url", "https://receiver.test/outages");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(
                    new TestHttpClientFactory(webhookHandler));
            });
        }

        if (!string.IsNullOrWhiteSpace(heartbeatCron))
        {
            builder.UseSetting("BlackoutWatch:HeartbeatCron", heartbeatCron);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing &&
            ownedTestDirectory is not null &&
            Directory.Exists(ownedTestDirectory))
        {
            Directory.Delete(ownedTestDirectory, recursive: true);
        }
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blackoutwatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

internal sealed class RecoveringWebhookHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource delivered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int attempts;

    public Task Delivered => delivered.Task;

    public int FirstEventId { get; private set; }

    public int DeliveredEventId { get; private set; }

    public int DeliveredDurationSeconds { get; private set; }

    public string DeliveredDuration { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var eventId = payload.GetProperty("eventId").GetInt32();

        if (Interlocked.Increment(ref attempts) == 1)
        {
            FirstEventId = eventId;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        DeliveredEventId = eventId;
        DeliveredDurationSeconds = payload.GetProperty("durationSeconds").GetInt32();
        DeliveredDuration = payload.GetProperty("duration").GetString()!;
        delivered.TrySetResult();
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

internal sealed class UnavailableWebhookHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource attempted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int attempts;

    public Task Attempted => attempted.Task;

    public int Attempts => Volatile.Read(ref attempts);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref attempts);
        attempted.TrySetResult();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}

internal sealed class RecordingWebhookHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource delivered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Delivered => delivered.Task;

    public string? ContentType { get; private set; }

    public JsonElement Payload { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ContentType = request.Content?.Headers.ContentType?.MediaType;
        Payload = (await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken)).Clone();
        delivered.TrySetResult();
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

internal sealed class SuccessfulWebhookHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource delivered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Delivered => delivered.Task;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        delivered.TrySetResult();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

internal sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
