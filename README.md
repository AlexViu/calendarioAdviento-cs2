# AdventCalendar (CS2 · CounterStrikeSharp + cs2-store)

Cada día de diciembre que un jugador se conecta se abre su casilla y recibe el premio de ese día
(créditos, skins, alas, títulos/tags… cualquier ítem de `cs2-store.json`). Los días que no se conecta se pierden.

## Servidores
Instalado en **minigames** (`/root/cs2-config`, :27015) y **surf** (`/root/cs2-config-surf`, :27016).
Los dos usan la misma BD de cs2-store (`cs2_store_db`), así que comparten casillas y créditos: **una casilla al día por
jugador en total**, entre en el servidor que entre. Cada servidor tiene su propio `cs2-store.json`: los ítems `xmas_*`
están añadidos en los dos.

## Build / despliegue
    dotnet build -c Release AdventCalendar
    # copiar bin/Release/net10.0/{AdventCalendar.dll,.deps.json,.pdb,MySqlConnector.dll}
    #   -> host:/root/cs2-config/addons/counterstrikesharp/plugins/AdventCalendar/
    # (la recarga en caliente lo coge al cambiar la dll; no hace falta reiniciar)

## Config
`configs/plugins/AdventCalendar/AdventCalendar.json`. En `Days`, para cada día una lista de premios:
- `{"Type":"credits","Credits":500}`
- `{"Type":"item","UniqueId":"vampire_wings","ExpirationSeconds":0}`  (0 = permanente)
Si el jugador ya tiene el ítem, recibe `DuplicateItemCredits`. Si el uniqueid no existe, también.
Se aplica con `css_plugins reload AdventCalendar`.

Ítems exclusivos del calendario: añádelos a `cs2-store.json` con `"hide": "true"` para que no se puedan comprar.

## Comandos
- `!calendario` / `!adviento` — muestra el calendario del jugador.
- `css_adviento_dia <dia|off>` (admin) — simula un día para pruebas (OJO: entrega premios reales a los conectados).
- `css_adviento_reset <steamid64> [dia]` (admin) — borra casillas de un jugador.

## Logros (CourseAchievements)
`AchievementHooks` lanza comandos de servidor según el progreso: `MinClaims` (casillas abiertas) o `Day` (casilla de ese día).
Por defecto da los logros `xmas_advent_1/10/25`, `xmas_nochebuena` y `xmas_navidad` con `css_logro_give #{userid} <id>`
(ver `sql/navidad_2026.sql` en logros-cs2). Se reevalúan en cada conexión: `css_logro_give` ignora los que ya tiene.

## Datos
Tabla `advent_calendar_claims` en `cs2_store_db` (usa la conexión de cs2-store). Cada casilla se
entrega una sola vez incluso con varios servidores; si el jugador se va antes de recibirla, queda pendiente.

## Premios Navidad 2026 (config/AdventCalendar.json)
Ítems exclusivos añadidos a `cs2-store.json` (ocultos en la tienda, `"hide":"true"`, precio 0):
- Skins: `xmas_santa_t` / `xmas_santa_ct` (día 25), `xmas_grinch` (día 8), `xmas_phoenix` (día 18)
- Alas: `xmas_wingedfury_wings` (día 14)
- Tags de chat: `xmas_ct_elfo` (3), `xmas_ct_nieve` (11), `xmas_ct_espiritu` (20), `xmas_ct_papanoel` (25)
- Tags de marcador: `xmas_st_reno` (6), `xmas_st_grinch` (16), `xmas_st_navidad` (23)
- El resto de días, créditos (300 → 3000; el 25 además 5000).
`css_adviento_check` valida que todos los ítems existen en la store.

## HUD
Panel `ac_cal` dentro de `randompicks_hud.xml/.css` (repo `logros-cs2/addon-randompicks`), mismo estilo que la tienda.
Se dibuja con `custom_hud_layout` igual que StoreHud. `!calendario` lo abre (WASD mover, R cerrar);
al abrir la casilla del día se muestra solo `AutoShowSeconds`. `!calendario chat` = versión de chat.
**Activar `"UseHud": true` solo cuando el addon publicado ya tenga el panel** (si no, congela sin mostrar nada).

## Dependencias de terceros y licencias

`lib/StoreApi.dll` es el contrato de la API de [cs2-store](https://github.com/schwarper/cs2-store) de schwarper (API v33),
licencia [GPL-3.0](https://github.com/schwarper/cs2-store/blob/main/LICENSE). Solo sirve para compilar: **no se copia al
servidor** con el plugin (en runtime la aporta cs2-store desde `addons/counterstrikesharp/shared/StoreApi`).
Todos los derechos de esa DLL pertenecen a su autor; el código fuente está en su repositorio.

Las dependencias de NuGet (CounterStrikeSharp.API, MySqlConnector) no se incluyen en el repo: se descargan al compilar.
El panel del HUD (`ac_cal`) vive en el layout de [RandomPicksHud](https://github.com/torment93/RandomPicksHud) (MIT),
en el repo [logros-cs2](https://github.com/AlexViu/logros-cs2) (`addon-randompicks/panorama/`).
