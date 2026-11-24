// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace EquiLedger.Storage;

public record EventRow(
    long Seq, Guid EventId, string Type, string ActorId, DateTimeOffset OccurredAt,
    string? ResolutionNo, JsonNode Evidence, JsonNode Payload, JsonNode RuleRefs,
    string PrevHash, string Hash, string Signature);

public class Db
{
    public string AppCs { get; }
    public string OwnerCs { get; }
    private readonly string _initSqlPath;

    public Db(string appCs, string ownerCs, string initSqlPath)
    { AppCs = appCs; OwnerCs = ownerCs; _initSqlPath = initSqlPath; }

    public NpgsqlConnection App() { var c = new NpgsqlConnection(AppCs); c.Open(); return c; }
    public NpgsqlConnection Owner() { var c = new NpgsqlConnection(OwnerCs); c.Open(); return c; }

    public async Task InitAsync()
    {
        var sql = await File.ReadAllTextAsync(_initSqlPath);
        await using var c = Owner();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}

public static class Pg
{
    public static NpgsqlCommand Cmd(this NpgsqlConnection c, string sql, NpgsqlTransaction? tx = null, params (string, object?)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var (k, v) in ps)
            cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    public static NpgsqlParameter Json(string name, JsonNode? node) =>
        new(name, NpgsqlDbType.Jsonb) { Value = node?.ToJsonString() ?? "null" };

    public static async Task ExecTx(this NpgsqlConnection c, string sql, NpgsqlTransaction? tx, params (string, object?)[] ps)
    {
        await using var cmd = c.Cmd(sql, tx, ps);
        await cmd.ExecuteNonQueryAsync();
    }
    public static Task Exec(this NpgsqlConnection c, string sql, params (string, object?)[] ps) => c.ExecTx(sql, null, ps);

    public static async Task<object?> ScalarTx(this NpgsqlConnection c, string sql, NpgsqlTransaction? tx, params (string, object?)[] ps)
    {
        await using var cmd = c.Cmd(sql, tx, ps);
        return await cmd.ExecuteScalarAsync();
    }
    public static Task<object?> Scalar(this NpgsqlConnection c, string sql, params (string, object?)[] ps) => c.ScalarTx(sql, null, ps);

    public static async Task<List<Dictionary<string, object?>>> RowsTx(this NpgsqlConnection c, string sql, NpgsqlTransaction? tx, params (string, object?)[] ps)
    {
        await using var cmd = c.Cmd(sql, tx, ps);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Dictionary<string, object?>>();
        while (await r.ReadAsync())
        {
            var d = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++)
                d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            list.Add(d);
        }
        return list;
    }
    public static Task<List<Dictionary<string, object?>>> Rows(this NpgsqlConnection c, string sql, params (string, object?)[] ps) => c.RowsTx(sql, null, ps);

    public static JsonNode J(object? o) => o is string s ? (JsonNode.Parse(s) ?? new JsonObject()) : new JsonObject();
    public static decimal D(object? o) => o is null or DBNull ? 0m : Convert.ToDecimal(o);
    public static string S(object? o) => o?.ToString() ?? "";
}