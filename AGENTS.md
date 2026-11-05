# bepinex-ogs-ts — working conventions

- Motion object is the player PAWN (tag Player / camera-root proxy),
  not the camera — KB ruling C-20261010-MBP-08. Unity frame maps to
  OGS-TS natively (no conversion); the vendored C# core IS the wire.
  `Pawn.RotationSource` (Pawn | CameraYaw | Camera | Auto) is the opt-in
  for games whose body does not turn with the view; the logic lives in
  Heading/HeadingSelector.cs, compiled into both flavors and into Smoke.
- OgsTelemetry/ is vendored source from ../ogs-ts-dotnet (sync-core.ps1,
  receiver excluded). The core carries a netstandard2.0 TFM specifically
  for this plugin (BepInEx 5 Mono runs .NET Framework).
- vendor/UnityEngineStub is a compile-only stub ASSEMBLY NAMED
  "UnityEngine" (BepInEx.dll's MonoBehaviour lives in the classic
  facade name) — extend only with what the plugin references; the real
  game module provides implementation at runtime.
- lib/BepInEx.dll is fetched (never committed) by prepare-refs.ps1
  (LGPL; asset resolved via gh api — set GH_TOKEN in CI).
- Two flavors, one release zip: `plugin/` (BepInEx 5 Mono, net472)
  and `plugin-il2cpp/` (BepInEx 6 Unity-IL2CPP, net6.0), zipped under
  `mono/` and `il2cpp/` subfolders. Same GUID/config keys/wire
  behaviour; keep the glue files mirrored when either changes.
- IL2CPP works because BepInEx 6 runs plugins on its own .NET 6
  CoreCLR sidecar; only game-object access crosses Il2CppInterop. The
  vendored core writes Cap'n Proto itself (generated writers, no
  reflection), so no serializer DLL ships in either flavor.
  `plugin-il2cpp` refs BepInEx.Unity.IL2CPP 6.0.0-be.788
  (== current official IL2CPP distribution; pin, don't float) + its
  Il2CppInterop.ReferenceLibs from nuget.bepinex.dev, compile-only —
  none of their DLLs ship in the zip. Do NOT use UnityEngine.Modules
  (the BepInEx-6-Mono template default): it ships vanilla Mono refs —
  MonoBehaviour isn't injectable and value types aren't interop-mapped.
- Il2CppInterop (1.5.x lineage) does NOT map UnityEngine value types to
  System.Numerics — proxies keep Unity's names/layout (lowercase
  fields); convert per component exactly like the Mono flavor.
- The Mono plugin targets net472 and ships System.Memory's closure
  (System.Memory/Buffers/Numerics.Vectors/Runtime.CompilerServices.Unsafe)
  beside ogs_telemetry.dll. IL2CPP games run the net6.0 flavor on
  BepInEx 6 be.788+; it ships ogs_telemetry.dll alone.
- Product docs describe current state only; history lives in the
  Open-Game-Standards knowledge base.
