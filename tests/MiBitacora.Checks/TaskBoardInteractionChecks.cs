using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MiBitacora.Components.Pages;
using MiBitacora.Data;
using MiBitacora.Services;

internal static class TaskBoardInteractionChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mibitacora-task-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { await VerifyAsync(folder, check); } finally { Directory.Delete(folder, true); }
    }
    private static async Task VerifyAsync(string folder, Action<bool, string> check)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite($"Data Source={folder}/test.db;Pooling=False;Foreign Keys=True"));
        services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton(TimeProvider.System); services.AddSingleton(TimeZoneInfo.Utc);
        var account = new Account(); var capture = new Capture();
        services.AddSingleton<AuthenticationStateProvider>(account); services.AddSingleton<NavigationManager, Navigation>();
        services.AddSingleton<IComponentActivator>(capture); services.AddSingleton<IJSRuntime, NoJavaScript>();
        services.AddScoped<TaskBoardService>();
        await using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope();
        var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var user = new ApplicationUser { UserName = "task-ui-synthetic" };
        if (!(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user)).Succeeded) throw new Exception("Synthetic account failed.");
        account.UserId = user.Id;
        var tasks = scope.ServiceProvider.GetRequiredService<TaskBoardService>();
        var board = await tasks.SaveBoardAsync(user.Id, null, "Tablero ficticio", "", "sage", true);
        board = await tasks.SaveCardAsync(user.Id, board.Summary.Id, null, board.Columns[0].Id, "Tarjeta ficticia", "", "paper", null, board.Summary.Revision);
        var cardId = board.Cards.Single().Id;
        capture.BoardId = board.Summary.Id;
        await using var renderer = new PageRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = renderer.BeginRenderingComponent(typeof(Pendientes), ParameterView.Empty);
            await result.QuiescenceTask; return result;
        });
        var page = capture.Page ?? throw new Exception("Page not captured.");
        string Html() => root.ToHtmlString();
        var initial = await renderer.Dispatcher.InvokeAsync(Html);
        check(initial.Contains($"data-revision=\"{board.Summary.Revision}\""), "Tablero interactivo: render inicial con revisión y tarjeta ficticias");
        for (var i = 0; i < 4; i++)
        {
            var destination = board.Columns[(i + 1) % board.Columns.Count].Id;
            // A JS-invokable call is not a Blazor DOM event and must schedule its own render.
            await Task.Run(() => page.MoveTaskCard(cardId.ToString(), destination.ToString(), null, board.Summary.Revision));
            var saved = (await tasks.GetAsync(user.Id, board.Summary.Id))!;
            check(saved.Cards.Single().ColumnId == destination, $"Tablero interactivo: movimiento {i + 1} persistido");
            var html = await renderer.Dispatcher.InvokeAsync(Html);
            check(html.Contains($"data-revision=\"{saved.Summary.Revision}\"") && html.Contains($"data-task-column-id=\"{destination}\""),
                $"Tablero interactivo: movimiento {i + 1} actualiza columna y revisión sin recargar ni otro evento");
            check(html.Contains("data-readonly=\"false\""), "Tablero interactivo: habilita el siguiente movimiento al finalizar");
            board = saved;
        }
        var stale = board.Summary.Revision;
        board = await tasks.SaveColumnAsync(user.Id, board.Summary.Id, null, "Cambio en otra pestaña", false, board.Summary.Revision);
        await Task.Run(() => page.MoveTaskCard(cardId.ToString(), board.Columns[0].Id.ToString(), null, stale));
        var conflict = await renderer.Dispatcher.InvokeAsync(Html);
        check(System.Net.WebUtility.HtmlDecode(conflict).Contains("otra pestaña") && conflict.Contains("role=\"alert\""), "Tablero interactivo: un conflicto de revisión aparece sin necesitar otro clic");

        var saveRoot = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = renderer.BeginRenderingComponent(typeof(Pendientes), ParameterView.Empty);
            await result.QuiescenceTask; return result;
        });
        var savePage = capture.Page!;
        var unavailableDialog = new UnavailableDialog();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            typeof(Pendientes).GetMethod("EditCard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(savePage, [board.Columns[0].Id, null]);
            typeof(Pendientes).GetField("_draftTitle", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(savePage, "Guardar sin opcionales");
            typeof(Pendientes).GetField("_module", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(savePage, unavailableDialog);
            var submit = typeof(Pendientes).GetMethod("SaveEditorAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<Task>>(savePage);
            await ((IHandleEvent)savePage).HandleEventAsync(new EventCallbackWorkItem(submit), null);
        });
        var savedBoard = (await tasks.GetAsync(user.Id, board.Summary.Id))!;
        var saveHtml = await renderer.Dispatcher.InvokeAsync(saveRoot.ToHtmlString);
        check(savedBoard.Cards.Count(c => c.Title == "Guardar sin opcionales" && c.Description == "" && c.DueDate is null) == 1,
            "Tablero interactivo: guardar con sólo título persiste exactamente una tarjeta");
        check(!saveHtml.Contains("<dialog") && saveHtml.Contains("Tarjeta guardada.") && saveHtml.Contains($"data-revision=\"{savedBoard.Summary.Revision}\""),
            "Tablero interactivo: guardar cierra el editor y muestra la nueva tarjeta sin recargar");
        check(unavailableDialog.Calls == 0 && !saveHtml.Contains("No pudimos guardar"),
            "Tablero interactivo: el cierre visual no depende de otra llamada JS tras confirmar la escritura");
    }
    private sealed class PageRenderer(IServiceProvider services, ILoggerFactory logger) : StaticHtmlRenderer(services, logger)
    {
        protected override RendererInfo RendererInfo => new("TaskInteractionTest", true);
        protected override IComponent ResolveComponentForRenderMode(Type type, int? parent, IComponentActivator activator, IComponentRenderMode mode) =>
            mode is InteractiveServerRenderMode ? activator.CreateInstance(type) : throw new NotSupportedException();
    }
    private sealed class Capture : IComponentActivator
    {
        public Guid BoardId { get; set; }
        public Pendientes? Page { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var instance = (IComponent)Activator.CreateInstance(type)!;
            if (instance is Pendientes page) { Page = page; typeof(Pendientes).GetProperty(nameof(Pendientes.BoardQuery))!.SetValue(page, BoardId.ToString()); }
            return instance;
        }
    }
    private sealed class Account : AuthenticationStateProvider
    {
        public string UserId { get; set; } = "";
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Synthetic"))));
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/pendientes");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => throw new NotSupportedException("Static interaction renderer does not call the browser.");
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => InvokeAsync<T>(name, args);
    }
    private sealed class UnavailableDialog : IJSObjectReference
    {
        public int Calls { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) { Calls++; throw new JSException("Synthetic unavailable dialog reference"); }
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => InvokeAsync<T>(name, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
