# bepinex-ogs-ts

> [!IMPORTANT]
> **In active development.** Validated in Subnautica Below Zero, Hyper
> Storm and Exo One (Mono) and Sable (IL2CPP), but every Unity game sets
> up its player differently, so expect the odd title where tracking
> needs a config tweak. Please report what you find on
> [Discord](https://discord.gg/yyCp5D2Ccp). If a game misbehaves and I
> don't already own it, a key for it is the fastest way to get it fixed.
>
> Development is funded by
> [sponsorship](https://github.com/sponsors/BrianGilbert), which moves this
> and the rest of the [OGS](https://github.com/Open-Game-Standards)
> work forward.

[BepInEx](https://github.com/BepInEx/BepInEx) plugin that publishes
[OGS Telemetry Standard (OGS-TS)](https://github.com/Open-Game-Standards/OGS-Telemetry-Standard)
motion telemetry from **any Unity game BepInEx injects — Mono or
IL2CPP** — zero game modification, LAN-only UDP multicast. Motion
platforms, haptic hardware, and dashboards on the local network can
follow the game. Motion frames carry pose **plus body dynamics**
(specific force + angular rates — the direct motion-cueing inputs,
derived from the pawn pose). The release zip carries both flavors in `mono/` and
`il2cpp/` subfolders; each is the same plugin built for its BepInEx
generation. Companion plugins:
[uevr-ogs-ts](https://github.com/BrianGilbert/uevr-ogs-ts) (UE VR) ·
[ue4ss-ogs-ts](https://github.com/BrianGilbert/ue4ss-ogs-ts) (UE flatscreen).

Support & discussion: [Discord](https://discord.gg/yyCp5D2Ccp)

Status: **in-game verified** (Mono + IL2CPP) — both flavors
observed publishing motion telemetry in live games (Mono on BepInEx 5,
IL2CPP on BepInEx 6 be.788), on top of the shared core's standalone
wire-path checks (150-tick synthetic pawn with body-dynamics
assertions, observed end-to-end on a real receiver).

## What it tracks — the pawn, not the camera

The motion object is the **player pawn** (tag `Player` by default, or
the main camera's root transform as a flatscreen pawn proxy). Unity's
world frame matches OGS-TS natively — values pass through unmapped.
The camera itself is engine-smoothed and a rig following it would feed
platform motion back into telemetry.

Some games do not turn the body with the view: on foot in Subnautica the
player root stays put while only the camera rig turns, so the pawn's
heading never changes. `Pawn.RotationSource` chooses where the published
rotation comes from:

| Value | Rotation |
|---|---|
| `Pawn` (default) | the pawn's own rotation |
| `CameraYaw` | the camera's heading plus the pawn's own tilt, so looking around steers the heading without pitching or rolling the rig |
| `Camera` | the camera's full rotation |
| `Auto` | `Pawn` while the body follows the view (swimming, vehicles, first-person pawns), `CameraYaw` while the body stays put as the camera turns (on foot); decided from observed motion, with a short blend so the heading never steps |

### Games the generic lookup misses

Some games have no `Player` tag and no `MainCamera`-tagged camera, so the
default lookup finds nothing and the stream stays at a constant zero pose
(the Monitor shows "pose only"). `Pawn.ComponentType` tracks the object
carrying a named game component instead:

```ini
[Pawn] ComponentType = CarPhysics, ChassisWithWheels   ; names from the game's code
       Pick = NearestCamera                            ; or First
       Survey = false                                  ; true = log what surrounds the camera
       AutoDiscover = true                             ; find the body from what the camera follows (set false to turn off)
       LearnedComponentType =                          ; written by AutoDiscover once a pick proves it moves
```

- The value is a comma-separated list in priority order; the first type
  with a live instance wins, and if several instances exist the one nearest
  the camera is picked (the camera follows the local player). Names match
  a type's simple name (`Car`) or full name (`car.Car`).
- If a lower-priority type was picked (say a menu garage car) and a
  higher-priority one appears later, the plugin switches to it.
- When the camera has no `MainCamera` tag the plugin uses the enabled
  camera with the highest depth.
- When the list is set there is no silent fallback to the camera root.
- `Survey = true` logs the cameras and the 15 objects nearest the camera,
  with their components, once per pawn binding and every 60 s while no
  pawn is found. Read the component names off the log to choose
  `ComponentType`. The log also warns when the picked object has not moved
  after 10 s.

**Automatic discovery (on by default).** `Pawn.AutoDiscover` finds the
body with no game knowledge, from what the camera does. When the tag lookup
finds nothing, the plugin tracks the camera root at once (as it does without
this option, so a game where that was already right loses nothing) and
observes the objects around the camera for a few seconds of gameplay; it
then switches to the one that moves with the camera at a steady distance. Static scenery, a frozen logical player object, and
independent movers (a ball, other cars) don't fit that pattern; a wheel or
mesh that rides on the body scores the same as the body, so the one nearest
the hierarchy root wins. The camera has to travel before it decides, the
camera root stays in place if nothing is found in 45 s, and a pick that
doesn't move while the camera does is rejected and the search starts again.
A game with a `Player` tag, a `ComponentType` or a saved
`LearnedComponentType` never reaches discovery.

Once discovery picks a body, its most specific game component type (engine
types are skipped; of the rest, the one with the fewest live instances) is
remembered for the session, so the next scene or vehicle binds at once
instead of observing again. When that pick has proven it moves, the type is
saved to `Pawn.LearnedComponentType` and tried first on later launches. It
is a type, not an object name or position, so it carries across levels and
vehicles that share the component. It never overwrites `ComponentType`, a
saved type that stops moving with the camera is forgotten automatically,
and clearing the entry resets it. A miss (the type isn't in this scene)
just falls through to discovery. The log names the
pick, its components and a suggested `ComponentType` to pin it. It can be
wrong in games with a detached camera (strategy, fixed cameras), split
screen, or another vehicle travelling in formation with the player.

**Built-in presets.** Games verified in-game get defaults seeded from their
process name (Turbo Golf Racing: `ComponentType = CarPhysics,
ChassisWithWheels`; Subnautica Below Zero: `RotationSource = Auto`). A
preset only seeds the default written into `ogs.telemetry.cfg` on first
run; once the file exists its values win, so edit freely, and delete the
file to re-seed.

`Auto` is tuned in the `[Heading]` section of the same per-game config file
(defaults suit on-foot Subnautica; all take effect when the pawn is
re-resolved, i.e. on the next game load):

| Key | Default | Meaning |
|---|---|---|
| `EnterCameraDeg` | 20 | camera turn needed, with a still body, to follow the camera. Raise it in games where head-look in vehicles trips the switch; lower it to switch sooner |
| `StillBodyDeg` / `StillBodyRatio` | 6 / 0.2 | the body counts as still below this many degrees and this share of the camera's turn |
| `ExitBodyDeg` / `ExitBodyRatio` | 12 / 0.5 | body turn (degrees, and share of the camera's) that hands the heading back to the pawn |
| `DecaySeconds` | 1.5 | memory of the turn measurement |
| `MinDwellSeconds` | 1.0 | minimum time between switches |
| `BlendSeconds` | 0.5 | how long the heading eases across a switch |

If head-look keeps switching in a game, raise `EnterCameraDeg`, or set
`RotationSource = Pawn` (or `CameraYaw` to stay on the camera heading).

The log line `[ogs] heading source: camera|pawn (Auto)` marks each switch.
Body dynamics are derived from the published rotation, so a camera source
makes head-turns register as angular velocity.

Body dynamics ride the `bodyDynamics` union variant in every motion
frame: **specific force** (m/s², accelerometer-style: `f = a − g`,
+1 g on the up axis at rest — what an IMU reads) and **angular
velocity** (rad/s, C-4 body-frame sense: +x nose-up, +y nose-right,
+z right-side-down), derived each frame from the pawn's pose by the
vendored `BodyDynamicsDeriver` (position-delta velocity, τ = 0.1 s
acceleration low-pass, teleport reset, quaternion-delta rates with no
Euler representation-flip artifacts — see GameConn `docs/cueing.md`
for the receiver-side value). Disable with the `Dynamics` config entry
(default on; takes effect when the pawn is (re)resolved).

Pauses are reported on the ratified `session.paused` channel (standard
CONVENTIONS C-11): while the game's `Time.timeScale` is 0 the plugin
keeps streaming on real time with the pause flag set and no dynamics, so
motion rigs come to rest instead of freezing mid-move or dropping the
session. Games that pause without touching `timeScale` aren't detected.
Disable with the `PauseDetection` config entry (default on).

The session gravity in discovery is the game's own `Physics.gravity`
(standard CONVENTIONS C-12), polled once a second, and the derived
specific force reacts to the same gravity, so heave at rest reads the
game's |g| rather than an assumed 9.80665. Games without Unity's 3D
physics module keep standard gravity. Disable with the
`GravityFromPhysics` config entry (default on).

## Compatibility

| Unity band | Status | Notes |
|---|---|---|
| Any IL2CPP game (Unity 2019+ typical) | ✅ | `il2cpp/` flavor on [BepInEx 6](https://builds.bepinex.dev/projects/bepinex_be) (be.788+); plugin code runs on BepInEx's .NET 6 sidecar, not inside IL2CPP |
| 2018.3 – 2021.1 (Mono) | ✅ | .NET 4.x runtime is the default (Mono ≈ 4.7.1) |
| 2021.2 – 2023.x (Mono builds) | ✅ | Mono 6 / .NET 4.8-era runtime |
| Unity 6 (Mono builds) | ⚠️ | one game tried (6000.3.1) does not load: its standard library is stripped, see [Known limitations](#known-limitations) |
| 2017.2 – 2018.2 (Mono) | ⚠️ | works if the developer selected the then-experimental ".NET 4.x" scripting profile |
| ≤ 2017.1, or any game on the legacy .NET 3.5 profile | ❌ | the vendored core (System.Memory) can't target net35 — older BepInEx catalog |

The binding constraint on Mono is the game's **.NET runtime**, not
Unity's API — every Unity surface this plugin touches has been
ABI-stable since the Unity 5 era. **Quick check** for a specific game,
look in `<game>/<Game>_Data/`:

- `GameAssembly.dll` present → **IL2CPP** → BepInEx 6 + the `il2cpp/`
  flavor
- only `Assembly-CSharp.dll` / `UnityEngine.dll` → **Mono** → check the
  Unity version (BepInEx prints it on first run) → 2018.3+ should work

If the game is Mono and 2018.3+ and the plugin still doesn't load, check the BepInEx console/log — a `TargetFramework` load failure means the game shipped on the legacy profile.

**Two 10-second checks** (no guessing) for a Mono game:

- **Before installing anything:** right-click
  `<game>\<Game>_Data\Managed\mscorlib.dll` → Properties → Details —
  File version `2.0.x` = legacy .NET 3.5 (incompatible);
  `4.0.x` = .NET 4.x (compatible)
- **From the BepInEx log** (`<game root>\BepInEx\LogOutput.log`):
  `CLR runtime version: 2.0.50727.x` = legacy (incompatible);
  `4.0.30319.x` = .NET 4.x (compatible). `0 plugins to load` next to a
  `Chainloader` line means BepInEx rejected the plugin — look up for
  the reason.

## Known limitations

**Unity 6 Mono builds with a stripped class library.** Unity 6 strips
unused code out of the Mono class library (`mscorlib.dll`, `System.dll`,
`System.Core.dll`, ...) at build time. Observed on Vecter (Unity 6000.3.1,
Mono): the game's `mscorlib.dll` is about 2.6 MB with 2,035 types, against
about 4.5 MB and 2,993 types unstripped. Two layers fail:

- **BepInEx 5.4.23.6 does not start.** No `BepInEx\LogOutput.log` is
  written; the game folder gets a `preloader_*.log` with
  `MissingMethodException: ... System.Reflection.Module.GetPEKind(...)`
  (BepInEx's and MonoMod's ARM platform check) and, once that call is
  patched out, `... System.IO.File.ReadAllLines(string)`. The same
  `GetPEKind` call is in BepInEx 6's `PlatformUtils.cs`.
- **The plugin itself needs APIs the stripped libraries lack.** A
  comparison of the plugin's BCL references against the game's assemblies
  found about 32 missing members in 12 types plus 8 missing types,
  including `System.Numerics.Vector3` / `Quaternion` constructors and
  operators, `Socket.SendTo`, `DateTimeOffset.UtcNow`, and the
  `NetworkInterface` multicast and gateway members used to choose the
  multicast interface. Name and parameter-count matching only, so
  approximate.

**Don't swap in unstripped libraries.** The corlibs on
`unity.bepinex.dev/corlibs/<version>.zip` come from a different runtime
build than the game's player. Replacing the game's `mscorlib.dll` and
friends with them made the game fail during load with
`DllNotFoundException: System.Native`. Restore the originals (or Steam's
*Verify integrity of game files*).

**Quick check** for a Unity 6 Mono game before installing: a
`Managed\mscorlib.dll` far smaller than a normal Mono one (about 2.6 MB
here) and a `preloader_*.log` with a `MissingMethodException` in the game
folder after the first launch.

**What would fix it:** the developer building with *Managed Stripping
Level: Disabled* (fixes both layers); or a standalone Doorstop loader plus
a plugin build that avoids the missing APIs (own vector and quaternion
types, different socket and network calls). Neither exists yet.

**Other known limits** (details in their sections above):

- `Pawn.AutoDiscover` can be wrong with a detached camera (strategy or
  fixed cameras), split screen, or another vehicle travelling in
  formation with the player, and needs the camera to move before it
  decides.
- `RotationSource = Auto` can take head-look in a vehicle for an on-foot
  turn; raise `[Heading] EnterCameraDeg` (or fix the source) for that game.
- Built-in presets only seed the default of a new config file; an
  existing `ogs.telemetry.cfg` keeps its values.

## Install

1. Install BepInEx (x64) into the game — **which flavor depends on the
   game's scripting backend**:

   - **Mono game** (no `GameAssembly.dll`): [BepInEx 5](https://github.com/BepInEx/BepInEx/releases)
     (5.4.22+). Mono games with a .NET 4.7.2+ runtime only.
   - **IL2CPP game** (`GameAssembly.dll` present): [BepInEx 6 Unity-IL2CPP](https://builds.bepinex.dev/projects/bepinex_be)
     (be.788 or newer) — grab the `BepInEx-Unity.IL2CPP-win-x64` zip.

   **Where:** extract the BepInEx zip into the game's installation root —
   the folder containing the game's `.exe` (Steam: right-click the game →
   Manage → **Browse local files**). This creates the `BepInEx\` folder
   next to the exe; run the game once to finish setup (BepInEx 6 spends
   the first launch generating interop assemblies — expect a longer
   black screen and check `LogOutput.log` before assuming failure).
2. From the release zip, extract the **matching subfolder's contents** —
   `mono/` for BepInEx 5, `il2cpp/` for BepInEx 6 — into that game's
   `<game root>\BepInEx\plugins\` — **every DLL in it** (IL2CPP:
   `ogs_telemetry.dll` alone; Mono: it plus four .NET Framework
   backport DLLs such as `System.Memory.dll`). E.g.
   `C:\...\steamapps\common\<GameName>\BepInEx\plugins\`.
   If `plugins\` doesn't exist yet, create it. BepInEx plugins are
   per-game: the DLLs only load for the game you place them in.
   Don't mix flavors — the `il2cpp/` DLL is a .NET 6 assembly BepInEx 5
   cannot load, and vice versa.
3. Run the game once; configure `<game>/BepInEx/config/ogs.telemetry.cfg`:
   ```ini
   [Transport] Group = 239.255.17.17   ; Port = 40123   ; Ttl = 1
   [Telemetry] MotionHz = 60           ; ObjectName = player   ; PauseDetection = true
               GravityFromPhysics = true
   [Pawn] Tag = Player                 ; CameraRootFallback = true
          RotationSource = Pawn        ; Pawn | CameraYaw | Camera | Auto
   ```
4. Pair with a receiver — e.g. ogs-ts-dotnet's
   `dotnet run --project src/TelemetryReceive`.

If the tag finds nothing, the plugin falls back to the camera root and
logs a pointer — set `Pawn.Tag` to your game's player tag (BepInEx
console `logoutput.log`) if the default misses.

### Rai Pal deployments

Rai Pal ships its own BepInEx copy and installs it under
`%APPDATA%\raicuparta\rai-pal\data\installed-mods\<hash>\bepinex\`
(hash-named per game) — **not** the game root. Plugins go into that
folder's `BepInEx\plugins\`, and logs land there too. The plugin
derives the game name from the process (Rai Pal's hash folder name is
meaningless), so expect `game=<exe name>` on the wire.

## Standalone test (no game)

`dotnet run --project Smoke` — drives the same vendored core publisher
with a synthetic pawn; exit 0 = pass. TelemetryReceive beside it shows
`bepinex_smoke` on the wire.

## How it builds without Unity's DLLs

Both flavors compile the entire vendored Ogs.Telemetry core
(`sync-core.ps1`) straight into the plugin — it writes Cap'n Proto frames
itself, so there is no serializer DLL, and no game DLLs are required at
build time:

- **Mono** (`plugin/`, net472): `vendor/UnityEngineStub` is a
  compile-only stub assembly (named `UnityEngine`) mirroring the small
  stable API surface the plugin touches; the real game's UnityEngine
  provides the implementation at runtime. `prepare-refs.ps1` fetches
  the real LGPL BepInEx.dll reference.
- **IL2CPP** (`plugin-il2cpp/`, net6.0): references the official
  BepInEx NuGet feed — `BepInEx.Unity.IL2CPP` (be.788, the current
  IL2CPP distribution) plus its interop-mapped UnityEngine reference
  libs, both compile-only. The plugin runs on BepInEx's .NET 6
  CoreCLR sidecar, not inside IL2CPP — only game-object access crosses
  the interop boundary.

The Mono flavor's .NET Framework backport DLLs (System.Memory and its
closure) ride in the release zip as siblings under `mono/`; the IL2CPP
flavor is the single plugin DLL under `il2cpp/`.

## Layout

| Path | Contents |
|---|---|
| `plugin/` | BepInEx 5 Mono glue (config, pawn ladder, Update tick) |
| `plugin-il2cpp/` | BepInEx 6 IL2CPP glue (BasePlugin + injected tick component) |
| `OgsTelemetry/` | vendored engine-agnostic core (source) |
| `vendor/UnityEngineStub/` | compile-only UnityEngine stub (Mono flavor) |
| `Heading/` | engine-agnostic rotation-source selector and component-type lookup (both flavors compile them) |
| `Shared/` | per-game presets and camera-following pawn discovery (both flavors compile them) |
| `Smoke/` | standalone wire-path verification |
