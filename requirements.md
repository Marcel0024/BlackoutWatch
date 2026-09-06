# BlackoutWatch requirements

BlackoutWatch is a self-hosted ASP.NET Core service that detects periods when its host was unavailable. It is intended to run in Docker with a persistent SQLite database and an automatic container restart policy.

## Heartbeat and outage detection

- `HeartbeatBackgroundService.StartAsync` performs startup outage detection exactly once before heartbeat recording starts.
- Outage detection compares the single stored UTC heartbeat with the current UTC time.
- A gap is recorded as an outage when it is at least `BlackoutWatch:MinimumOutageSeconds`.
- A missing or future-dated heartbeat does not create an outage.
- A non-positive minimum outage duration disables outage detection while heartbeat recording continues.
- Every outage stores its UTC start, UTC restoration time, and duration in whole seconds.
- `HeartbeatBackgroundService.ExecuteAsync` writes the first heartbeat immediately and schedules later heartbeats with Cronos.
- Both standard five-field cron and six-field cron with seconds are accepted and evaluated in UTC.
- The default heartbeat cron is `*/5 * * * * *`, or every five seconds.
- If a heartbeat iteration fails, it is logged and retried after `BlackoutWatch:HeartbeatRestartDelay` without rerunning startup detection.
- If startup detection fails, the error is logged and heartbeat recording continues.
- An unhandled `BackgroundService` exception stops the host so the container restart policy can restart the application.

## Persistence

- `BlackoutWatchDbContext` uses SQLite and EF Core migrations.
- SQLite uses the standard `ConnectionStrings:BlackoutWatch` configuration key, which defaults to `Data Source=/data/blackoutwatch.db`.
- Heartbeat state, recorded outages, and notification delivery state survive application restarts when `/data` is persistent.
- Successful EF Core database commands, including routine heartbeat writes, are not logged; database command warnings and errors remain enabled.

## Notifications

- Every detected restoration produces one JSON payload containing a stable `eventId`, `eventType`, `outageStart`, `restored`, `durationSeconds`, and an `HH:mm:ss` `duration` whose hours can exceed 23.
- An enabled webhook receives the payload as an HTTP JSON POST.
- Enabled MQTT publishes the payload to the configured broker and topic with QoS 1 and without retention.
- MQTT supports a configurable host, port, username, password, and TLS.
- The outage and one pending delivery for each enabled channel are stored in a single database transaction before network delivery begins.
- A background service attempts webhook and MQTT delivery independently.
- Failed deliveries are retried with exponential backoff from `BlackoutWatch:NotificationRetryInitialDelay`, capped by `BlackoutWatch:NotificationRetryMaxDelay`, for at most `BlackoutWatch:NotificationMaxAttempts` total attempts.
- Delivery attempts, last errors, next retry times, successful delivery times, and permanently failed delivery times are persisted in SQLite.
- Delivery is at least once; the stable `eventId` allows receivers to detect duplicates.

## HTTP API

- `GET /health` returns HTTP 200 with a healthy status.
- `GET /api/outages` returns recorded outages as JSON ordered newest first, including `durationSeconds` and human-readable `duration` values.
- Unknown routes return HTTP 404.

## Configuration

- Configuration is bound from the ASP.NET Core `BlackoutWatch` section and validated during startup.
- Environment variables use ASP.NET Core double-underscore nesting, such as `BlackoutWatch__Mqtt__Host`.
- Invalid cron expressions, heartbeat or notification retry delays, webhook URLs, MQTT ports, and required MQTT values prevent application startup.

| Setting | Default |
| --- | --- |
| `ConnectionStrings:BlackoutWatch` | `Data Source=/data/blackoutwatch.db` |
| `BlackoutWatch:HeartbeatCron` | `*/5 * * * * *` |
| `BlackoutWatch:HeartbeatRestartDelay` | `00:00:05` |
| `BlackoutWatch:NotificationRetryInitialDelay` | `00:00:05` |
| `BlackoutWatch:NotificationRetryMaxDelay` | `00:05:00` |
| `BlackoutWatch:NotificationMaxAttempts` | `10` |
| `BlackoutWatch:MinimumOutageSeconds` | `60` |
| `BlackoutWatch:Webhook:Enabled` | `false` |
| `BlackoutWatch:Webhook:Url` | unset |
| `BlackoutWatch:Mqtt:Enabled` | `false` |
| `BlackoutWatch:Mqtt:Host` | `localhost` |
| `BlackoutWatch:Mqtt:Port` | `1883` |
| `BlackoutWatch:Mqtt:UseTls` | `false` |
| `BlackoutWatch:Mqtt:Username` | unset |
| `BlackoutWatch:Mqtt:Password` | unset |
| `BlackoutWatch:Mqtt:Topic` | `blackoutwatch/events` |
