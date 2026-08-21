using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Platform.Persistence.Postgres;

/// <summary>
/// adr/0017: the one port method that saves on the caller's behalf, because "already processed" is
/// only knowable from the outcome of an actual write - <see cref="InboxRecordConfiguration"/>'s
/// composite primary key is the source of truth, not an in-memory check.
/// </summary>
public sealed class EfInboxChecker<TContext>(TContext dbContext, IClock clock) : IInboxChecker
    where TContext : DbContext
{
    public async Task<bool> TryRecordAndSaveAsync(Guid messageId, string consumer, CancellationToken cancellationToken)
    {
        dbContext.Set<InboxRecord>().Add(new InboxRecord(messageId, consumer, clock.UtcNow));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
