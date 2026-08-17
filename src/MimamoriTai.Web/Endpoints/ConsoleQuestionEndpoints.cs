using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;

using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Web.Endpoints;

public sealed record ConsoleQuestionRequest(string Question);

/// <summary>
/// The console's "ask a question" endpoint.
///
/// The operator console is a static site on Fabric, so it cannot hold a model key —
/// anything shipped in that bundle is readable by whoever loads the page. The call
/// therefore lands here, where the key stays server-side and the figures the model is
/// allowed to use come from the app database rather than from the browser.
///
/// Because the caller is a browser on another origin and cannot present this app's
/// session cookie, the endpoint is anonymous. That is bounded rather than trusted:
/// the question is capped in length, the origin is restricted, and a rate limiter
/// caps how much can ever be spent through it — a fixed window shared by all callers,
/// so a scripted caller cannot run up a bill.
/// </summary>
public static class ConsoleQuestionEndpoints
{
    public const string CorsPolicy = "console-question";
    public const string RateLimitPolicy = "console-question";

    /// <summary>
    /// Shared ceiling on model spend through this endpoint. Generous for a demo
    /// audience clicking through the console, far too small to be worth abusing.
    /// </summary>
    private const int RequestsPerMinute = 20;

    public static IServiceCollection AddConsoleQuestionSupport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The console's own origin, and nothing else. Configured rather than hardcoded
        // because the Fabric static-hosting host name is assigned at deploy time.
        var origins = configuration.GetSection("ConsoleQuestion:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
        {
            if (origins.Length == 0)
            {
                // No origin configured means the browser console is not wired up yet.
                // Deny rather than fall back to "*", which would publish this endpoint
                // to every page on the internet.
                policy.WithOrigins("https://localhost");
            }
            else
            {
                policy.WithOrigins(origins);
            }

            policy.WithMethods("POST").WithHeaders("Content-Type");
        }));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter(RateLimitPolicy, limiter =>
            {
                limiter.PermitLimit = RequestsPerMinute;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });

        return services;
    }

    public static IEndpointRouteBuilder MapConsoleQuestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/console/ask", async (
            ConsoleQuestionRequest request,
            IConsoleQuestionService questions,
            CancellationToken ct) =>
        {
            if (!questions.IsConfigured)
            {
                return Results.Json(
                    new { error = "AI の接続設定がないため、この環境では質問にお答えできません。" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var answer = await questions.AskAsync(request.Question ?? string.Empty, ct);

            // A failed answer still carries its evidence, and the caller renders it, so
            // this is 200 with success:false rather than an error status that would
            // throw the body away.
            return Results.Ok(new
            {
                success = answer.Success,
                answer = answer.Answer,
                model = answer.Model,
                evidence = answer.Evidence,
                answeredAt = answer.AnsweredAt,
                error = answer.Error
            });
        })
        .WithName("PostConsoleQuestion")
        .RequireCors(CorsPolicy)
        .RequireRateLimiting(RateLimitPolicy)
        .AllowAnonymous()
        .DisableAntiforgery();

        return app;
    }
}
