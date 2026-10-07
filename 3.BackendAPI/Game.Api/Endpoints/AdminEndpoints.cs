using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Game.Api.Admin;
using Game.Api.Auth;
using Game.Api.Models;

namespace Game.Api.Endpoints;

// Tools for running the game, for accounts with the Admin role only (see AdminRoleSync).
// Every change needs a reason and is written to the audit log.
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
                       .WithTags("Admin")
                       .RequireAuthorization(GameRoles.AdminPolicy);

        // GET: /api/v1/admin/players?search=ali, players whose name contains the text
        group.MapGet("/players", async (AdminService admin, string? search, int? take, CancellationToken cancellationToken) =>
            Results.Ok(await admin.SearchAsync(search, take ?? 25, cancellationToken)));

        group.MapGet("/players/{id:guid}", async (Guid id, AdminService admin, CancellationToken cancellationToken) =>
            await admin.GetAsync(id, cancellationToken) is { } player ? Results.Ok(player) : Results.NotFound());

        group.MapPost("/players/{id:guid}/suspend", async (Guid id, SuspendRequest request, ClaimsPrincipal user, AdminService admin, CancellationToken cancellationToken) =>
            ToResult(await admin.SuspendAsync(Actor(user), id, request, cancellationToken)));

        group.MapPost("/players/{id:guid}/reinstate", async (Guid id, ReinstateRequest request, ClaimsPrincipal user, AdminService admin, CancellationToken cancellationToken) =>
            ToResult(await admin.ReinstateAsync(Actor(user), id, request, cancellationToken)));

        group.MapPost("/players/{id:guid}/gold", async (Guid id, GoldCorrectionRequest request, ClaimsPrincipal user, AdminService admin, CancellationToken cancellationToken) =>
            ToResult(await admin.CorrectGoldAsync(Actor(user), id, request, cancellationToken)));

        // GET: /api/v1/admin/audit?before=120, newest first; pass the last id seen to page back
        group.MapGet("/audit", async (AdminService admin, int? take, long? before, CancellationToken cancellationToken) =>
            Results.Ok(await admin.AuditLogAsync(take ?? 25, before, cancellationToken)));
    }

    private static AdminActor Actor(ClaimsPrincipal user) =>
        new(user.GetPlayerId(), user.FindFirstValue(JwtRegisteredClaimNames.UniqueName) ?? "admin");

    private static IResult ToResult(AdminChange change) => change.Status switch
    {
        AdminChangeStatus.Done => Results.Ok(change.Player),
        AdminChangeStatus.NotFound => Results.NotFound(),
        AdminChangeStatus.Invalid => Results.Problem(change.Message, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Problem(change.Message, statusCode: StatusCodes.Status409Conflict),
    };
}
