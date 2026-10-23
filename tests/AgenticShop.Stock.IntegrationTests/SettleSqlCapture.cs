using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AgenticShop.Stock.IntegrationTests;

/// <summary>
/// Records the SQL the host actually issues while a capture window is open.
/// </summary>
/// <remarks>
/// <para>
/// This exists to guard a property that is invisible in a response and unreachable by assertion on
/// state: <c>SettleAsync</c> must read the reservation and its stock item in <b>one</b> statement.
/// Two statements give each its own READ COMMITTED snapshot, and a winner committing between them
/// leaves the loser with a stale <c>Pending</c> reservation against a fresh <c>Reserved = 0</c>
/// counter — the state machine passes, the counter guard throws <see cref="InvalidOperationException"/>,
/// and an ordinary conflict reaches the client as a 500. That is not hypothetical: measured on a live
/// container it fired 7 times in 10,000 parallel confirms and 6 times in 10,000 parallel releases,
/// and it shipped past a green suite for months because no test could hit a window that narrow.
/// </para>
/// <para>
/// A parallel burst cannot pin it — at roughly 7 in 10,000 a ten-request test sees it about once in
/// a hundred runs. Counting the statements does, deterministically, on every run.
/// </para>
/// </remarks>
public sealed class SettleSqlCapture : DbCommandInterceptor
{
    private readonly object _gate = new();

    private List<string>? _statements;

    /// <summary>Every command issued since <see cref="Begin"/>, in order.</summary>
    public IReadOnlyList<string> Statements
    {
        get
        {
            lock (_gate)
            {
                return _statements is null ? [] : [.. _statements];
            }
        }
    }

    public void Begin()
    {
        lock (_gate)
        {
            _statements = [];
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _statements = null;
        }
    }

    private void Record(DbCommand command)
    {
        lock (_gate)
        {
            _statements?.Add(command.CommandText);
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);

        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Record(command);

        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Record(command);

        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);

        return ValueTask.FromResult(result);
    }
}
