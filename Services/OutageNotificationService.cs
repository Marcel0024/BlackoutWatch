using System.Text;
using System.Text.Json;
using BlackoutWatch.Configuration;
using BlackoutWatch.Data;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace BlackoutWatch.Services;

public sealed class OutageNotificationService(
    IOptions<BlackoutWatchOptions> options,
    IHttpClientFactory httpClientFactory)
{
    private readonly BlackoutWatchOptions options = options.Value;

    public Task SendAsync(
        NotificationChannel channel,
        Outage outage,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            eventId = outage.Id,
            eventType = "power_restored",
            outageStart = outage.OutageStartUtc,
            restored = outage.RestoredUtc,
            durationSeconds = outage.DurationSeconds,
            duration = OutageDuration.Format(outage.DurationSeconds)
        });

        return channel switch
        {
            NotificationChannel.Webhook => SendWebhookAsync(payload, cancellationToken),
            NotificationChannel.Mqtt => PublishMqttAsync(payload, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
        };
    }

    private async Task SendWebhookAsync(string payload, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(payload);

        using var response = await httpClientFactory
            .CreateClient()
            .PostAsync(options.Webhook.Url, content, cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private async Task PublishMqttAsync(string payload, CancellationToken cancellationToken)
    {
        var mqttOptionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Mqtt.Host, options.Mqtt.Port);

        if (!string.IsNullOrWhiteSpace(options.Mqtt.Username))
        {
            mqttOptionsBuilder.WithCredentials(options.Mqtt.Username, options.Mqtt.Password);
        }

        if (options.Mqtt.UseTls)
        {
            mqttOptionsBuilder.WithTlsOptions(tls => tls.UseTls());
        }

        var factory = new MqttClientFactory();

        using var client = factory.CreateMqttClient();

        await client.ConnectAsync(mqttOptionsBuilder.Build(), cancellationToken);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(options.Mqtt.Topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await client.PublishAsync(message, cancellationToken);
        await client.DisconnectAsync(cancellationToken: cancellationToken);
    }

}
