using Microsoft.Data.SqlClient;

namespace StaticSignatureVerification.Storage;

/// <summary>
/// Retries opening a SQL connection on transient failures (brief network blips, SQL failover).
/// Deliberately scoped to connection-open only: retrying a command already in flight isn't safe
/// without per-statement idempotency analysis (a plain INSERT can't be blindly resent), while
/// retrying a connection that hasn't sent anything to the server yet always is.
/// </summary>
internal static class TransientSqlRetry
{
    private const int MaxAttempts = 3;

    // Standard SQL Server / Azure SQL transient error numbers: network-level failures, timeouts,
    // and throttling/failover conditions that are expected to resolve on their own shortly.
    private static readonly HashSet<int> TransientErrorNumbers = new()
    {
        -2, 2, 53, 64, 233, 4060, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613, 49918, 49919, 49920
    };

    public static async Task<SqlConnection> OpenConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch (SqlException ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        throw new InvalidOperationException("Unreachable: the retry loop always returns or throws.");
    }

    private static bool IsTransient(SqlException ex) => TransientErrorNumbers.Contains(ex.Number);
}
