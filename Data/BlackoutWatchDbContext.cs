using Microsoft.EntityFrameworkCore;

namespace BlackoutWatch.Data;

public sealed class BlackoutWatchDbContext(
    DbContextOptions<BlackoutWatchDbContext> options) : DbContext(options)
{
    public DbSet<HeartbeatState> HeartbeatStates => Set<HeartbeatState>();

    public DbSet<Outage> Outages => Set<Outage>();

    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HeartbeatState>(entity =>
        {
            entity.ToTable("State");
            entity.HasKey(state => state.Id);
            entity.Property(state => state.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<Outage>(entity =>
        {
            entity.ToTable("Outages");
            entity.HasKey(outage => outage.Id);
        });

        modelBuilder.Entity<NotificationDelivery>(entity =>
        {
            entity.ToTable("NotificationDeliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.Property(delivery => delivery.Channel)
                .HasConversion<string>()
                .HasMaxLength(16);
            entity.Property(delivery => delivery.LastError).HasMaxLength(2_000);
            entity.HasIndex(delivery => new { delivery.OutageId, delivery.Channel }).IsUnique();
            entity.HasIndex(delivery => new { delivery.DeliveredUtc, delivery.NextAttemptUtc });
            entity.HasOne(delivery => delivery.Outage)
                .WithMany(outage => outage.NotificationDeliveries)
                .HasForeignKey(delivery => delivery.OutageId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class HeartbeatState
{
    public int Id { get; set; }

    public DateTimeOffset LastHeartbeat { get; set; }
}

public sealed class Outage
{
    public int Id { get; set; }

    public DateTimeOffset OutageStartUtc { get; set; }

    public DateTimeOffset RestoredUtc { get; set; }

    public int DurationSeconds { get; set; }

    public List<NotificationDelivery> NotificationDeliveries { get; } = [];
}

public sealed class NotificationDelivery
{
    public int Id { get; set; }

    public int OutageId { get; set; }

    public Outage Outage { get; set; } = null!;

    public NotificationChannel Channel { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptUtc { get; set; }

    public DateTimeOffset? DeliveredUtc { get; set; }

    public DateTimeOffset? FailedUtc { get; set; }

    public string? LastError { get; set; }
}

public enum NotificationChannel
{
    Webhook,
    Mqtt
}
