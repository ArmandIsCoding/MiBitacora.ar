using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;
using System.Security.Claims;

namespace MiBitacora.Services;

public static class BillingAdministration
{
    public static void MapBillingAdministration(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/suscripciones/solicitud/{id:guid}/{action}", async (Guid id, string action, HttpContext context,
            IAntiforgery csrf, IDbContextFactory<ApplicationDbContext> factory, TransactionalMail mail, TimeProvider clock) => {
            if (!await csrf.IsRequestValidAsync(context)) return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var resolution = form["resolution"].ToString().Trim();
            await using var db = await factory.CreateDbContextAsync(context.RequestAborted);
            var row = await db.CustomerRequests.SingleOrDefaultAsync(row => row.Id == id, context.RequestAborted);
            if (row is null) return Results.NotFound();
            if (row.Status == "completed") return Results.LocalRedirect("/admin/suscripciones");
            if (action == "retry" && row.VerifiedUtc is not null && row.Kind != "support") {
                row.Status = "processing"; row.LastAttemptUtc = null;
            }
            else if (action == "refund" && row.VerifiedUtc is not null && row.Kind is "withdraw" or "refund" && resolution.Length is >= 5 and <= 2000) {
                row.RefundApprovedByAdmin = true; row.Status = "processing"; row.LastAttemptUtc = null; row.Resolution = resolution;
            }
            else if (action == "resolve" && resolution.Length is >= 5 and <= 2000) {
                if (mail.IsReady)
                    await mail.QueueAsync("request-manual-resolution-" + row.Id, row.Email, "Respuesta a tu solicitud " + row.Code,
                        $"Código {row.Code}\n\n{resolution}\n\n{BusinessIdentity.Name}\n{BusinessIdentity.SupportEmail}", context.RequestAborted);
                row.Resolution = resolution; row.ResolvedUtc = clock.GetUtcNow().UtcDateTime; row.Status = "completed";
                row.VerificationHash = null; row.VerificationExpiresUtc = null;
            }
            else return Results.BadRequest();
            db.BillingAudit.Add(new() { Id = Guid.NewGuid(), UserId = row.UserId ?? "unregistered", Type = "admin.request." + action,
                Reference = row.Code + ":by:" + context.User.FindFirstValue(ClaimTypes.NameIdentifier) + ":" + Guid.NewGuid().ToString("N"), SubscriptionId = row.SubscriptionId, CreatedUtc = clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.LocalRedirect("/admin/suscripciones");
        }).RequireAuthorization(AdminAccess.Policy);

        app.MapPost("/admin/suscripciones/factura/{id:guid}", async (Guid id, HttpContext context, IAntiforgery csrf,
            IDbContextFactory<ApplicationDbContext> factory, TimeProvider clock) => {
            if (!await csrf.IsRequestValidAsync(context)) return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var reference = form["reference"].ToString().Trim();
            if (reference.Length is < 5 or > 100) return Results.BadRequest();
            await using var db = await factory.CreateDbContextAsync(context.RequestAborted);
            var payment = await db.BillingPayments.SingleOrDefaultAsync(row => row.Id == id, context.RequestAborted);
            if (payment is null) return Results.NotFound();
            if (payment.FiscalInvoiceReference is null) {
                payment.FiscalInvoiceReference = reference; payment.FiscalInvoiceIssuedUtc = clock.GetUtcNow().UtcDateTime;
                var owner = await db.BillingSubscriptions.Where(row => row.Id == payment.SubscriptionId).Select(row => row.UserId).SingleAsync(context.RequestAborted);
                db.BillingAudit.Add(new() { Id = Guid.NewGuid(), UserId = owner, SubscriptionId = payment.SubscriptionId, Type = "invoice.issued",
                    Reference = payment.ProviderPaymentId + ":" + reference + ":by:" + context.User.FindFirstValue(ClaimTypes.NameIdentifier), AmountCents = payment.AmountCents, CreatedUtc = clock.GetUtcNow().UtcDateTime });
                await db.SaveChangesAsync(context.RequestAborted);
            }
            return Results.LocalRedirect("/admin/suscripciones");
        }).RequireAuthorization(AdminAccess.Policy);
    }
}
