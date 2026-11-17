using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using TimerFlags = CounterStrikeSharp.API.Modules.Timers.TimerFlags;

namespace AdventCalendar;

/// <summary>
/// Calendario dibujado en el HUD Panorama de RandomPicksHud (panel "ac_cal" de
/// randompicks_hud.xml), con el mismo mecanismo que StoreHud: variables de diálogo
/// y clases por jugador sobre la entidad custom_hud_layout.
/// </summary>
public partial class AdventCalendarPlugin
{
    private const int GridDays = 25; // casillas que hay en el layout (ac_d_1..ac_d_25)
    private static readonly string[] DayStates = ["claimed", "today", "missed", "future"];

    private sealed class CalView
    {
        public int Sel;
        public HashSet<int> Claimed = [];
        public bool Interactive;
        public float CloseAt;
        public PlayerButtons Last;
        public MoveType_t? PrevMove;
        public bool Frozen;
        public string Msg = "";
    }

    private CCSCustomHudLayout? _hud;
    private nint _hudPtr;
    private readonly Dictionary<(int Slot, string Key), string> _sent = [];
    private readonly Dictionary<int, CalView> _views = [];

    private void LoadHud()
    {
        RegisterListener<Listeners.OnTick>(HudTick);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _hud = null; _hudPtr = 0; _sent.Clear(); _views.Clear();
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            _views.Remove(slot);
            ForgetSent(slot);
        });
        // Si la entidad del HUD se recrea (cambio de ronda/mapa), se reenvía todo.
        AddTimer(0.5f, () =>
        {
            if (_views.Count == 0) return;
            var h = FindHud();
            if (h is null || h.Handle != _hudPtr)
            {
                _hud = h; _hudPtr = h?.Handle ?? 0; _sent.Clear();
                foreach (var (slot, v) in _views)
                    if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p) Render(p, v);
            }
        }, TimerFlags.REPEAT);
    }

    private void UnloadHud()
    {
        foreach (var slot in _views.Keys.ToList())
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } p) CloseHud(p);
    }

    // ───────────────────────── Entidad HUD ─────────────────────────

    private CCSCustomHudLayout? FindHud() =>
        Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout")
            .FirstOrDefault(e => e.IsValid && e.StrLayout == Config.HudLayout);

    private bool EnsureHud()
    {
        if (_hud is { IsValid: true } && _hud.Handle == _hudPtr) return true;
        _hud = FindHud();
        _hudPtr = _hud?.Handle ?? 0;
        _sent.Clear();
        return _hud is not null;
    }

    private void ForgetSent(int slot)
    {
        foreach (var k in _sent.Keys.Where(k => k.Slot == slot).ToList()) _sent.Remove(k);
    }

    private void HudText(CCSPlayerController p, string id, string value)
    {
        if (_hud is not { IsValid: true }) return;
        var key = (p.Slot, id);
        if (_sent.TryGetValue(key, out var old) && old == value) return;
        _sent[key] = value;
        _hud.SetDialogVariableStringForPlayer(p, id, id, value);
    }

    private void HudClass(CCSPlayerController p, string id, string cls, bool on)
    {
        if (_hud is not { IsValid: true }) return;
        var key = (p.Slot, id + "." + cls);
        var v = on ? "1" : "0";
        if (_sent.TryGetValue(key, out var old) && old == v) return;
        _sent[key] = v;
        _hud.SetHasClassForPlayer(p, id, cls, on);
    }

    // ───────────────────────── Abrir / cerrar ─────────────────────────

    /// <summary>
    /// Carga las casillas del jugador y abre el calendario en el HUD.
    /// Devuelve false (sin hacer nada) si el HUD no está disponible.
    /// </summary>
    private bool ShowHud(CCSPlayerController player, bool interactive, int? select = null, string msg = "")
    {
        if (!Config.UseHud || !EnsureHud()) return false;
        var steamId = player.SteamID;
        var year = CalendarYear;

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
                if (p is not { IsValid: true } || !EnsureHud()) return;

                // Un calendario interactivo abierto no se pisa con el automático.
                if (_views.TryGetValue(p.Slot, out var existing) && existing.Interactive && !interactive)
                {
                    existing.Claimed = claimed;
                    existing.Msg = msg;
                    Render(p, existing);
                    return;
                }
                if (existing is not null) CloseHud(p);

                var refDay = RefDay();
                var v = new CalView
                {
                    Claimed = claimed,
                    Interactive = interactive,
                    Sel = Math.Clamp(select ?? (refDay is >= 1 and <= GridDays ? refDay : 1), 1, GridDays),
                    CloseAt = interactive ? 0 : Server.CurrentTime + Config.AutoShowSeconds,
                    Last = p.Buttons,
                    Msg = msg,
                };
                _views[p.Slot] = v;
                ForgetSent(p.Slot);
                if (interactive) Freeze(p, v, true);
                Render(p, v);
            });
        });
        return true;
    }

    private void CloseHud(CCSPlayerController p)
    {
        if (!_views.Remove(p.Slot, out var v)) return;
        Freeze(p, v, false);
        HudClass(p, "ac_cal", "hidden", true);
    }

    /// <summary>Mismo congelado que StoreHud (MoveType 1 ↔ el que tuviera).</summary>
    private void Freeze(CCSPlayerController p, CalView v, bool on)
    {
        if (!Config.FreezePlayer) return;
        var pawn = p.PlayerPawn.Value;
        if (pawn is not { IsValid: true }) return;
        if (on)
        {
            if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
            v.PrevMove = pawn.MoveType;
            v.Frozen = true;
        }
        else if (!v.Frozen) return;

        var mt = on ? (MoveType_t)1
                    : v.PrevMove is { } prev && (int)prev != 1 ? prev : MoveType_t.MOVETYPE_WALK;
        pawn.MoveType = mt;
        pawn.ActualMoveType = mt;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    // ───────────────────────── Entrada ─────────────────────────

    private void HudTick()
    {
        if (_views.Count == 0) return;
        foreach (var (slot, v) in _views.ToList())
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is not { IsValid: true }) { _views.Remove(slot); continue; }

            var now = p.Buttons;
            var pressed = now & ~v.Last;
            v.Last = now;

            if ((pressed & PlayerButtons.Reload) != 0 || (!v.Interactive && Server.CurrentTime >= v.CloseAt))
            {
                CloseHud(p);
                continue;
            }
            if (!v.Interactive) continue;

            var sel = v.Sel;
            if ((pressed & PlayerButtons.Forward) != 0) sel -= 7;
            if ((pressed & PlayerButtons.Back) != 0) sel += 7;
            if ((pressed & PlayerButtons.Moveleft) != 0) sel -= 1;
            if ((pressed & PlayerButtons.Moveright) != 0) sel += 1;
            sel = Math.Clamp(sel, 1, GridDays);
            if (sel != v.Sel)
            {
                v.Sel = sel;
                v.Msg = "";
                Render(p, v);
            }
        }
    }

    // ───────────────────────── Dibujo ─────────────────────────

    private enum DayState { Claimed, Today, Missed, Future }

    private DayState StateOf(int day, HashSet<int> claimed, int refDay) =>
        claimed.Contains(day) ? DayState.Claimed
        : day == refDay && Config.Days.ContainsKey(day.ToString()) ? DayState.Today
        : day < refDay ? DayState.Missed
        : DayState.Future;

    private void Render(CCSPlayerController p, CalView v)
    {
        if (!EnsureHud()) return;
        var refDay = RefDay();
        var total = AllDays().Count;

        HudClass(p, "ac_cal", "hidden", false);
        HudText(p, "ac_title", "CALENDARIO DE ADVIENTO");
        HudText(p, "ac_player", p.PlayerName);
        HudText(p, "ac_progress", $"{v.Claimed.Count} / {total}");
        HudText(p, "ac_close", "R  cerrar");

        for (var d = 1; d <= GridDays; d++)
        {
            var id = $"ac_d_{d}";
            var st = StateOf(d, v.Claimed, refDay);
            var stName = DayStates[(int)st];
            foreach (var s in DayStates) HudClass(p, id, s, s == stName);
            HudClass(p, id, "sel", v.Interactive && d == v.Sel);
            HudText(p, id + "_r", Visible(st) ? ShortReward(d) : "Sorpresa");
        }

        var sst = StateOf(v.Sel, v.Claimed, refDay);
        HudText(p, "ac_detail_day", $"DÍA {v.Sel}");
        HudText(p, "ac_detail", Visible(sst) ? FullReward(v.Sel) : "Premio sorpresa");
        HudText(p, "ac_detail_sub", sst switch
        {
            DayState.Claimed => "Abierta: ya es tuya",
            DayState.Today => "Hoy: se abre sola al conectarte",
            DayState.Missed => "Te la perdiste: no te conectaste ese día",
            _ => $"Disponible el {v.Sel} de diciembre",
        });

        HudClass(p, "ac_msg", "hidden", v.Msg.Length == 0);
        if (v.Msg.Length > 0)
        {
            HudText(p, "ac_msg", v.Msg);
            HudClass(p, "ac_msg", "ok", true);
        }
        HudText(p, "ac_keys", v.Interactive ? "W A S D  mover   ·   R  cerrar" : "R  cerrar");
    }

    private bool Visible(DayState st) => st != DayState.Future || Config.RevealFutureRewards;

    private static readonly Regex Bracket = new(@"\s*\[[^\]]*\]\s*$", RegexOptions.Compiled);

    private string RewardName(AdventReward r)
    {
        if (r.Type.Equals("credits", StringComparison.OrdinalIgnoreCase)) return $"{r.Credits} cr.";
        if (r.DisplayName is { Length: > 0 } dn) return dn;
        var item = _store?.GetItem(r.UniqueId);
        var name = item is not null && item.TryGetValue("name", out var n) ? n : r.UniqueId;
        return Bracket.Replace(name, "");
    }

    private string ShortReward(int day)
    {
        if (!Config.Days.TryGetValue(day.ToString(), out var rs) || rs.Count == 0) return "—";
        var first = RewardName(rs[0]);
        return rs.Count > 1 ? $"{first} +{rs.Count - 1}" : first;
    }

    private string FullReward(int day)
    {
        if (!Config.Days.TryGetValue(day.ToString(), out var rs) || rs.Count == 0) return "Sin premio";
        return string.Join("  +  ", rs.Select(r =>
        {
            if (r.Type.Equals("credits", StringComparison.OrdinalIgnoreCase)) return $"{r.Credits} créditos";
            var type = _store?.GetItem(r.UniqueId)?.GetValueOrDefault("type") ?? "";
            return $"{RewardName(r)} ({TypeLabel(type)})";
        }));
    }
}
