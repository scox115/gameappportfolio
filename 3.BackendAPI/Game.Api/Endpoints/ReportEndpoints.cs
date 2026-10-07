using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Models;
using Game.Api.Moderation;

namespace Game.Api.Endpoints;

// Players report offensive hero names and portraits for an admin to look at.
// See docs/adr/0024-moderation-and-reports.md.
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        // POST: /api/v1/players/{id}/reports { "reason": "Name", "note": "..." }
        // 202 whether or not this player had already reported it, so a second click is harmless.
        app.MapPost("/players/{id:guid}/reports", async (
            Guid id,
            ReportRequest request,
            ClaimsPrincipal user,
            ModerationService moderation,
            CancellationToken cancellationToken) =>
        {
            var result = await moderation.ReportAsync(user.GetPlayerId(), id, request, cancellationToken);
            return result.Status switch
            {
                ReportStatus.Received => Results.Accepted(),
                ReportStatus.NotFound => Results.NotFound(),
                _ => Results.Problem(result.Message, statusCode: StatusCodes.Status400BadRequest),
            };
        })
        .WithTags("Reports")
        .RequireAuthorization()
        .RequireRateLimiting(RateLimits.Report);
    }
}
