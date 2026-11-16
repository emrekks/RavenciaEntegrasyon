using MarketplaceHub.Api.Common;
using MarketplaceHub.Api.Security;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Api.Settings;

// API-GUVENLIK-6 (owner decision 16 Nov 2026): the invited side. Ayarlar › Kullanıcılar invites an existing account
// instead of adding it (UserManagementEndpoints.CreateAsync); the person sees the invitation after signing in to a
// workspace of their own and answers it here.
//   GET  /api/v1/auth/invitations                 the signed-in account's own unexpired invitations
//   POST /api/v1/auth/invitations/{id}/accept     Kabul Et: the membership is added; the workspace is chosen at sign-in
//   POST /api/v1/auth/invitations/{id}/decline    Reddet: the invitation is removed
// Each takes a fully signed-in session (state Active) and touches only invitations addressed to that account. An
// invitation is a row of iam.tenant_invitations, never a membership, so until it is accepted no membership check (the
// sign-in's workspace list, the session's role, the realtime hub, the user guards) sees it.
public static class WorkspaceInvitationEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceInvitationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth/invitations").WithGates(ApiGate.Open, ApiGate.Open);
        group.MapGet("", ListAsync).Produces<IReadOnlyList<MyInvitationView>>();
        group.MapPost("/{id:guid}/accept", AcceptAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit)
            .Idempotent("accepts the invitation once and removes it; a repeat finds no invitation (404) and changes nothing").Produces<InvitationAcceptedView>();
        group.MapPost("/{id:guid}/decline", DeclineAsync).RequireRateLimiting(AuthEndpoints.StepRateLimit)
            .Idempotent("removes the invitation; a repeat finds it removed").Produces(StatusCodes.Status204NoContent);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, TimeProvider time)
    {
        if (ActiveSession(http) is not { } session) return Refusal(http);
        var now = time.GetUtcNow();
        var rows = await (
            from invitation in db.TenantInvitations.AsNoTracking()
            where invitation.UserId == session.UserId && invitation.ExpiresAt > now
            join tenant in db.Tenants.AsNoTracking() on invitation.TenantId equals tenant.Id
            where tenant.Status == RecordStatus.Active
            select new
            {
                invitation.Id,
                tenant.DisplayName,
                InvitedBy = db.Users.Where(user => user.Id == invitation.InvitedByUserId).Select(user => user.DisplayName).FirstOrDefault(),
                invitation.Role,
                invitation.CreatedAt,
                invitation.ExpiresAt
            }).ToListAsync(http.RequestAborted);
        return Results.Ok(rows.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new MyInvitationView(x.Id, x.DisplayName, x.InvitedBy ?? "Çalışma alanı yöneticisi", UserManagementEndpoints.RoleWire(x.Role), x.CreatedAt, x.ExpiresAt)).ToList());
    }

    private static async Task<IResult> AcceptAsync(Guid id, HttpContext http, AppDbContext db, TimeProvider time)
    {
        if (ActiveSession(http) is not { } session) return Refusal(http);
        var now = time.GetUtcNow();
        var invitation = await db.TenantInvitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == session.UserId, http.RequestAborted);
        if (invitation is null) return NotFound();
        if (invitation.ExpiresAt <= now)
            return ApiProblems.Of(null, "INVITATION_EXPIRED", "Bu davetin süresi doldu; çalışma alanının yöneticisinden yeniden davet isteyin.", 409);
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invitation.TenantId && x.Status == RecordStatus.Active, http.RequestAborted);
        if (tenant is null) return ApiProblems.Of(null, "INVITATION_WORKSPACE_UNAVAILABLE", "Bu çalışma alanı artık kullanılmıyor.", 409);

        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        // Removing the row is the claim: of two accepts at once only one removes it and adds the membership.
        if (await db.TenantInvitations.Where(x => x.Id == invitation.Id && x.UserId == session.UserId && x.ExpiresAt > now).ExecuteDeleteAsync(http.RequestAborted) != 1)
            return NotFound();
        // A membership made meanwhile (an earlier one, active or disabled) is left as it is: accepting never re-enables it.
        if (!await db.TenantMemberships.AnyAsync(x => x.TenantId == invitation.TenantId && x.UserId == session.UserId, http.RequestAborted))
            db.TenantMemberships.Add(new TenantMembership { Id = Guid.CreateVersion7(), TenantId = invitation.TenantId, UserId = session.UserId, Role = invitation.Role, CreatedAt = now, UpdatedAt = now });
        Audit(db, http, invitation, session.UserId, UserManagementEndpoints.InvitationAccepted, now);
        await db.SaveChangesAsync(http.RequestAborted);
        await transaction.CommitAsync(http.RequestAborted);
        return Results.Ok(new InvitationAcceptedView(tenant.Id, tenant.DisplayName));
    }

    private static async Task<IResult> DeclineAsync(Guid id, HttpContext http, AppDbContext db, TimeProvider time)
    {
        if (ActiveSession(http) is not { } session) return Refusal(http);
        var invitation = await db.TenantInvitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == session.UserId, http.RequestAborted);
        if (invitation is null) return Results.NoContent();
        if (await db.TenantInvitations.Where(x => x.Id == invitation.Id && x.UserId == session.UserId).ExecuteDeleteAsync(http.RequestAborted) == 1)
        {
            Audit(db, http, invitation, session.UserId, UserManagementEndpoints.InvitationDeclined, time.GetUtcNow());
            await db.SaveChangesAsync(http.RequestAborted);
        }
        return Results.NoContent();
    }

    // The workspace that invited sees the answer in its own log; the invited account is the actor.
    private static void Audit(AppDbContext db, HttpContext http, TenantInvitation invitation, Guid userId, string action, DateTimeOffset now)
    {
        var detail = $"role={UserManagementEndpoints.RoleWire(invitation.Role)}; invitation={invitation.Id:D}; ip={http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = invitation.TenantId,
            ActorUserId = userId,
            Action = action,
            TargetType = "User",
            TargetId = userId.ToString("D"),
            Reason = detail.Length > 512 ? detail[..512] : detail,
            CorrelationId = http.TraceIdentifier,
            CreatedAt = now
        });
    }

    private static UserSession? ActiveSession(HttpContext http) =>
        http.Items["UserSession"] is UserSession { State: SessionState.Active } session && http.User.FindFirst("tenant_id") is not null ? session : null;

    // No session: signed out (401); a session still at the password or second-factor step: refused (403).
    private static IResult Refusal(HttpContext http) => http.Items["UserSession"] is null
        ? ApiProblems.Of(null, "AUTHENTICATION_REQUIRED", "Oturum bulunamadı.", 401)
        : ApiProblems.Of(null, "SESSION_STATE_FORBIDDEN", "Oturumun şu anki adımı bu işleme izin vermiyor.", 403);

    private static IResult NotFound() => ApiProblems.Of(null, "INVITATION_NOT_FOUND", "Davet bulunamadı; geri çekilmiş ya da yanıtlanmış olabilir.", 404);
}
