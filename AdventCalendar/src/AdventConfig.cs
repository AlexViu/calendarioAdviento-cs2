using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace AdventCalendar;

public class AdventConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 1;

    /// <summary>Interruptor general.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Año del calendario. 0 = el año actual.</summary>
    public int Year { get; set; } = 0;

    /// <summary>Mes del calendario (12 = diciembre).</summary>
    public int Month { get; set; } = 12;

    /// <summary>
    /// Zona horaria para decidir "qué día es". El contenedor no trae tzdata,
    /// así que si no se encuentra se usa <see cref="UtcOffsetHours"/>.
    /// </summary>
    public string TimeZone { get; set; } = "Europe/Madrid";

    /// <summary>Respaldo si la zona horaria no existe (España en diciembre = +1).</summary>
    public double UtcOffsetHours { get; set; } = 1;

    /// <summary>Segundos de espera tras conectarse antes de entregar (deja cargar la store).</summary>
    public float DelayAfterConnectSeconds { get; set; } = 5f;

    /// <summary>Créditos que se dan si el jugador ya tenía el ítem del día.</summary>
    public int DuplicateItemCredits { get; set; } = 500;

    /// <summary>Anunciar a todo el servidor cuando alguien abre su casilla.</summary>
    public bool AnnounceToAll { get; set; } = true;

    /// <summary>Equipar automáticamente los ítems regalados.</summary>
    public bool AutoEquipItems { get; set; } = false;

    /// <summary>Flag de admin para los comandos de gestión.</summary>
    public string AdminFlag { get; set; } = "@css/root";

    public string ChatPrefix { get; set; } = "{green}[Adviento]{default}";

    // ───── HUD (panel ac_cal dentro del layout de RandomPicksHud) ─────

    /// <summary>Usar el HUD Panorama; si no está disponible se usa el chat.</summary>
    public bool UseHud { get; set; } = true;

    /// <summary>Mismo layout que RandomPicksHud / StoreHud.</summary>
    public string HudLayout { get; set; } = "panorama/layout/custom_game/randompicks_hud.xml";

    /// <summary>Congelar al jugador mientras navega el calendario (como la tienda).</summary>
    public bool FreezePlayer { get; set; } = true;

    /// <summary>Segundos que se muestra el calendario solo al abrir la casilla del día (0 = no mostrar).</summary>
    public float AutoShowSeconds { get; set; } = 6f;

    /// <summary>Enseñar los premios de días futuros (false = "Sorpresa").</summary>
    public bool RevealFutureRewards { get; set; } = false;

    /// <summary>
    /// Comandos de servidor según el progreso del calendario (p. ej. dar logros de CourseAchievements).
    /// Se comprueban en cada conexión; el comando debe ser idempotente (css_logro_give lo es).
    /// Admite {userid}, {steamid} y {slot}.
    /// </summary>
    public List<AchievementHook> AchievementHooks { get; set; } =
    [
        new() { MinClaims = 1, Command = "css_logro_give #{userid} xmas_advent_1" },
        new() { MinClaims = 10, Command = "css_logro_give #{userid} xmas_advent_10" },
        new() { MinClaims = 25, Command = "css_logro_give #{userid} xmas_advent_25" },
        new() { Day = 24, Command = "css_logro_give #{userid} xmas_nochebuena" },
        new() { Day = 25, Command = "css_logro_give #{userid} xmas_navidad" },
    ];

    /// <summary>
    /// Premios por día del mes. Clave = día ("1".."25").
    /// Cada día puede tener varios premios.
    /// </summary>
    public Dictionary<string, List<AdventReward>> Days { get; set; } = DefaultDays();

    private static Dictionary<string, List<AdventReward>> DefaultDays()
    {
        static List<AdventReward> C(int credits) => [new AdventReward { Type = "credits", Credits = credits }];
        static List<AdventReward> I(string id, int expiration = 0) =>
            [new AdventReward { Type = "item", UniqueId = id, ExpirationSeconds = expiration }];

        return new Dictionary<string, List<AdventReward>>
        {
            ["1"] = C(250),
            ["2"] = C(300),
            ["3"] = I("stking"),
            ["4"] = C(400),
            ["5"] = C(500),
            ["6"] = I("ncred"),
            ["7"] = C(500),
            ["8"] = I("trailgreencolor"),
            ["9"] = C(600),
            ["10"] = C(700),
            ["11"] = I("ctqueen"),
            ["12"] = C(800),
            ["13"] = I("vampire_wings"),
            ["14"] = C(900),
            ["15"] = I("naruto"),
            ["16"] = C(1000),
            ["17"] = I("chatcolorred"),
            ["18"] = C(1200),
            ["19"] = I("sapphire_angel_wings_pink"),
            ["20"] = C(1500),
            ["21"] = I("kakashi_hatake"),
            ["22"] = C(2000),
            ["23"] = I("phanton_wings"),
            ["24"] = [new AdventReward { Type = "item", UniqueId = "homer_simpson" }, new AdventReward { Type = "credits", Credits = 2500 }],
            ["25"] = [new AdventReward { Type = "item", UniqueId = "sapphire_angel_wings_v2_red" }, new AdventReward { Type = "credits", Credits = 5000 }],
        };
    }
}

public class AchievementHook
{
    /// <summary>Se dispara si el jugador lleva al menos estas casillas abiertas (0 = no se usa).</summary>
    public int MinClaims { get; set; }

    /// <summary>Se dispara si el jugador tiene abierta la casilla de este día (0 = no se usa).</summary>
    public int Day { get; set; }

    public string Command { get; set; } = "";
}

public class AdventReward
{
    /// <summary>"credits" o "item".</summary>
    public string Type { get; set; } = "credits";

    /// <summary>Para Type=credits.</summary>
    public int Credits { get; set; }

    /// <summary>Para Type=item: el "uniqueid" del ítem en cs2-store.json.</summary>
    public string UniqueId { get; set; } = "";

    /// <summary>Para Type=item: 0 = permanente; si no, segundos de duración.</summary>
    public int ExpirationSeconds { get; set; }

    /// <summary>Texto opcional para mostrar en vez del nombre del ítem.</summary>
    public string? DisplayName { get; set; }
}
