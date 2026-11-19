# AdventCalendar

Calendario de adviento para servidores de CS2 con [cs2-store](https://github.com/schwarper/cs2-store).
Plugin de CounterStrikeSharp.

## Cómo funciona

- Del 1 al 25 de diciembre, cada día que un jugador entra al servidor se abre su casilla de ese día y recibe el premio.
- Los premios son créditos o cualquier ítem de la store: skins, alas, tags, etc.
- Si un día no entra, esa casilla se pierde.
- Si ya tenía el ítem del día, recibe créditos a cambio.
- Con `!calendario` el jugador ve sus casillas en un panel del HUD. Si el HUD no está disponible, las ve en el chat.
- Las casillas se guardan en la base de datos de la store. Si varios servidores comparten esa base de datos, comparten también el calendario: una casilla al día por jugador, entre en el servidor que entre.

## Requisitos

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [cs2-store](https://github.com/schwarper/cs2-store) con base de datos MySQL
- Opcional, para el panel del HUD: [RandomPicksHud](https://github.com/torment93/RandomPicksHud) con el panel `ac_cal` del addon de [logros-cs2](https://github.com/AlexViu/logros-cs2).

## Instalación

1. Descarga `AdventCalendar.zip` de la [última release](https://github.com/AlexViu/calendarioAdviento-cs2/releases/latest).
2. Descomprímelo en `game/csgo/` del servidor. Se crean dos carpetas:
   - `addons/counterstrikesharp/plugins/AdventCalendar/` (el plugin)
   - `addons/counterstrikesharp/configs/plugins/AdventCalendar/AdventCalendar.json` (la configuración)
3. Añade a tu `cs2-store.json` los ítems que regala el calendario. Los de Navidad 2026 están en [`config/cs2-store-items-navidad.json`](config/cs2-store-items-navidad.json). Van ocultos en la tienda (`"hide": "true"`) para que no se puedan comprar.
4. Reinicia el servidor, o ejecuta `css_plugins load AdventCalendar`.
5. Comprueba que todos los premios existen en la store con `css_adviento_check`.

Si actualizas desde una versión anterior, copia solo la carpeta `plugins/` para no sobrescribir tu configuración.

## Configuración

Los premios se configuran en `AdventCalendar.json`, en `Days`. Cada día lleva una lista de premios:

```json
"Days": {
  "1":  [ { "Type": "credits", "Credits": 300 } ],
  "3":  [ { "Type": "item", "UniqueId": "xmas_ct_elfo" } ],
  "25": [ { "Type": "item", "UniqueId": "xmas_santa_t" }, { "Type": "credits", "Credits": 5000 } ]
}
```

Otras opciones útiles:

| Opción | Qué hace |
|---|---|
| `UtcOffsetHours` | Diferencia con UTC para decidir qué día es (España en invierno = `1`). |
| `DuplicateItemCredits` | Créditos que se dan si el jugador ya tenía el ítem. |
| `UseHud` | `true` para usar el panel del HUD, `false` para usar solo el chat. |
| `AchievementHooks` | Comandos que se ejecutan según las casillas abiertas (por ejemplo, para dar logros). |

Para aplicar los cambios, ejecuta `css_plugins reload AdventCalendar`.

## Comandos

| Comando | Quién | Qué hace |
|---|---|---|
| `!calendario` / `!adviento` | Todos | Abre el calendario (WASD para moverse, R para cerrar). |
| `css_adviento_check` | Admin | Comprueba que los premios existen en la store. |
| `css_adviento_dia <día\|off>` | Admin | Simula un día para hacer pruebas. Entrega premios reales. |
| `css_adviento_reset <steamid64> [día]` | Admin | Borra las casillas de un jugador. |

## Compilar

```
dotnet build -c Release AdventCalendar
```

## Licencias de terceros

`lib/StoreApi.dll` es la API de [cs2-store](https://github.com/schwarper/cs2-store), de schwarper, con licencia [GPL-3.0](https://github.com/schwarper/cs2-store/blob/main/LICENSE). Solo se usa para compilar y no se incluye en la release. En el servidor la aporta cs2-store.
