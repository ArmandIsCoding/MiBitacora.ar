using Microsoft.AspNetCore.Components;

namespace MiBitacora.Components.Account;

internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "Identity.StatusMessage";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(15)
    };

    public void RedirectToLocal(string? requestedPath, string defaultPath)
    {
        var baseUri = new Uri(navigationManager.BaseUri);
        var destination = !string.IsNullOrWhiteSpace(requestedPath)
            && Uri.TryCreate(baseUri, requestedPath, out var target)
            && baseUri.IsBaseOf(target)
            ? target.AbsoluteUri
            : defaultPath;
        navigationManager.NavigateTo(destination);
    }

    public void RedirectToWithStatus(string path, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        navigationManager.NavigateTo(path);
    }

    public void RedirectToInvalidUser(HttpContext context)
    {
        RedirectToWithStatus("Account/Login", "Tu sesión venció. Volvé a ingresar.", context);
    }
}
