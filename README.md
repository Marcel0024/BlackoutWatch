# BlackoutWatch

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Docker](https://img.shields.io/badge/Docker-ready-2496ED?logo=docker&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)

## What it is

BlackoutWatch is a small, self-hosted service that detects and records when its host was unavailable. It is intended to run continuously in Docker on a home server, NAS, or Raspberry Pi.

When the host comes back, BlackoutWatch calculates the downtime and stores it in SQLite. It can notify you by publishing an MQTT event, sending an HTTP webhook, or both. MQTT and webhooks are optional, independent channels. The recorded outage history is also available through an HTTP API.

## How it works

1. BlackoutWatch writes a UTC heartbeat to a persistent SQLite database every five seconds by default.
2. A power loss or shutdown stops the heartbeat.
3. Docker restarts BlackoutWatch when the host returns.
4. BlackoutWatch compares the last heartbeat with the current time.
5. If the gap is at least `MinimumOutageSeconds`, it records an outage and queues a notification for each enabled channel: MQTT, webhook, or both.
6. Notification delivery runs in the background and retries temporary failures.

BlackoutWatch detects that the host stopped running; it cannot tell a power failure from an intentional shutdown, crash, or manual container stop. Set a suitable minimum duration to ignore short restarts.

## Run it

You need Docker with Docker Compose. Create `compose.yaml`:

```yaml
services:
  blackoutwatch:
    image: ghcr.io/marcel0024/blackoutwatch:latest
    container_name: blackoutwatch
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      BlackoutWatch__Mqtt__Enabled: "true"
      BlackoutWatch__Mqtt__Host: "${MQTT_HOST}"
      BlackoutWatch__Mqtt__Port: "${MQTT_PORT:-1883}"
      BlackoutWatch__Mqtt__UseTls: "${MQTT_USE_TLS:-false}"
      BlackoutWatch__Mqtt__Username: "${MQTT_USERNAME}"
      BlackoutWatch__Mqtt__Password: "${MQTT_PASSWORD}"
      BlackoutWatch__Mqtt__Topic: "blackoutwatch/events"
      BlackoutWatch__Webhook__Enabled: "true"
      BlackoutWatch__Webhook__Url: "${WEBHOOK_URL}"
    volumes:
      - blackoutwatch-data:/data

volumes:
  blackoutwatch-data:
```

Create a `.env` file beside `compose.yaml` and replace the example connection details:

```dotenv
MQTT_HOST=192.168.1.10
MQTT_PORT=1883
MQTT_USE_TLS=false
MQTT_USERNAME=blackoutwatch
MQTT_PASSWORD=replace-with-your-password
WEBHOOK_URL=https://example.net/hooks/power-restored
```

Start it and check its health:

```bash
docker compose up -d
curl http://localhost:8080/health
```

Expected response:

```json
{"status":"healthy"}
```

Keep both the `/data` volume and the automatic restart policy. The volume preserves the last heartbeat; automatic restart lets BlackoutWatch detect restoration when the host boots.

To run from source instead, install the .NET 10 SDK and use a writable database path:

```powershell
$env:ConnectionStrings__BlackoutWatch = "Data Source=.\blackoutwatch.db"
dotnet run
```

## MQTT examples

The Compose example enables MQTT on `blackoutwatch/events`. Use port `8883` and set `MQTT_USE_TLS=true` when the broker uses TLS. If the broker is another service in the same Compose project, set `MQTT_HOST` to its service name, such as `mosquitto`.

Subscribe from a machine with the Mosquitto clients installed:

```bash
mosquitto_sub \
  -h 192.168.1.10 \
  -u blackoutwatch \
  -P 'your-password' \
  -t blackoutwatch/events \
  -v
```

BlackoutWatch publishes QoS 1, non-retained messages like this:

```json
{
  "eventId": 42,
  "eventType": "power_restored",
  "outageStart": "2026-08-19T12:10:30+00:00",
  "restored": "2026-08-19T12:14:12+00:00",
  "durationSeconds": 222,
  "duration": "00:03:42"
}
```

`eventId` stays the same when a delivery is retried, so consumers can use it to ignore duplicates.

### Home Assistant

BlackoutWatch publishes events rather than MQTT Discovery data. Once Home Assistant is connected to the same broker, an automation can listen on the configured topic:

```yaml
alias: Power restored notification
triggers:
  - trigger: mqtt
    topic: blackoutwatch/events
actions:
  - action: notify.mobile_app_your_phone
    data:
      title: Power restored
      message: >-
        The server was unavailable for
        {{ trigger.payload_json.duration }}.
        Power returned at {{ trigger.payload_json.restored }}.
mode: queued
```

Replace `notify.mobile_app_your_phone` with the notification action for your device.

## HTTP examples

### Read outages

Check health and fetch recorded outages:

```bash
curl http://localhost:8080/health
curl http://localhost:8080/api/outages
```

`GET /api/outages` returns the newest outage first:

```json
[
  {
    "outageStart": "2026-08-19T12:10:30+00:00",
    "restored": "2026-08-19T12:14:12+00:00",
    "durationSeconds": 222,
    "duration": "00:03:42"
  }
]
```

### Send a webhook

The Compose example enables the webhook at `WEBHOOK_URL`. For every detected outage, BlackoutWatch sends an `application/json` POST containing the same object shown in the MQTT example. The receiver must return a successful HTTP status. Failed deliveries are kept in SQLite and retried independently of MQTT.

## All settings

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__BlackoutWatch` | `Data Source=/data/blackoutwatch.db` | SQLite database location |
| `BlackoutWatch__HeartbeatCron` | `*/5 * * * * *` | UTC heartbeat schedule; accepts five or six cron fields |
| `BlackoutWatch__HeartbeatRestartDelay` | `00:00:05` | Delay before retrying after a heartbeat failure |
| `BlackoutWatch__NotificationRetryInitialDelay` | `00:00:05` | Delay after the first notification failure |
| `BlackoutWatch__NotificationRetryMaxDelay` | `00:05:00` | Maximum exponential notification retry delay |
| `BlackoutWatch__NotificationMaxAttempts` | `10` | Maximum delivery attempts for each notification channel |
| `BlackoutWatch__MinimumOutageSeconds` | `60` | Minimum gap to record; `0` disables detection |
| `BlackoutWatch__Mqtt__Enabled` | `false` | Enable MQTT events |
| `BlackoutWatch__Mqtt__Host` | `localhost` | MQTT broker hostname or address |
| `BlackoutWatch__Mqtt__Port` | `1883` | MQTT broker port |
| `BlackoutWatch__Mqtt__UseTls` | `false` | Enable MQTT TLS |
| `BlackoutWatch__Mqtt__Username` | unset | Optional MQTT username |
| `BlackoutWatch__Mqtt__Password` | unset | Optional MQTT password |
| `BlackoutWatch__Mqtt__Topic` | `blackoutwatch/events` | MQTT event topic |
| `BlackoutWatch__Webhook__Enabled` | `false` | Enable HTTP webhook delivery |
| `BlackoutWatch__Webhook__Url` | unset | Absolute HTTP or HTTPS receiver URL |

Run the test suite with:

```bash
dotnet test BlackoutWatch.slnx
```

## License

BlackoutWatch is available under the [MIT License](LICENSE).
