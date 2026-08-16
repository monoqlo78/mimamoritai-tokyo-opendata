using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Web.Services;

/// <summary>Why a LIFF view is not showing household data.</summary>
public enum LiffSessionStatus
{
    /// <summary>No usable ID token was presented (or verification is not configured).</summary>
    NotSignedIn,

    /// <summary>The LINE user is genuine, but their id is not linked to any household yet.</summary>
    NotLinked,

    /// <summary>Verified and linked - <see cref="LiffSession.Status"/> carries the household view.</summary>
    Ready
}

/// <summary>
/// What the LIFF page renders. Deliberately much narrower than
/// <see cref="DashboardModel"/>: a LIFF WebView is opened by whoever holds the phone,
/// with no app sign-in behind it, so it exposes only the reassurance summary the family
/// already receives over LINE - never the device list, credentials or household settings.
/// </summary>
public sealed record LiffStatus(
    string HouseholdName,
    string ResidentName,
    RiskResult Risk,
    DailyActivity Today);

/// <summary>The outcome of resolving a LIFF ID token, with the view when there is one.</summary>
public sealed record LiffSession(LiffSessionStatus Status, string? DisplayName, LiffStatus? View)
{
    public static readonly LiffSession NotSignedIn = new(LiffSessionStatus.NotSignedIn, null, null);
}

/// <summary>
/// Turns the ID token a LIFF page hands us into the household summary shown inside LINE.
///
/// The whole point of going through <see cref="ILineIdTokenVerifier"/> first is that the
/// page's own claim about who it is cannot be trusted - a raw userId posted from the
/// browser would let anyone read any linked family's status. Only the `sub` of a token
/// LINE itself has just re-validated is used to look up the household.
/// </summary>
public sealed class LiffSessionService(
    AppDbContext db,
    ILineIdTokenVerifier verifier,
    TimeProvider clock,
    IHeatAdvisoryProvider? heatAdvisory = null)
{
    public async Task<LiffSession> ResolveAsync(string? idToken, CancellationToken ct = default)
    {
        var identity = await verifier.VerifyAsync(idToken, ct);
        if (identity is null)
        {
            return LiffSession.NotSignedIn;
        }

        var householdId = await db.LineRecipients
            .Where(r => r.LineUserId == identity.LineUserId && r.IsActive)
            .OrderByDescending(r => r.LastSeenAt)
            .Select(r => (Guid?)r.HouseholdId)
            .FirstOrDefaultAsync(ct);

        if (householdId is not { } resolved)
        {
            return new LiffSession(LiffSessionStatus.NotLinked, identity.DisplayName, null);
        }

        var view = await BuildStatusAsync(resolved, ct);
        return view is null
            ? new LiffSession(LiffSessionStatus.NotLinked, identity.DisplayName, null)
            : new LiffSession(LiffSessionStatus.Ready, identity.DisplayName, view);
    }

    private async Task<LiffStatus?> BuildStatusAsync(Guid householdId, CancellationToken ct)
    {
        var household = await db.Households
            .Where(h => h.Id == householdId)
            .Select(h => h.Name)
            .FirstOrDefaultAsync(ct);

        if (household is null)
        {
            return null;
        }

        var residentName = await db.People
            .Where(p => p.HouseholdId == householdId && p.Role == PersonRole.Resident)
            .Select(p => p.DisplayName)
            .FirstOrDefaultAsync(ct);

        // Same 14-day window and same evaluation the dashboard and the LINE alert use, so
        // the CG in LINE never contradicts what the web view or the last push said.
        var activity = new ActivityService(db);
        var recent = await activity.GetRecentAsync(householdId, 14, ct);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());
        var today = recent.LastOrDefault(d => d.Date == todayDate)
            ?? new DailyActivity(todayDate, null, null, 0, 0, 0);
        var risks = new RiskAssessmentService(db, clock, heatAdvisory);
        var heat = await risks.GetHeatAsync(ct);
        var cooling = await risks.LoadCoolingAsync(householdId, ct);
        var risk = RiskAssessmentService.Evaluate(
            today, recent, HouseholdTime.LocalTime(clock.GetUtcNow()), null, null, heat, cooling);

        return new LiffStatus(household, residentName ?? "ご家族", risk, today);
    }

    /// <summary>
    /// Maps a risk level onto the greeting names <c>mimamori-mascot-3d.js</c> understands,
    /// so the CG's animation matches the message instead of always waving.
    /// </summary>
    public static string MascotGreeting(RiskLevel level) => level switch
    {
        RiskLevel.High => "emergency",
        RiskLevel.Medium => "concern",
        _ => "okay"
    };
}
