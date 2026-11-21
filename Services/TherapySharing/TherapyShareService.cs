using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed partial class TherapyShareService(IDbContextFactory<ApplicationDbContext> factory,
    TherapyShareBuilder builder, IDataProtectionProvider protection, TimeProvider clock)
{
    public const int MaxActiveLinks = 5;
    [GeneratedRegex("\\A[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant)] private static partial Regex TokenPattern();
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private IDataProtector Protector(TherapyShareRow row, string purpose) => protection.CreateProtector("MiBitacora.TherapyShare.v1", row.UserId, row.Id.ToString("N"), purpose);

    public async Task<PreparedTherapyShare> PrepareAsync(string userId, string title, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        return await builder.BuildAsync(db, userId, title, Now, ct);
    }
    public async Task<TherapyShareInfo> PublishAsync(string userId, PreparedTherapyShare prepared, bool consent,
        int expirationDays = 30, Guid? replaceId = null, int? expectedRevision = null, CancellationToken ct = default)
    {
        if (!consent) throw new JournalInputException("Confirmá que revisaste el contenido y que querés compartirlo mediante un enlace público.");
        if (prepared.UserId != userId) throw Unavailable();
        if (expirationDays > 365 || expirationDays < 0 && !(expirationDays == -1 && replaceId is not null)) throw new JournalInputException("Elegí un vencimiento de hasta 365 días o sin vencimiento.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await builder.BuildAsync(db, userId, prepared.Snapshot.Title, Now, ct);
        if (current.Fingerprint != prepared.Fingerprint) throw new JournalInputException("Tu cuaderno cambió después de la vista previa. Prepará otra vista previa antes de compartir.");
        var now = Now;
        // Retain minimal link metadata, not expired copies of a diary.
        await db.TherapyShares.Where(s => s.UserId == userId && s.ExpiresUtc != null && s.ExpiresUtc <= now)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ProtectedSnapshot, "").SetProperty(s => s.ProtectedToken, ""), ct);
        TherapyShareRow row;
        string token;
        if (replaceId is { } id)
        {
            row = await db.TherapyShares.SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct) ?? throw Unavailable();
            if (row.Revoked || row.ExpiresUtc <= now || row.Revision != expectedRevision) throw Unavailable();
            token = Protector(row, "link").Unprotect(row.ProtectedToken);
            row.Revision++;
        }
        else
        {
            if (await db.TherapyShares.CountAsync(s => s.UserId == userId && !s.Revoked && (s.ExpiresUtc == null || s.ExpiresUtc > now), ct) >= MaxActiveLinks)
                throw new JournalInputException("Ya tenés cinco enlaces activos. Revocá uno antes de crear otro.");
            token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            row = new() { Id = Guid.NewGuid(), UserId = userId, TokenHash = Hash(token), CreatedUtc = now };
            row.ProtectedToken = Protector(row, "link").Protect(token);
            db.TherapyShares.Add(row);
        }
        row.Title = Protector(row, "title").Protect(current.Snapshot.Title);
        row.ProtectedSnapshot = Protector(row, "snapshot").Protect(JsonSerializer.Serialize(current.Snapshot, TherapyShareBuilder.Json));
        row.UpdatedUtc = now;
        if (expirationDays >= 0) row.ExpiresUtc = expirationDays == 0 ? null : now.AddDays(expirationDays);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Unavailable(); }
        return Info(row, token);
    }
    public async Task<IReadOnlyList<TherapyShareInfo>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        var rows = await db.TherapyShares.AsNoTracking().Where(s => s.UserId == userId && db.Users.Any(u => u.Id == s.UserId))
            .OrderBy(s => s.Revoked || s.ExpiresUtc != null && s.ExpiresUtc <= now).ThenByDescending(s => s.CreatedUtc).Take(100).ToListAsync(ct);
        return rows.Select(r => Info(r, !r.Revoked && (r.ExpiresUtc is null || r.ExpiresUtc > Now) ? ReadToken(r) : null)).ToArray();
    }
    public async Task RevokeAsync(string userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var affected = await db.TherapyShares.Where(s => s.Id == id && s.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Revoked, true).SetProperty(s => s.ProtectedSnapshot, "")
                .SetProperty(s => s.ProtectedToken, "").SetProperty(s => s.Revision, s => s.Revision + 1), ct);
        if (affected == 0) throw Unavailable();
    }
    public async Task<OpenTherapyShare?> OpenAsync(string? token, CancellationToken ct = default)
    {
        if (token is null || !TokenPattern().IsMatch(token)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var hash = Hash(token); var now = Now;
        var row = await db.TherapyShares.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == hash && !s.Revoked && (s.ExpiresUtc == null || s.ExpiresUtc > now) && db.Users.Any(u => u.Id == s.UserId), ct);
        if (row is null || row.ProtectedSnapshot.Length == 0) return null;
        try {
            var snapshot = JsonSerializer.Deserialize<SharedTherapySnapshot>(Protector(row, "snapshot").Unprotect(row.ProtectedSnapshot), TherapyShareBuilder.Json);
            return snapshot is { Version: 1 } ? new(snapshot, row.ExpiresUtc, row.Revision) : null;
        } catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }
    public async Task<int?> GetRevisionAsync(string token, CancellationToken ct = default)
    {
        if (!TokenPattern().IsMatch(token)) return null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var hash = Hash(token); var now = Now;
        return await db.TherapyShares.AsNoTracking().Where(s => s.TokenHash == hash && !s.Revoked && (s.ExpiresUtc == null || s.ExpiresUtc > now) && db.Users.Any(u => u.Id == s.UserId))
            .Select(s => (int?)s.Revision).SingleOrDefaultAsync(ct);
    }
    private string? ReadToken(TherapyShareRow row) { try { return Protector(row, "link").Unprotect(row.ProtectedToken); } catch (CryptographicException) { return null; } }
    private TherapyShareInfo Info(TherapyShareRow row, string? token)
    {
        string title;
        try { title = Protector(row, "title").Unprotect(row.Title); } catch (CryptographicException) { title = "Enlace compartido"; }
        return new(row.Id, title, row.CreatedUtc, row.UpdatedUtc, row.ExpiresUtc, row.Revoked, row.Revision, token);
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static JournalInputException Unavailable() => new("Ese enlace ya no está disponible para esta operación. Actualizá la lista de enlaces.");
}
