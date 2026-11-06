using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MiBitacora.Data;

namespace MiBitacora.Services;

public sealed record AdminDashboardSnapshot(
    int Days,
    int RegisteredAccounts,
    int ActiveAccounts,
    int Entries,
    int Notes,
    int Attachments,
    IReadOnlyList<AdminActivityDay> Activity,
    IReadOnlyList<AdminAccountUsage> Accounts,
    int MatchingAccounts,
    int Page,
    int PageCount);

public sealed record AdminActivityDay(DateOnly Date, int ActiveAccounts);

public sealed record AdminAccountUsage(
    string AccountId,
    DateOnly? RegisteredDate,
    DateOnly? LastActiveDate,
    int ActiveDays,
    int Entries,
    int Notes,
    int Attachments);

public sealed class AdminDashboardService(
    IDbContextFactory<ApplicationDbContext> factory,
    IAuthorizationService authorization,
    TimeZoneInfo timeZone)
{
    private const int PageSize = 20;

    public async Task<AdminDashboardSnapshot> GetAsync(ClaimsPrincipal principal, int days, string? search, bool activeOnly,
        int page, CancellationToken cancellationToken = default)
    {
        var access = await authorization.AuthorizeAsync(principal, AdminAccess.Policy);
        var adminId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!access.Succeeded || string.IsNullOrEmpty(adminId))
        {
            throw new UnauthorizedAccessException();
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // Read all counts and the account page from the same SQLite snapshot.
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var transactionScope = await db.Database.UseTransactionAsync(transaction, cancellationToken);
        if (!await db.Users.AnyAsync(user => user.Id == adminId, cancellationToken))
        {
            throw new UnauthorizedAccessException();
        }

        days = days is 7 or 30 or 90 ? days : 30;
        var today = JournalDates.ToLocalDate(DateTime.UtcNow, timeZone);
        var firstDay = today.AddDays(1 - days);
        var fromUtc = JournalDates.StartOfDayUtc(firstDay, timeZone);
        var untilUtc = JournalDates.StartOfDayUtc(today.AddDays(1), timeZone);

        var entries = db.JournalEntries.Where(row => row.CreatedUtc >= fromUtc && row.CreatedUtc < untilUtc);
        var notes = db.JournalNotes.Where(row => row.CreatedUtc >= fromUtc && row.CreatedUtc < untilUtc);
        var attachments = db.JournalAttachments
            .Where(row => row.Entry.CreatedUtc >= fromUtc && row.Entry.CreatedUtc < untilUtc);

        var events = entries.Select(row => new { row.UserId, row.CreatedUtc })
            .Concat(notes.Select(row => new { row.UserId, row.CreatedUtc }));
        var usersByDay = Enumerable.Range(0, days).Select(firstDay.AddDays)
            .ToDictionary(day => day, _ => new HashSet<string>(StringComparer.Ordinal));
        await foreach (var activity in events.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (usersByDay.TryGetValue(JournalDates.ToLocalDate(activity.CreatedUtc, timeZone), out var users))
            {
                users.Add(activity.UserId);
            }
        }

        var activeIds = entries.Select(row => row.UserId).Union(notes.Select(row => row.UserId));
        var matchingAccountsQuery = db.Users.Select(user => new { user.Id, user.RegisteredUtc });
        if (activeOnly)
        {
            matchingAccountsQuery = matchingAccountsQuery.Where(user => activeIds.Contains(user.Id));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            matchingAccountsQuery = matchingAccountsQuery.Where(user => user.Id.ToLower().Contains(term));
        }

        var matchingAccounts = await matchingAccountsQuery.CountAsync(cancellationToken);
        var pageCount = Math.Max(1, (int)Math.Ceiling(matchingAccounts / (double)PageSize));
        page = Math.Clamp(page, 1, pageCount);

        var lastActivity = db.JournalEntries.Select(row => new { row.UserId, row.CreatedUtc })
            .Concat(db.JournalNotes.Select(row => new { row.UserId, row.CreatedUtc }))
            .GroupBy(row => row.UserId)
            .Select(group => new { UserId = group.Key, LastActiveUtc = (DateTime?)group.Max(row => row.CreatedUtc) });
        var accountPage = await (
            from account in matchingAccountsQuery
            join recent in lastActivity on account.Id equals recent.UserId into activity
            from recent in activity.DefaultIfEmpty()
            orderby recent.LastActiveUtc descending, account.Id
            select new { account.Id, account.RegisteredUtc, recent.LastActiveUtc })
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(cancellationToken);

        var accounts = new List<AdminAccountUsage>(accountPage.Count);
        if (accountPage.Count > 0)
        {
            var pageIds = accountPage.Select(account => account.Id).ToArray();
            var entryCounts = await CountByAccountAsync(
                entries.Where(row => pageIds.Contains(row.UserId)).Select(row => row.UserId), cancellationToken);
            var noteCounts = await CountByAccountAsync(
                notes.Where(row => pageIds.Contains(row.UserId)).Select(row => row.UserId), cancellationToken);
            var attachmentCounts = await CountByAccountAsync(
                attachments.Where(row => pageIds.Contains(row.Entry.UserId)).Select(row => row.Entry.UserId), cancellationToken);

            foreach (var account in accountPage)
            {
                accounts.Add(new AdminAccountUsage(
                    account.Id,
                    account.RegisteredUtc is DateTime registeredUtc ? JournalDates.ToLocalDate(registeredUtc, timeZone) : null,
                    account.LastActiveUtc is DateTime lastActiveUtc ? JournalDates.ToLocalDate(lastActiveUtc, timeZone) : null,
                    usersByDay.Values.Count(users => users.Contains(account.Id)),
                    entryCounts.GetValueOrDefault(account.Id),
                    noteCounts.GetValueOrDefault(account.Id),
                    attachmentCounts.GetValueOrDefault(account.Id)));
            }
        }

        var activityDays = usersByDay.OrderBy(pair => pair.Key)
            .Select(pair => new AdminActivityDay(pair.Key, pair.Value.Count)).ToArray();
        return new AdminDashboardSnapshot(
            days,
            await db.Users.CountAsync(cancellationToken),
            usersByDay.Values.SelectMany(users => users).Distinct(StringComparer.Ordinal).Count(),
            await entries.CountAsync(cancellationToken),
            await notes.CountAsync(cancellationToken),
            await attachments.CountAsync(cancellationToken),
            activityDays,
            accounts,
            matchingAccounts,
            page,
            pageCount);
    }

    private static Task<Dictionary<string, int>> CountByAccountAsync(IQueryable<string> accountIds,
        CancellationToken cancellationToken) =>
        accountIds.GroupBy(id => id).Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Id, row => row.Count, cancellationToken);
}
