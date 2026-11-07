# Can Em' Thogether

Mod cooperativo en línea para **Can 'Em All** (Steam). Convierte el juego de un jugador en una fábrica compartida para 2-4 jugadores: mismos bichos, mismas latas, mismo dinero y mismo día.

> **Aviso:** proyecto de aficionados, no oficial y sin relación con los creadores del juego. Hace falta una copia legal del juego en cada ordenador. Este repositorio no contiene archivos del juego.
>
> **Estado: experimental.** El multijugador se ha añadido a un juego que no lo tiene, así que puede haber fallos. Hay una lista de límites conocidos más abajo.

---

## Qué hace

| Función | Cómo funciona |
|---|---|
| **Salas por Steam** | El líder crea una sala y comparte su ID (o invita a amigos desde Steam). Los demás se unen con el ID o aceptando la invitación. Solo amigos. |
| **El líder manda** | El líder decide cuándo empieza la partida. Al pulsar *Continuar* o *Nueva partida*, a todos los demás se les carga **la partida del líder**. Los invitados no guardan: su partida original se restaura al salir. |
| **Fábrica compartida** | El líder simula camión, bichos, trituradora y latas. Los invitados ven los mismos bichos y latas, pueden golpearlos, aspirarlos, cargarlos y lanzarlos. |
| **Dinero, armas y mejoras compartidos** | Lo que ganan, compran o desbloquean unos lo tienen todos, al instante. |
| **Ascensor sincronizado** | El día solo termina cuando **todos** están dentro del ascensor. |
| **Continuar día** | Solo lo pulsa el líder y se aplica a todos. |
| **Reloj del día/noche sincronizado** | El líder envía el tiempo restante y los demás se corrigen. |
| **Tubo → votación** | Tirarse por el tubo ya no acaba el día (para evitar fallos): te teletransporta al centro de la fábrica. Para saltar el día se vota con **F7** y se salta cuando votan todos. |
| **Pausa sin parar el tiempo** | En una sala, la pausa no congela la partida (si no, se desincroniza con los demás). |
| **Jugadores visibles** | Cada jugador ve a los demás como un muñeco con su nombre. |
| **Si el líder se va** | Los invitados salen de la sala, recuperan su partida y vuelven al menú. |
| **Red optimizada** | Los mensajes se agrupan, se comprimen y se limitan en frecuencia para reducir el lag por internet (ver [Rendimiento de red](#rendimiento-de-red)). |
| **Conexión por IP (opcional)** | Alternativa directa por TCP, útil con VPN o con el puerto abierto. |

---

## Requisitos

- **Can 'Em All** comprado en Steam, en la misma versión para todos los jugadores.
- **[MelonLoader](https://github.com/LavaGang/MelonLoader)** instalado en el juego (versión para juegos IL2CPP). Abre el juego una vez con él instalado para que se cree la carpeta `Mods`.
- Windows.
- Todos los jugadores deben ser **amigos en Steam** (para el método de salas por Steam).

---

## Instalación (jugadores)

1. Instala MelonLoader en la carpeta del juego y ábrelo una vez.
2. Descarga `CanEmThogether.dll` de la sección *Releases* del repositorio (o compílalo, ver abajo).
3. Copia `CanEmThogether.dll` a `Can 'Em All\Mods\`.
4. Abre el juego **desde Steam**. Dentro del juego, pulsa **F6** para ver el panel del mod.

Todos los jugadores (líder e invitados) necesitan exactamente la misma versión del mod.

---

## Cómo se juega

1. Todos abren el juego y se quedan en el **menú principal**. **F6** muestra u oculta el panel (los botones se pulsan con el cursor libre: menú o pausa).
2. **Líder:** pulsa **Crear sala (Steam)**. Aparece el **ID de sala**; un clic en él (o en *Copiar ID*) lo copia al portapapeles. También puede usar *Invitar amigos (Steam)*.
3. **Invitados:** pulsan **Pegar ID** (o *Escribir ID* y teclean los números) y luego **UNIRSE A LA SALA**. También sirve aceptar la invitación de Steam.
4. Cuando están todos, **el líder pulsa Continuar o Nueva partida** en el menú del juego. A los demás se les carga la misma partida automáticamente. Los invitados no pueden iniciarla.
5. Durante la partida:
   - **Terminar el día:** todos deben entrar en el ascensor. Cuando entra el último, el día termina para todos a la vez.
   - **Continuar al día siguiente:** solo el líder, desde la pantalla de mejoras.
   - **Saltar el día antes de tiempo:** cada jugador pulsa **F7** (o el botón del panel). Se salta cuando votan todos.
   - **Tubo:** si alguien se tira, vuelve al centro de la fábrica en lugar de terminar el día.

### Teclas

| Tecla | Acción |
|---|---|
| **F6** | Mostrar/ocultar el panel del mod |
| **F7** | Votar para saltar el día (pulsar otra vez retira el voto) |

### Qué pasa con tu partida guardada

- Al unirte, el mod guarda una copia de tu partida (`…mpbackup`) y carga la del líder.
- Los invitados **no guardan** durante la sesión; manda la partida del líder.
- Al salir de la sala o cerrar el juego, tu partida original se restaura. Si el juego se cierra de golpe, se restaura la siguiente vez que lo abras.

---

## Conexión por IP (opcional)

Está pensada para quien no pueda usar las salas de Steam, o para jugar con una **VPN** (Radmin VPN, ZeroTier, Tailscale, Hamachi…), que suele dar menos retraso que las salas.

1. En el panel F6 abre **▶ Opción por IP**.
2. El líder pulsa **Crear sala por IP**. Debe tener el puerto **7777 (TCP)** abierto en su router, o estar en la misma VPN que los demás.
3. Los invitados escriben o pegan la IP del líder (`85.12.34.56` o `85.12.34.56:7777`) y pulsan **UNIRSE POR IP**.

El puerto se cambia en la configuración (`IpPort`).

---

## Rendimiento de red

El mod no es un multijugador de servidor dedicado: uno de los jugadores (el líder) simula la fábrica y envía el estado a los demás. Para que funcione bien por internet:

- **Agrupado y comprimido.** En lugar de cientos de mensajes pequeños por segundo, cada ~70 ms se envía un único paquete comprimido (Deflate + Base64). Los eventos urgentes (golpes, soltar/lanzar, comprar mejoras, ascensor…) salen en el siguiente fotograma.
- **Datos compactos.** Posiciones y velocidades con precisión de 1 cm y sin ceros sobrantes; solo se reenvía lo que cambia.
- **Menos envíos por Steam.** Unos 8 envíos de estado por segundo; los demás extrapolan el movimiento con la velocidad para que se vea fluido.
- **Medición en pantalla.** El panel F6 muestra `Red: ↑ KB/s ↓ KB/s paq/s Ping`. Cada 10 s se escribe lo mismo en el log. Con esto se puede ver si el problema es ancho de banda, ritmo de mensajes o latencia.

Consejos si hay lag:

1. Comprueba el **ping** del panel. El mod no puede bajarlo; solo reduce lo que se envía.
2. Prueba la **conexión por IP con una VPN**: suele tener mucho menos retraso que el chat de las salas de Steam.
3. Sube `NetFlushMs` (por ejemplo 100) si el ancho de banda es el límite; bájalo (40-50) si la conexión es buena.
4. El líder debe ser quien tenga **mejor conexión y mejor PC**.
5. No minimices la ventana del juego: Unity la ralentiza. (El mod quita el vSync cuando la ventana pierde el foco, pero lo mejor es no minimizar.)

---

## Configuración

Se genera en `UserData\MelonPreferences.cfg`, sección `CanEmThogether`.

| Clave | Por defecto | Descripción |
|---|---|---|
| `ShareFactory` | `true` | `true` = fábrica compartida (el líder simula). `false` = cada jugador tiene su propia fábrica (solo se comparten dinero, armas, mejoras y ascensor). |
| `MaxPlayers` | `4` | Jugadores máximos de la sala de Steam. |
| `NetFlushMs` | `70` | Intervalo de envío agrupado por Steam, en ms. |
| `IpPort` | `7777` | Puerto de la conexión por IP. |
| `TubeTeleport` | *(vacío)* | Destino al tirarse por el tubo, como `x,y,z`. Vacío = centro de la fábrica calculado automáticamente (se escribe en el log). |
| `GuestLocalHitFx` | `true` | El invitado ve al instante sangre/ragdoll de sus golpes (el líder sigue mandando el estado real). |
| `AvatarYOffset` | `0` | Sube o baja los muñecos de los demás jugadores si se hunden o flotan. |
| `TestName` | `Jugador2` | Nombre de la 2ª ventana de pruebas. |
| `GateMethod` | *(vacío)* | Uso avanzado/depuración. |

---

## Simular lag online en una prueba local

Con dos ventanas en el mismo PC la conexión es instantánea, así que no se nota el lag de internet. Para simularlo:

1. Con las dos ventanas conectadas, en el panel F6 de **una sola ventana** (por ejemplo la del invitado) pulsa **Simular lag (solo pruebas)** para cambiar entre `NO`, `50`, `100`, `200` y `400` ms *por sentido*.
2. El ping resultante es el doble (el panel lo muestra en `Ping:`). Valores de referencia: 50 ms ≈ conexión buena, 100 ms ≈ internet normal o sala de Steam, 200-400 ms ≈ mala conexión.
3. Juega como lo haría un invitado remoto: golpear bichos, aspirar latas, votar, ascensor… y observa si se ve fluido.
4. Mira los **KB/s** del panel: son los datos reales que se enviarían por internet (la compresión es la misma). Si suben y bajan por debajo de ~30 KB/s, una conexión doméstica normal los aguanta sin problema.

Limitaciones: el simulador añade retraso pero no pérdida de paquetes, y no reproduce los límites de ritmo del chat de las salas de Steam (la prueba local va por IP). Para eso, prueba con un amigo y compara los números del panel.

---

## Probar en un solo PC (2ª ventana)

En el panel F6: **▶ Opción por IP → Probar en este PC: crear sala + abrir 2ª ventana**. Se abre otra copia del juego que se une sola por `127.0.0.1` con el nombre `TestName`. La ventana de prueba **no escribe ni restaura tu guardado**. Esta prueba va por IP: las salas de Steam no se pueden probar con la misma cuenta en dos ventanas.

---

## Compilar desde el código

Requisitos: [SDK de .NET](https://dotnet.microsoft.com/download) (6 o superior), el juego instalado con MelonLoader y haberlo abierto una vez (para que MelonLoader genere las DLL de interoperabilidad en `MelonLoader\Il2CppAssemblies`).

1. Abre `CanEmThogether.csproj` y ajusta:
   - `GameDir`: carpeta de instalación del juego.
   - `MLNet`: `net6` o `net8`, según la carpeta que exista dentro de `MelonLoader\`.
2. En la carpeta del proyecto: `dotnet build -c Release`
3. El resultado es `bin\Release\net6.0\CanEmThogether.dll` (también se copia a `Mods`).

El resultado **no depende de las rutas**: se compila una vez y se reparte la DLL.

### Estructura

| Archivo | Contenido |
|---|---|
| `Mod.cs` | Núcleo: sala, panel F6, flujo de inicio, dinero/armas/mejoras, ascensor, día, votación, tubo, parches de Harmony. |
| `FactorySync.cs` | Fábrica compartida: bichos (líder real, invitados con marionetas), aspiradora, trituradora, dinero de ventas. |
| `CanSync.cs` | Latas compartidas. |
| `Transport.cs` | Interfaz de transporte y salas de Steam (Facepunch.Steamworks del juego). |
| `TcpTransport.cs` | Conexión directa por IP. |
| `BatchTransport.cs` | Agrupado, compresión y estadísticas de red. |

---

## Solución de problemas

**El panel F6 no aparece:** MelonLoader no ha cargado el mod. Comprueba que `CanEmThogether.dll` está en `Mods` y que en `MelonLoader\Logs\Latest.log` aparece `Can Em' Thogether v…`.

**Se queda en "Creando sala…":** abre el juego **desde Steam** y espera unos segundos. Si persiste, revisa en el log las líneas que empiezan por `Steam:`.

**Error al compilar:** guarda la salida con `dotnet build -c Release > errores.txt 2>&1` y revisa las líneas con `error`.

**Algo falla en partida:** cada ventana escribe su propio log en `UserData\CanEmThogether_<nombre>.log` (el del líder y el del invitado). Al abrir un *issue* en el repositorio, adjunta:

1. Los dos logs (líder e invitado).
2. La versión del mod (`Can Em' Thogether v…` en `Latest.log`).
3. Qué hacías justo antes del fallo y si eras líder o invitado.
4. El ping y los KB/s que muestra el panel.

No hace falta adjuntar partidas guardadas ni la DLL.

---

## Límites conocidos

- Es experimental: pueden aparecer desincronizaciones (bichos o latas fuera de sitio, que se corrigen solas en pocos segundos).
- Las salas de Steam usan el chat de la sala como canal: añade latencia respecto a una conexión directa.
- Si el líder se va, la sala se cierra (no hay cambio de líder).
- No hay protección contra trampas: juega con gente de confianza.
- Torretas, robots y efectos visuales de impacto no se sincronizan al detalle.
- Si el juego se actualiza, el mod puede dejar de funcionar hasta adaptarlo.

---

## Contribuir

Las contribuciones son bienvenidas. Antes de abrir un *pull request*, compila y prueba con dos ventanas (ver arriba). Para informar de fallos, abre un *issue* con los datos de la sección *Solución de problemas*.

## Licencia

Código bajo licencia MIT (ver `LICENSE`). El juego **Can 'Em All** y todos sus recursos pertenecen a sus respectivos autores; este proyecto no los incluye ni distribuye.
