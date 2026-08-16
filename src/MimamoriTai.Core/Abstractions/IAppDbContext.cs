using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Abstractions;

/// <summary>Database surface used by the application services in Core.</summary>
public interface IAppDbContext
{
    DbSet<Household> Households { get; }
    DbSet<Person> People { get; }
    DbSet<Device> Devices { get; }
    DbSet<DeviceEvent> DeviceEvents { get; }
    DbSet<DeviceCommand> DeviceCommands { get; }
    DbSet<FamilyMessage> FamilyMessages { get; }
    DbSet<RiskAssessment> RiskAssessments { get; }
    DbSet<DailyActivitySummary> DailyActivitySummaries { get; }
    DbSet<AiRequestLog> AiRequestLogs { get; }
    DbSet<WatchAlert> WatchAlerts { get; }
    DbSet<AppUser> AppUsers { get; }
    DbSet<HouseholdMember> HouseholdMembers { get; }
    DbSet<LineRecipient> LineRecipients { get; }
    DbSet<SwitchBotConnection> SwitchBotConnections { get; }
    DbSet<PlugMiniReading> PlugMiniReadings { get; }
    DbSet<HeatReading> HeatReadings { get; }
    DbSet<LineLinkCode> LineLinkCodes { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
