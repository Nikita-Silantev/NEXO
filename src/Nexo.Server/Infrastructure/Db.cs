using System.Data.Common;
using Dapper;
using Npgsql;

namespace Nexo.Server.Infrastructure;

/// <summary>Вспомогательные методы доступа к БД (Npgsql + Dapper). Все перечисления БД читаются и пишутся как text с явным приведением в SQL.</summary>
public static class Db
{
    public static void ConfigureDapper() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public static async Task<NpgsqlConnection> OpenAsync(this NpgsqlDataSource ds, CancellationToken ct)
        => await ds.OpenConnectionAsync(ct);

    public static Task<List<T>> ListAsync<T>(this DbConnection c, string sql, object? p = null, DbTransaction? tx = null, CancellationToken ct = default)
        => c.QueryAsync<T>(new CommandDefinition(sql, p, tx, cancellationToken: ct)).ContinueWith(t => t.Result.ToList(), ct);

    public static Task<T?> OneAsync<T>(this DbConnection c, string sql, object? p = null, DbTransaction? tx = null, CancellationToken ct = default)
        => c.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql, p, tx, cancellationToken: ct));

    public static Task<int> ExecAsync(this DbConnection c, string sql, object? p = null, DbTransaction? tx = null, CancellationToken ct = default)
        => c.ExecuteAsync(new CommandDefinition(sql, p, tx, cancellationToken: ct));

    public static Task<T?> ScalarAsync<T>(this DbConnection c, string sql, object? p = null, DbTransaction? tx = null, CancellationToken ct = default)
        => c.ExecuteScalarAsync<T>(new CommandDefinition(sql, p, tx, cancellationToken: ct));

    /// <summary>Нарушение уникальности (SQLSTATE 23505).</summary>
    public static bool IsUniqueViolation(Exception e) => e is PostgresException { SqlState: "23505" };

    public static DateTime UtcNow => DateTime.UtcNow;
}
