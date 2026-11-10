using System.Security.Claims;

namespace MiBitacora.Services;

public static class AttachmentEndpoints
{
    public static void MapJournalAttachments(this IEndpointRouteBuilder app)
    {
        var attachments = app.MapGroup("/adjuntos").RequireAuthorization();
        attachments.MapGet("/{id:guid}", DownloadAsync);
        attachments.MapMethods("/{id:guid}/vista", ["GET", "HEAD"], PreviewAsync);
    }

    public static async Task<IResult> DownloadAsync(Guid id, ClaimsPrincipal user, IJournalStore store,
        HttpContext http, CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();
        var file = await store.GetAttachmentAsync(userId, id, cancellationToken);
        if (file is null) return Results.NotFound();
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.File(file.Content, "application/octet-stream", file.FileName);
    }

    public static async Task<IResult> PreviewAsync(Guid id, ClaimsPrincipal user, IJournalStore store,
        HttpContext http, CancellationToken cancellationToken)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();
        var file = await store.GetAttachmentPreviewAsync(userId, id, cancellationToken);
        if (file is null || !AttachmentPresentation.IsInlineType(file.ContentType))
        {
            if (file is not null) await file.Content.DisposeAsync();
            return Results.NotFound();
        }
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; frame-ancestors 'self'";
        http.Response.Headers.ContentDisposition = "inline";
        return Results.File(file.Content, file.ContentType, enableRangeProcessing: true);
    }
}
