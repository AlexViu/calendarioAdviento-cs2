using MySqlConnector;

namespace AdventCalendar;

/// <summary>
/// Persistencia de casillas abiertas, en la misma BD MySQL que cs2-store
/// (reutiliza su cadena de conexión, así no hay credenciales duplicadas).
///
/// delivered: 0 = reclamada pero pendiente de entregar
///            1 = en entrega / entregada
/// El paso 0→1 es un UPDATE atómico: aunque haya varios servidores con el
/// plugin contra la misma BD, cada casilla se entrega una sola vez.
/// </summary>
public sealed class ClaimRepository(string connectionString)
{
    private const string Table = "advent_calendar_claims";

    private async Task<MySqlConnection> OpenAsync()
    {
        var c = new MySqlConnection(connectionString);
        await c.OpenAsync();
        return c;
    }

    public async Task EnsureSchemaAsync()
    {
        await using var c = await OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS `{Table}` (
              `steamid`     BIGINT UNSIGNED NOT NULL,
              `year`        SMALLINT        NOT NULL,
              `day`         TINYINT         NOT NULL,
              `player_name` VARCHAR(128)    NOT NULL DEFAULT '',
              `delivered`   TINYINT         NOT NULL DEFAULT 0,
              `claimed_at`  DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
              `delivered_at` DATETIME       NULL,
              PRIMARY KEY (`steamid`, `year`, `day`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Registra la casilla del día (si no existía ya).</summary>
    public async Task ClaimAsync(ulong steamId, int year, int day, string name)
    {
        await using var c = await OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT IGNORE INTO `{Table}` (steamid, year, day, player_name) VALUES (@s, @y, @d, @n)";
        cmd.Parameters.AddWithValue("@s", steamId);
        cmd.Parameters.AddWithValue("@y", year);
        cmd.Parameters.AddWithValue("@d", day);
        cmd.Parameters.AddWithValue("@n", name.Length > 128 ? name[..128] : name);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Bloquea (0→1) y devuelve los días pendientes de entregar de ese jugador.
    /// </summary>
    public async Task<List<int>> TakePendingAsync(ulong steamId, int year)
    {
        await using var c = await OpenAsync();
        var pending = new List<int>();
        await using (var sel = c.CreateCommand())
        {
            sel.CommandText = $"SELECT day FROM `{Table}` WHERE steamid=@s AND year=@y AND delivered=0 ORDER BY day";
            sel.Parameters.AddWithValue("@s", steamId);
            sel.Parameters.AddWithValue("@y", year);
            await using var r = await sel.ExecuteReaderAsync();
            while (await r.ReadAsync()) pending.Add(r.GetInt32(0));
        }

        var taken = new List<int>();
        foreach (var day in pending)
        {
            await using var upd = c.CreateCommand();
            upd.CommandText = $"UPDATE `{Table}` SET delivered=1, delivered_at=NOW() WHERE steamid=@s AND year=@y AND day=@d AND delivered=0";
            upd.Parameters.AddWithValue("@s", steamId);
            upd.Parameters.AddWithValue("@y", year);
            upd.Parameters.AddWithValue("@d", day);
            if (await upd.ExecuteNonQueryAsync() == 1) taken.Add(day);
        }
        return taken;
    }

    /// <summary>Devuelve una casilla a pendiente (p. ej. el jugador se fue antes de recibirla).</summary>
    public async Task ReleaseAsync(ulong steamId, int year, int day)
    {
        await using var c = await OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE `{Table}` SET delivered=0, delivered_at=NULL WHERE steamid=@s AND year=@y AND day=@d";
        cmd.Parameters.AddWithValue("@s", steamId);
        cmd.Parameters.AddWithValue("@y", year);
        cmd.Parameters.AddWithValue("@d", day);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<int>> GetClaimedDaysAsync(ulong steamId, int year)
    {
        await using var c = await OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT day FROM `{Table}` WHERE steamid=@s AND year=@y";
        cmd.Parameters.AddWithValue("@s", steamId);
        cmd.Parameters.AddWithValue("@y", year);
        var days = new HashSet<int>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) days.Add(r.GetInt32(0));
        return days;
    }

    public async Task<int> ResetAsync(ulong steamId, int year, int? day)
    {
        await using var c = await OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = day is null
            ? $"DELETE FROM `{Table}` WHERE steamid=@s AND year=@y"
            : $"DELETE FROM `{Table}` WHERE steamid=@s AND year=@y AND day=@d";
        cmd.Parameters.AddWithValue("@s", steamId);
        cmd.Parameters.AddWithValue("@y", year);
        if (day is not null) cmd.Parameters.AddWithValue("@d", day.Value);
        return await cmd.ExecuteNonQueryAsync();
    }
}
