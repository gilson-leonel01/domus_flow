using System.Text;
using Npgsql;

namespace DomusFlow.Api.Infrastructure;

public sealed class Database(IConfiguration configuration, IWebHostEnvironment environment)
{
    private readonly string _connectionString = BuildConnectionString(
        configuration["DATABASE_URL"]
        ?? configuration["Database:Url"]
        ?? "postgres://domusflow:domusflow@localhost:5432/domusflow?sslmode=disable");

    private readonly string _contentRoot = environment.ContentRootPath;

    public NpgsqlConnection OpenConnection() => new(_connectionString);

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var create = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                name TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """, connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var directory = Path.Combine(_contentRoot, "migrations");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Migration directory not found: {directory}");
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.sql").OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            await using var exists = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM schema_migrations WHERE name = @name)", connection);
            exists.Parameters.AddWithValue("name", name);

            if (await exists.ExecuteScalarAsync(cancellationToken) is true)
            {
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var sql = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken);
                await using var migration = new NpgsqlCommand(sql, connection, transaction);
                await migration.ExecuteNonQueryAsync(cancellationToken);

                await using var register = new NpgsqlCommand(
                    "INSERT INTO schema_migrations(name) VALUES (@name)", connection, transaction);
                register.Parameters.AddWithValue("name", name);
                await register.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }

    private static string BuildConnectionString(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
        {
            return value;
        }

        var credentials = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(credentials.ElementAtOrDefault(0) ?? string.Empty),
            Password = Uri.UnescapeDataString(credentials.ElementAtOrDefault(1) ?? string.Empty),
            Pooling = true,
            IncludeErrorDetail = false
        };

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
        query.TryGetValue("sslmode", out var sslModeValue);
        var sslMode = sslModeValue?.ToLowerInvariant();
        builder.SslMode = sslMode switch
        {
            "require" => SslMode.Require,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            _ => SslMode.Disable
        };

        return builder.ConnectionString;
    }
}
