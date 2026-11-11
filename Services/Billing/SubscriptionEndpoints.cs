using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MiBitacora.Data;

namespace MiBitacora.Services;

public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/Account/Subscription/{action}", async (string action, HttpContext context,
            IAntiforgery antiforgery, UserManager<ApplicationUser> users, SubscriptionService billing) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
            var user = await users.GetUserAsync(context.User);
            if (user is null) return Results.Unauthorized();
            try
            {
                if (action == "Subscribe")
                {
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    if (form["accept"].ToString() != "yes") return Results.BadRequest();
                    var link = await billing.StartAsync(user.Id, user.Email ?? "", context.RequestAborted);
                    return Results.Redirect(link);
                }
                if (action == "Cancel") await billing.CancelAsync(user.Id, context.RequestAborted);
                else if (action == "Refresh") await billing.RefreshAsync(user.Id, context.RequestAborted);
                else return Results.NotFound();
                return Results.LocalRedirect("/Account/Manage/Subscription?updated=1");
            }
            catch (Exception ex) when (ex is SubscriptionException or HttpRequestException or TaskCanceledException or JsonException)
            { return Results.LocalRedirect("/Account/Manage/Subscription?error=1"); }
        }).RequireAuthorization();

        app.MapPost("/Account/Subscription/Simulate/{id:guid}/{action}", async (Guid id, string action, HttpContext context,
            IAntiforgery antiforgery, SubscriptionService billing) =>
        {
            if (!billing.IsSimulation || context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return Results.NotFound();
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            try { await billing.SimulateAsync(userId, id, action, context.RequestAborted); }
            catch (SubscriptionException) { return Results.LocalRedirect("/Account/Manage/Subscription?error=1"); }
            return Results.LocalRedirect("/Account/Manage/Subscription?updated=1");
        }).RequireAuthorization();

        app.MapPost("/api/mercadopago/webhook", ReceiveWebhookAsync);
    }

    private static async Task<IResult> ReceiveWebhookAsync(HttpContext context, SubscriptionService billing,
        BillingWebhookInbox inbox, TimeProvider clock)
    {
        if (billing.Settings.Provider != "MercadoPago" || !billing.Settings.AllowLiveRequests) return Results.NotFound();
        if (context.Request.ContentLength > 16_384) return Results.StatusCode(413);
        try
        {
            // Bound even chunked request bodies before parsing them.
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + count > 16_384) return Results.StatusCode(413);
                buffer.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            var dataId = root.GetProperty("data").GetProperty("id").ToString();
            if (type is not ("payment" or "subscription_preapproval" or "subscription_authorized_payment") ||
                !BillingWebhookSignature.IsResourceId(dataId)) return Results.BadRequest();
            var queryId = context.Request.Query["data.id"].ToString();
            if (queryId.Length == 0 || !string.Equals(queryId, dataId, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
            var requestId = context.Request.Headers["x-request-id"].ToString();
            if (!BillingWebhookSignature.Verify(context.Request.Headers["x-signature"].ToString(), requestId, queryId,
                billing.Settings.WebhookSecret, clock.GetUtcNow())) return Results.Unauthorized();
            await inbox.EnqueueAsync(type, dataId, requestId, context.RequestAborted);
            return Results.Ok(); // Durable acceptance precedes acknowledgement; the worker does remote I/O.
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { return Results.BadRequest(); }
    }
}
