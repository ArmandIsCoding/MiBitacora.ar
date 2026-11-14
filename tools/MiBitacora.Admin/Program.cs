using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiBitacora.Data;

if (args.Length < 3 || args[0] is not ("grant-premium" or "revoke-premium" or "reset-ai-providers") || args[1] != "--database")
    throw new ArgumentException("grant-premium|revoke-premium --database <existing SQLite path> --only-account|--user-id <id>; reset-ai-providers --database <path>");
var database = Path.GetFullPath(args[2]);
if (!File.Exists(database)) throw new FileNotFoundException("La base debe existir. Respaldala antes de modificar el acceso.");
var onlyAccount = args.Length == 4 && args[3] == "--only-account";
var requestedId = args.Length == 5 && args[3] == "--user-id" ? args[4] : null;
var resetProviders = args[0] == "reset-ai-providers" && args.Length == 3;
if (args[0] == "reset-ai-providers" && !resetProviders) throw new ArgumentException("reset-ai-providers sólo recibe --database <path>.");
if (!resetProviders && !onlyAccount && requestedId is null) throw new ArgumentException("Indicá --only-account o un --user-id explícito.");
var services = new ServiceCollection().AddLogging();
var connection = new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true, Pooling = false, Mode = SqliteOpenMode.ReadWrite, DefaultTimeout = 30 };
services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite(connection.ToString()));
services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();
await using var provider = services.BuildServiceProvider();
var factory = provider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
await using var db = await factory.CreateDbContextAsync();
// Do not migrate an unknown or empty database by accident.
var ids = await db.Users.AsNoTracking().Select(u => u.Id).ToListAsync();
if (resetProviders)
{
    if (ids.Count == 0) throw new InvalidOperationException("La base indicada no tiene cuentas existentes.");
    await db.Database.MigrateAsync();
    var changed = await db.AccountAiPreferences.Where(p => p.Provider != "OpenAI")
        .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Provider, "OpenAI"));
    Console.WriteLine($"OpenAI seleccionado por defecto para las {ids.Count} cuentas existentes. Preferencias actualizadas: {changed}.");
    return;
}
if (onlyAccount && ids.Count != 1) throw new InvalidOperationException("--only-account requiere exactamente una cuenta. Indicá el ID explícito.");
var id = onlyAccount ? ids.Single() : requestedId!;
if (!ids.Contains(id)) throw new InvalidOperationException("La cuenta indicada no existe.");
await db.Database.MigrateAsync();
await using var transaction = await db.Database.BeginTransactionAsync();
var grant = await db.BillingAccessGrants.SingleOrDefaultAsync(g => g.UserId == id);
if (args[0] == "grant-premium")
{
    if (grant is null)
    {
        grant = new() { UserId = id, GrantedUtc = DateTime.UtcNow, Reason = "Acceso premium de cortesía para pruebas, autorizado por el titular." };
        db.BillingAccessGrants.Add(grant);
    }
    grant.ExpiresUtc = null;
    var preference = await db.AccountAiPreferences.SingleOrDefaultAsync(p => p.UserId == id);
    if (preference is null) db.AccountAiPreferences.Add(new() { UserId = id, Provider = "OpenAI" });
    else preference.Provider = "OpenAI";
}
else if (grant is not null) db.BillingAccessGrants.Remove(grant);
await db.SaveChangesAsync();
await transaction.CommitAsync();
Console.WriteLine(args[0] == "grant-premium"
    ? "Acceso premium de cortesía habilitado para una cuenta. Proveedor: OpenAI. No se crearon pagos ni suscripciones."
    : "Concesión de cortesía revocada para una cuenta. Los períodos realmente abonados se conservan.");
