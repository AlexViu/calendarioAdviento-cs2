using System.Reflection;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using StoreApi;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using TimerFlags = CounterStrikeSharp.API.Modules.Timers.TimerFlags;

namespace AdventCalendar;

public partial class AdventCalendarPlugin : BasePlugin, IPluginConfig<AdventConfig>
{
    public override string ModuleName => "AdventCalendar";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "RandomPicks";
    public override string ModuleDescription => "Calendario de adviento: una recompensa de la store por cada día que te conectas.";

    private static readonly PluginCapability<IStoreApi?> StoreCapability = new("cs2-store:api");

    public AdventConfig Config { get; set; } = new();

    private IStoreApi? _store;
    private ClaimRepository? _repo;
    private bool _schemaReady;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    /// <summary>Jugadores con una entrega en curso (evita dobles).</summary>
    private readonly HashSet<ulong> _inFlight = [];
    /// <summary>Último "día" (yyyymmdd) comprobado por jugador, para detectar el cambio de día con gente conectada.</summary>
    private readonly Dictionary<ulong, int> _lastChecked = [];
    /// <summary>Día simulado por un admin para pruebas (null = fecha real).</summary>
    private int? _simulatedDay;

    private TimeZoneInfo? _tz;

    public void OnConfigParsed(AdventConfig config)
    {
        Config = config;
        _tz = null;
        try { _tz = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone); }
        catch { /* sin tzdata en el contenedor: se usa UtcOffsetHours */ }
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        LoadHud();
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is { IsBot: false }) _lastChecked.Remove(p.SteamID);
        });

        // Cambio de día con gente ya conectada (p. ej. a medianoche).
        AddTimer(60f, () =>
        {
            var key = TodayKey();
            foreach (var p in Humans())
                if (!_lastChecked.TryGetValue(p.SteamID, out var k) || k != key)
                    TryProcess(p, 0);
        }, TimerFlags.REPEAT);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _store = StoreCapability.Get();
        if (_store is null)
        {
            Logger.LogError("[AdventCalendar] No se encontró cs2-store (capability cs2-store:api). El plugin no hará nada.");
            return;
        }
        ValidateConfig(msg => Logger.LogInformation("{Msg}", msg));
        if (hotReload)
            foreach (var p in Humans()) TryProcess(p, 0);
    }

    /// <summary>Comprueba que todos los ítems del calendario existen en cs2-store.json.</summary>
    private void ValidateConfig(Action<string> report)
    {
        if (_store is null) { report("[AdventCalendar] cs2-store no disponible."); return; }
        var bad = 0;
        foreach (var day in AllDays())
            foreach (var r in Config.Days[day.ToString()])
            {
                if (r.Type.Equals("credits", StringComparison.OrdinalIgnoreCase)) continue;
                var item = _store.GetItem(r.UniqueId);
                if (item is null) { bad++; report($"[AdventCalendar] Día {day}: el ítem '{r.UniqueId}' NO existe en la store."); }
                else report($"[AdventCalendar] Día {day}: {item.GetValueOrDefault("name", r.UniqueId)} ({item["type"]}) OK");
            }
        report($"[AdventCalendar] Validación: {AllDays().Count} días, {bad} ítem(s) inválido(s).");
    }

    [ConsoleCommand("css_adviento_check", "Admin: valida que los ítems del calendario existen en la store")]
    public void CmdCheck(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!IsAdmin(player)) { cmd.ReplyToCommand("Sin permisos."); return; }
        ValidateConfig(cmd.ReplyToCommand);
    }

    // ───────────────────────── Fecha ─────────────────────────

    private DateTime Now()
    {
        var utc = DateTime.UtcNow;
        return _tz is not null ? TimeZoneInfo.ConvertTimeFromUtc(utc, _tz) : utc.AddHours(Config.UtcOffsetHours);
    }

    private int CalendarYear => Config.Year > 0 ? Config.Year : Now().Year;

    /// <summary>Día del calendario de hoy, o null si hoy no hay casilla.</summary>
    private int? Today()
    {
        if (_simulatedDay is { } sim) return sim;
        var now = Now();
        if (now.Year != CalendarYear || now.Month != Config.Month) return null;
        return Config.Days.ContainsKey(now.Day.ToString()) ? now.Day : null;
    }

    /// <summary>
    /// Día de referencia para pintar pasado/futuro: el día del mes si estamos en el
    /// mes del calendario, 0 si aún no ha empezado y 99 si ya terminó.
    /// </summary>
    private int RefDay()
    {
        if (_simulatedDay is { } sim) return sim;
        var now = Now();
        var year = CalendarYear;
        if (now.Year == year && now.Month == Config.Month) return now.Day;
        return now.Year > year || (now.Year == year && now.Month > Config.Month) ? 99 : 0;
    }

    public override void Unload(bool hotReload) => UnloadHud();

    private int TodayKey() => _simulatedDay is { } s ? -s : Now().Year * 10000 + Now().Month * 100 + Now().Day;

    private List<int> AllDays() => Config.Days.Keys
        .Select(k => int.TryParse(k, out var d) ? d : -1).Where(d => d > 0).OrderBy(d => d).ToList();

    // ───────────────────────── Entrega ─────────────────────────

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull ev, GameEventInfo info)
    {
        var p = ev.Userid;
        if (p is { IsValid: true, IsBot: false, IsHLTV: false })
        {
            var steamId = p.SteamID;
            AddTimer(Config.DelayAfterConnectSeconds, () =>
            {
                var player = Utilities.GetPlayerFromSteamId(steamId);
                if (player is { IsValid: true }) TryProcess(player, 0);
            });
        }
        return HookResult.Continue;
    }

    private async Task<ClaimRepository?> GetRepoAsync()
    {
        if (_schemaReady) return _repo;
        var cs = _store?.GetDatabaseString();
        if (string.IsNullOrEmpty(cs)) return null; // la store aún no ha conectado
        await _schemaLock.WaitAsync();
        try
        {
            if (!_schemaReady)
            {
                _repo = new ClaimRepository(cs);
                await _repo.EnsureSchemaAsync();
                _schemaReady = true;
            }
        }
        finally { _schemaLock.Release(); }
        return _repo;
    }

    private void TryProcess(CCSPlayerController player, int attempt)
    {
        if (!Config.Enabled || _store is null || !player.IsValid || player.IsBot) return;
        var steamId = player.SteamID;
        if (steamId == 0) return;

        _lastChecked[steamId] = TodayKey();
        var today = Today();
        if (today is null) return;

        // La store carga al jugador de forma asíncrona: hasta entonces devuelve -1.
        if (_store.GetPlayerCredits(player) < 0)
        {
            if (attempt < 20)
                AddTimer(3f, () =>
                {
                    var again = Utilities.GetPlayerFromSteamId(steamId);
                    if (again is { IsValid: true }) TryProcess(again, attempt + 1);
                });
            return;
        }

        if (!_inFlight.Add(steamId)) return;
        var name = player.PlayerName;
        var year = CalendarYear;
        var day = today.Value;

        Task.Run(async () =>
        {
            List<int> taken = [];
            HashSet<int>? claimed = null;
            try
            {
                var repo = await GetRepoAsync();
                if (repo is null) return;
                await repo.ClaimAsync(steamId, year, day, name);
                taken = await repo.TakePendingAsync(steamId, year);
                if (Config.AchievementHooks.Count > 0) claimed = await repo.GetClaimedDaysAsync(steamId, year);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[AdventCalendar] Error de BD procesando {SteamId}", steamId);
            }
            finally
            {
                Server.NextFrame(() =>
                {
                    _inFlight.Remove(steamId);
                    foreach (var d in taken) DeliverOrRelease(steamId, year, d);
                    if (claimed is not null) RunHooks(steamId, claimed);
                });
            }
        });
    }

    private void DeliverOrRelease(ulong steamId, int year, int day)
    {
        var player = Utilities.GetPlayerFromSteamId(steamId);
        if (player is not { IsValid: true } || _store is null || _store.GetPlayerCredits(player) < 0)
        {
            // Se fue antes de recibirlo: queda pendiente para la próxima vez que entre.
            var repo = _repo;
            if (repo is not null) Task.Run(() => repo.ReleaseAsync(steamId, year, day));
            return;
        }

        if (!Config.Days.TryGetValue(day.ToString(), out var rewards) || rewards.Count == 0) return;

        var got = new List<string>();
        foreach (var r in rewards)
        {
            try { got.Add(GiveReward(player, r)); }
            catch (Exception ex) { Logger.LogError(ex, "[AdventCalendar] Error entregando día {Day} a {SteamId}", day, steamId); }
        }

        var text = string.Join("{default}, ", got.Where(s => s.Length > 0));
        Logger.LogInformation("[AdventCalendar] {Name} ({SteamId}) abrió el día {Day}: {Text}", player.PlayerName, steamId, day, StripTags(text));

        Reply(player, $"🎄 ¡Has abierto la casilla {{gold}}{day}{{default}} del calendario de adviento!");
        Reply(player, $"Recompensa: {text}");
        Reply(player, "Vuelve mañana para abrir la siguiente. Escribe {lime}!calendario{default} para verlo.");

        if (Config.AutoShowSeconds > 0)
            ShowHud(player, interactive: false, select: day, msg: $"¡Casilla {day} abierta!  {StripTags(text)}");

        if (Config.AnnounceToAll)
            foreach (var other in Humans().Where(o => o.SteamID != steamId))
                Reply(other, $"{{lightblue}}{player.PlayerName}{{default}} abrió la casilla {{gold}}{day}{{default}}: {text}");
    }

    /// <summary>Lanza los comandos de AchievementHooks que el jugador ya cumple (deben ser idempotentes).</summary>
    private void RunHooks(ulong steamId, HashSet<int> claimed)
    {
        var p = Utilities.GetPlayerFromSteamId(steamId);
        if (p is not { IsValid: true } || p.UserId is null) return;
        foreach (var h in Config.AchievementHooks)
        {
            if (string.IsNullOrWhiteSpace(h.Command)) continue;
            var met = (h.MinClaims > 0 && claimed.Count >= h.MinClaims) || (h.Day > 0 && claimed.Contains(h.Day));
            if (!met) continue;
            Server.ExecuteCommand(h.Command
                .Replace("{userid}", p.UserId.Value.ToString())
                .Replace("{steamid}", steamId.ToString())
                .Replace("{slot}", p.Slot.ToString()));
        }
    }

    /// <summary>Entrega un premio y devuelve su descripción para el chat.</summary>
    private string GiveReward(CCSPlayerController player, AdventReward r)
    {
        var store = _store!;
        if (r.Type.Equals("credits", StringComparison.OrdinalIgnoreCase))
        {
            if (r.Credits <= 0) return "";
            store.GivePlayerCredits(player, r.Credits);
            return $"{{green}}{r.Credits} créditos";
        }

        var item = store.GetItem(r.UniqueId);
        if (item is null)
        {
            Logger.LogWarning("[AdventCalendar] El ítem '{Id}' no existe en cs2-store.json; se dan créditos de compensación.", r.UniqueId);
            return GiveCompensation(player);
        }

        var display = r.DisplayName ?? (item.TryGetValue("name", out var n) ? n : r.UniqueId);
        var type = item["type"];

        if (store.Item_PlayerHas(player, type, r.UniqueId, true))
            return $"{{purple}}{display}{{default}} (ya lo tenías → {GiveCompensation(player)}{{default}})";

        var copy = new Dictionary<string, string>(item);
        if (r.ExpirationSeconds > 0) copy["expiration"] = r.ExpirationSeconds.ToString();

        if (!store.Item_Give(player, copy))
        {
            Logger.LogWarning("[AdventCalendar] Item_Give falló para '{Id}'; se dan créditos de compensación.", r.UniqueId);
            return GiveCompensation(player);
        }

        if (Config.AutoEquipItems) store.Item_Equip(player, copy);

        var suffix = r.ExpirationSeconds > 0 ? $" ({FormatDuration(r.ExpirationSeconds)})" : "";
        return $"{{purple}}{display}{{default}} [{TypeLabel(type)}]{suffix}";
    }

    private string GiveCompensation(CCSPlayerController player)
    {
        if (Config.DuplicateItemCredits <= 0) return "";
        _store!.GivePlayerCredits(player, Config.DuplicateItemCredits);
        return $"{{green}}{Config.DuplicateItemCredits} créditos";
    }

    private static string TypeLabel(string type) => type switch
    {
        "playerskin" => "skin",
        "wings" => "alas",
        "trail" or "grenadetrail" => "estela",
        "scoretag" => "título tabla",
        "chattag" => "título chat",
        "namecolor" => "color de nombre",
        "chatcolor" => "color de chat",
        _ => type,
    };

    private static string FormatDuration(int s) =>
        s >= 86400 ? $"{s / 86400} días" : s >= 3600 ? $"{s / 3600} h" : $"{s / 60} min";

    // ───────────────────────── Comandos ─────────────────────────

    [ConsoleCommand("css_calendario", "Muestra tu calendario de adviento")]
    [ConsoleCommand("css_adviento", "Muestra tu calendario de adviento")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdCalendar(CCSPlayerController? player, CommandInfo cmd)
    {
        if (player is not { IsValid: true }) return;

        // HUD: !calendario abre / cierra. "!calendario chat" fuerza la versión de chat.
        if (!cmd.ArgString.Trim().Equals("chat", StringComparison.OrdinalIgnoreCase))
        {
            if (_views.TryGetValue(player.Slot, out var open) && open.Interactive) { CloseHud(player); return; }
            if (ShowHud(player, interactive: true)) return;
        }

        var steamId = player.SteamID;
        var year = CalendarYear;
        var today = Today();
        var refDay = RefDay();

        Task.Run(async () =>
        {
            HashSet<int> claimed = [];
            try
            {
                var repo = await GetRepoAsync();
                if (repo is not null) claimed = await repo.GetClaimedDaysAsync(steamId, year);
            }
            catch (Exception ex) { Logger.LogError(ex, "[AdventCalendar] Error leyendo calendario"); }

            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSteamId(steamId);
                if (p is not { IsValid: true }) return;

                var days = AllDays();
                Reply(p, $"🎄 Calendario de adviento {year} — {{green}}{claimed.Count}{{default}}/{days.Count} casillas abiertas");
                foreach (var row in days.Chunk(7))
                {
                    var sb = new StringBuilder(" ");
                    foreach (var d in row)
                    {
                        var color = claimed.Contains(d) ? "{green}" : d == refDay ? "{gold}" : d < refDay ? "{grey}" : "{default}";
                        var mark = claimed.Contains(d) ? "✔" : d < refDay ? "✘" : d == refDay ? "★" : "·";
                        sb.Append($"{color}{d:00}{mark} ");
                    }
                    Reply(p, sb.ToString(), prefix: false);
                }
                Reply(p, "{green}✔ abierta  {gold}★ hoy  {grey}✘ perdida  {default}· próximamente", prefix: false);

                if (today is null)
                    Reply(p, refDay == 0 ? "El calendario aún no ha empezado. ¡Empieza el 1 de diciembre!" : "El calendario ha terminado. ¡Gracias por jugar!");
                else if (!claimed.Contains(today.Value))
                    Reply(p, "La casilla de hoy se abrirá en unos segundos…");
            });
        });
    }

    [ConsoleCommand("css_adviento_dia", "Admin: simula el día del calendario (pruebas). Uso: css_adviento_dia <dia|off>")]
    [CommandHelper(minArgs: 1, usage: "<dia|off>")]
    public void CmdSimDay(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!IsAdmin(player)) { cmd.ReplyToCommand("Sin permisos."); return; }
        var arg = cmd.GetArg(1);
        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase)) _simulatedDay = null;
        else if (int.TryParse(arg, out var d) && Config.Days.ContainsKey(d.ToString())) _simulatedDay = d;
        else { cmd.ReplyToCommand($"Día no válido. Días configurados: {string.Join(",", AllDays())}"); return; }

        _lastChecked.Clear();
        cmd.ReplyToCommand(_simulatedDay is null ? "[Adviento] Usando la fecha real." : $"[Adviento] Simulando el día {_simulatedDay}. Se procesa a los conectados.");
        foreach (var p in Humans()) TryProcess(p, 0);
    }

    [ConsoleCommand("css_adviento_reset", "Admin: borra las casillas de un jugador. Uso: css_adviento_reset <steamid64> [dia]")]
    [CommandHelper(minArgs: 1, usage: "<steamid64> [dia]")]
    public void CmdReset(CCSPlayerController? player, CommandInfo cmd)
    {
        if (!IsAdmin(player)) { cmd.ReplyToCommand("Sin permisos."); return; }
        if (!ulong.TryParse(cmd.GetArg(1), out var steamId)) { cmd.ReplyToCommand("SteamID64 no válido."); return; }
        int? day = cmd.ArgCount > 2 && int.TryParse(cmd.GetArg(2), out var d) ? d : null;
        var year = CalendarYear;
        Task.Run(async () =>
        {
            var repo = await GetRepoAsync();
            var n = repo is null ? -1 : await repo.ResetAsync(steamId, year, day);
            Server.NextFrame(() =>
            {
                _lastChecked.Remove(steamId);
                Logger.LogInformation("[AdventCalendar] Reset {SteamId} día {Day}: {N} filas", steamId, day?.ToString() ?? "todos", n);
                if (player is { IsValid: true }) Reply(player, $"Reset de {steamId}: {n} casilla(s) borradas.");
            });
        });
        cmd.ReplyToCommand("[Adviento] Reset en curso…");
    }

    private bool IsAdmin(CCSPlayerController? p) => p is null || AdminManager.PlayerHasPermissions(p, Config.AdminFlag);

    // ───────────────────────── Utilidades ─────────────────────────

    private static IEnumerable<CCSPlayerController> Humans() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false } && p.SteamID != 0);

    private void Reply(CCSPlayerController p, string msg, bool prefix = true) =>
        p.PrintToChat(Colorize((prefix ? Config.ChatPrefix + " " : " ") + msg));

    private static readonly Dictionary<string, string> ColorTags = typeof(ChatColors)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(char))
        .GroupBy(f => "{" + f.Name.ToLowerInvariant() + "}")
        .ToDictionary(g => g.Key, g => ((char)g.First().GetValue(null)!).ToString());

    private static string Colorize(string s)
    {
        foreach (var (tag, code) in ColorTags)
            s = s.Replace(tag, code, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    private static string StripTags(string s)
    {
        foreach (var tag in ColorTags.Keys) s = s.Replace(tag, "", StringComparison.OrdinalIgnoreCase);
        return s;
    }
}
