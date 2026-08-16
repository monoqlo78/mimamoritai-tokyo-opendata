using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    /// <summary>
    /// SQL Server schema that isolates this application's objects from the other
    /// applications sharing the same Azure SQL database.
    /// </summary>
    public const string DefaultSchema = "mimamori";

    public DbSet<Household> Households => Set<Household>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceEvent> DeviceEvents => Set<DeviceEvent>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<FamilyMessage> FamilyMessages => Set<FamilyMessage>();
    public DbSet<RiskAssessment> RiskAssessments => Set<RiskAssessment>();
    public DbSet<DailyActivitySummary> DailyActivitySummaries => Set<DailyActivitySummary>();
    public DbSet<AiRequestLog> AiRequestLogs => Set<AiRequestLog>();
    public DbSet<WatchAlert> WatchAlerts => Set<WatchAlert>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<HouseholdMember> HouseholdMembers => Set<HouseholdMember>();
    public DbSet<LineRecipient> LineRecipients => Set<LineRecipient>();
    public DbSet<SwitchBotConnection> SwitchBotConnections => Set<SwitchBotConnection>();
    public DbSet<PlugMiniReading> PlugMiniReadings => Set<PlugMiniReading>();
    public DbSet<HeatReading> HeatReadings => Set<HeatReading>();
    public DbSet<LineLinkCode> LineLinkCodes => Set<LineLinkCode>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // The production database is shared with other applications, so every table,
        // view and the migrations history live in a dedicated `mimamori` schema.
        // SQLite has no schema concept, so the demo fallback stays unqualified.
        if (Database.IsSqlServer())
        {
            b.HasDefaultSchema(DefaultSchema);
        }

        // SQLite has no native DateTimeOffset type, so EF cannot translate comparisons
        // such as `e.OccurredAtUtc >= from`. Storing the value in an order-preserving
        // binary form keeps the demo fallback fully queryable. SQL Server is untouched.
        if (Database.IsSqlite())
        {
            foreach (var entityType in b.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    {
                        property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
                    }
                }
            }
        }

        b.Entity<Household>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.DataSourceMode).HasConversion<string>().HasMaxLength(32);
        });

        b.Entity<Person>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.HouseholdId);
            e.HasOne(x => x.Household).WithMany(x => x.People).HasForeignKey(x => x.HouseholdId);
        });

        b.Entity<Device>(e =>
        {
            e.Property(x => x.ExternalDeviceId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Alias).HasMaxLength(64).IsRequired();
            e.Property(x => x.Room).HasMaxLength(64);
            e.Property(x => x.DisplayNameOverride).HasMaxLength(60);
            e.Property(x => x.RoomOverride).HasMaxLength(64);
            e.Property(x => x.DeviceType).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.SafetyClass).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.HouseholdId, x.Alias }).IsUnique();
            e.HasOne(x => x.Household).WithMany(x => x.Devices).HasForeignKey(x => x.HouseholdId);
        });

        b.Entity<DeviceEvent>(e =>
        {
            e.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            e.Property(x => x.State).HasMaxLength(32).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(16);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.HouseholdId, x.OccurredAtUtc });
            e.HasIndex(x => new { x.DeviceId, x.OccurredAtUtc });
            // Supports the "unpublished rows, oldest first" query the Fabric stream
            // publish background service runs every cycle.
            e.HasIndex(x => x.PublishedToStreamAtUtc);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceCommand>(e =>
        {
            e.Property(x => x.OriginalText).HasMaxLength(1024);
            e.Property(x => x.FailureReason).HasMaxLength(512);
            e.Property(x => x.AiResolvedModel).HasMaxLength(128);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.HouseholdId, x.RequestedAtUtc });
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<FamilyMessage>(e =>
        {
            e.Property(x => x.Content).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.MessageType).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.HouseholdId, x.OccurredAtUtc });
            e.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<RiskAssessment>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(512);
            e.Property(x => x.RiskLevel).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.HouseholdId, x.CreatedAtUtc });
        });

        b.Entity<DailyActivitySummary>(e =>
        {
            e.Property(x => x.RiskLevel).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.HouseholdId, x.Date }).IsUnique();
        });

        b.Entity<AiRequestLog>(e =>
        {
            e.Property(x => x.Purpose).HasMaxLength(64);
            e.Property(x => x.Router).HasMaxLength(64);
            e.Property(x => x.ResolvedModel).HasMaxLength(128);
            e.Property(x => x.Error).HasMaxLength(256);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        b.Entity<WatchAlert>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(512);
            e.Property(x => x.Message).HasMaxLength(1024);
            e.Property(x => x.Error).HasMaxLength(512);
            e.Property(x => x.RiskLevel).HasConversion<string>().HasMaxLength(32);
            // Dedup lookup: latest alert for a person at a given risk level within the cooldown window.
            e.HasIndex(x => new { x.PersonId, x.RiskLevel, x.SentAtUtc });
        });

        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.IdentityProvider).HasMaxLength(32).IsRequired();
            e.Property(x => x.ExternalSubject).HasMaxLength(256).IsRequired();
            e.Property(x => x.LineUserId).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256);
            e.HasIndex(x => new { x.IdentityProvider, x.ExternalSubject }).IsUnique();
            e.HasIndex(x => x.LineUserId);
        });

        b.Entity<HouseholdMember>(e =>
        {
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.HouseholdId, x.AppUserId }).IsUnique();
            e.HasOne(x => x.Household).WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AppUser).WithMany().HasForeignKey(x => x.AppUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LineRecipient>(e =>
        {
            e.Property(x => x.LineUserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.HasIndex(x => new { x.HouseholdId, x.LineUserId }).IsUnique();
            e.HasOne(x => x.Household).WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SwitchBotConnection>(e =>
        {
            // Encrypted blobs can be sizeable (base64 Data Protection payloads);
            // 4000 comfortably covers them on both SQL Server (nvarchar) and SQLite.
            e.Property(x => x.EncryptedToken).HasMaxLength(4000).IsRequired();
            e.Property(x => x.EncryptedSecret).HasMaxLength(4000).IsRequired();
            e.Property(x => x.LastErrorMessage).HasMaxLength(512);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            // One connection row per household.
            e.HasIndex(x => x.HouseholdId).IsUnique();
            e.HasOne(x => x.Household).WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlugMiniReading>(e =>
        {
            e.HasIndex(x => new { x.HouseholdId, x.DeviceId, x.OccurredAtUtc }).IsUnique();
            // Supports the "unpublished rows, oldest first" query the Fabric stream
            // publish path runs every cycle, mirroring DeviceEvent.
            e.HasIndex(x => x.PublishedToStreamAtUtc);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HeatReading>(e =>
        {
            e.Property(x => x.PointCode).HasMaxLength(16).IsRequired();
            e.Property(x => x.AreaName).HasMaxLength(64).IsRequired();
            // One row per point per observation time: the provider re-reads the same
            // forecast column for up to CacheMinutes, and re-fetching must not create
            // a duplicate observation.
            e.HasIndex(x => new { x.PointCode, x.ObservedAtUtc }).IsUnique();
            e.HasIndex(x => x.PublishedToStreamAtUtc);
        });

        b.Entity<LineLinkCode>(e =>
        {
            e.Property(x => x.CodeHash).HasMaxLength(128).IsRequired();
            // Redemption looks up an active code by hash; expired/used codes are
            // deliberately not unique (a household may accumulate many over time).
            e.HasIndex(x => new { x.CodeHash, x.UsedAtUtc, x.ExpiresAtUtc });
            e.HasIndex(x => x.HouseholdId);
            e.HasOne(x => x.Household).WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(b);
    }
}
