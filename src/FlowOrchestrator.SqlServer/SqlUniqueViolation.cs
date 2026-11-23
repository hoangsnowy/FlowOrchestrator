using Microsoft.Data.SqlClient;

namespace FlowOrchestrator.SqlServer;

/// <summary>
/// Recognises SQL Server's duplicate-key errors so the insert-if-absent gates can report a lost
/// race as <see langword="false"/> instead of throwing.
/// </summary>
/// <remarks>
/// The gates take <c>UPDLOCK, HOLDLOCK</c>, so a concurrent loser normally blocks and then sees the
/// winner's row. This check is the belt-and-braces for anything that still slips through (an
/// isolation level forced on the connection, a later change to the query shape): the loser of a
/// race on a primary key or unique index has, by definition, lost — never an error the caller
/// should see.
/// </remarks>
internal static class SqlUniqueViolation
{
    /// <summary>Violation of a PRIMARY KEY or UNIQUE constraint.</summary>
    private const int UniqueConstraintViolation = 2627;

    /// <summary>Cannot insert a duplicate key row into an object with a unique index.</summary>
    private const int UniqueIndexViolation = 2601;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="exception"/> reports a duplicate key on a
    /// primary key, unique constraint or unique index.
    /// </summary>
    /// <param name="exception">The exception raised by the insert.</param>
    public static bool Matches(SqlException exception)
    {
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number is UniqueConstraintViolation or UniqueIndexViolation)
            {
                return true;
            }
        }

        return false;
    }
}
