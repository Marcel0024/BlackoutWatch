using System.ComponentModel.DataAnnotations;
using Cronos;

namespace BlackoutWatch.Configuration;

public sealed class BlackoutWatchOptions : IValidatableObject
{
    public const string SectionName = "BlackoutWatch";

    [Required, CronExpression]
    public string HeartbeatCron { get; init; } = "*/5 * * * * *";

    public TimeSpan HeartbeatRestartDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan NotificationRetryInitialDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan NotificationRetryMaxDelay { get; init; } = TimeSpan.FromMinutes(5);

    [Range(1, int.MaxValue)]
    public int NotificationMaxAttempts { get; init; } = 10;

    public int MinimumOutageSeconds { get; init; } = 60;

    public WebhookOptions Webhook { get; init; } = new();

    public MqttOptions Mqtt { get; init; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (HeartbeatRestartDelay <= TimeSpan.Zero)
        {
            yield return new(
                "HeartbeatRestartDelay must be greater than zero.",
                [nameof(HeartbeatRestartDelay)]);
        }

        if (NotificationRetryInitialDelay <= TimeSpan.Zero)
        {
            yield return new(
                "NotificationRetryInitialDelay must be greater than zero.",
                [nameof(NotificationRetryInitialDelay)]);
        }

        if (NotificationRetryMaxDelay < NotificationRetryInitialDelay)
        {
            yield return new(
                "NotificationRetryMaxDelay must be at least NotificationRetryInitialDelay.",
                [nameof(NotificationRetryMaxDelay)]);
        }

        if (Webhook.Enabled &&
            (!Uri.TryCreate(Webhook.Url, UriKind.Absolute, out var webhookUri) ||
             (webhookUri.Scheme != Uri.UriSchemeHttp && webhookUri.Scheme != Uri.UriSchemeHttps)))
        {
            yield return new(
                "Webhook.Url must be an absolute HTTP or HTTPS URL when webhooks are enabled.",
                [nameof(Webhook)]);
        }

        if (Mqtt.Enabled && string.IsNullOrWhiteSpace(Mqtt.Host))
        {
            yield return new("Mqtt.Host is required when MQTT is enabled.", [nameof(Mqtt)]);
        }

        if (Mqtt.Enabled && string.IsNullOrWhiteSpace(Mqtt.Topic))
        {
            yield return new("Mqtt.Topic is required when MQTT is enabled.", [nameof(Mqtt)]);
        }

        if (Mqtt.Port is < 1 or > 65535)
        {
            yield return new("Mqtt.Port must be between 1 and 65535.", [nameof(Mqtt)]);
        }
    }
}

public sealed class WebhookOptions
{
    public bool Enabled { get; init; }

    public string? Url { get; init; }
}

public sealed class MqttOptions
{
    public bool Enabled { get; init; }

    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 1883;

    public bool UseTls { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string Topic { get; init; } = "blackoutwatch/events";
}

[AttributeUsage(AttributeTargets.Property)]
internal sealed class CronExpressionAttribute : ValidationAttribute
{
    public CronExpressionAttribute()
        : base("The cron expression is invalid. Use a five-field expression or a six-field expression with seconds.")
    {
    }

    public override bool IsValid(object? value) =>
        value is string expression && CronSchedule.TryParse(expression, out _);
}

internal static class CronSchedule
{
    public static CronExpression Parse(string expression)
    {
        if (CronExpression.TryParse(expression, CronFormat.IncludeSeconds, out var withSeconds))
        {
            return withSeconds;
        }

        return CronExpression.Parse(expression, CronFormat.Standard);
    }

    public static bool TryParse(string expression, out CronExpression? cronExpression)
    {
        if (CronExpression.TryParse(expression, CronFormat.IncludeSeconds, out cronExpression))
        {
            return true;
        }

        return CronExpression.TryParse(expression, CronFormat.Standard, out cronExpression);
    }
}
