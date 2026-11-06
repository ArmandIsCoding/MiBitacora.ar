using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MiBitacora.Data;
using MiBitacora.Services;

namespace Microsoft.AspNetCore.Routing;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var accountGroup = endpoints.MapGroup("/Account").RequireAuthorization();
        accountGroup.MapPost("/Logout", LogoutAsync);
        accountGroup.MapPost("/Manage/DownloadPersonalData", DownloadPersonalDataAsync);
        return accountGroup;
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        SignInManager<ApplicationUser> signInManager,
        IAntiforgery antiforgery)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        await signInManager.SignOutAsync();
        return TypedResults.LocalRedirect("~/");
    }

    private static async Task<IResult> DownloadPersonalDataAsync(
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        IJournalStore store,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest();
        }

        var user = await userManager.GetUserAsync(context.User);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var profile = await store.GetProfileAsync(user.Id, cancellationToken);
        var entries = await store.GetEntriesAsync(user.Id, take: int.MaxValue, cancellationToken: cancellationToken);
        var notes = await store.GetNotesAsync(user.Id, cancellationToken);
        var data = new { user.Email, Profile = profile, Entries = entries, Notes = notes };

        context.Response.Headers.CacheControl = "private, no-store";
        var contents = JsonSerializer.SerializeToUtf8Bytes(data, ExportJsonOptions);
        return TypedResults.File(contents, contentType: "application/json", fileDownloadName: "mi-bitacora.json");
    }
}
