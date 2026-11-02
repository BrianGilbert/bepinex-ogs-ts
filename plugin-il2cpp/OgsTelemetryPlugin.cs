// OgsTelemetryPlugin.cs — OGS-TS telemetry for BepInEx 6 (Unity, IL2CPP).
//
// IL2CPP twin of ../plugin/OgsTelemetryPlugin.cs (BepInEx 5, Mono): same
// vendored Ogs.Telemetry core, same config keys and defaults, same pawn
// ladder and wire behaviour. Everything differs only at the engine seam:
//
//   * BepInEx 6 IL2CPP plugins are plain classes — BasePlugin.Load()
//     instead of BaseUnityPlugin — and reach Unity's update loop by
//     adding an injected MonoBehaviour component.
//   * The plugin (including the Cap'n Proto serializer and its
//     reflection) runs on BepInEx's own .NET 6 CoreCLR sidecar, NOT
//     inside IL2CPP — only game-object access crosses the Il2CppInterop
//     boundary. That is why the serializer survives a backend that has
//     no JIT of its own.
//   * Il2CppInterop emits its proxy value types (Vector3, Quaternion)
//     with Unity's original names, layout and lowercase fields — the
//     same shape the compile-time reference libs carry — so the pose
//     getters convert per component, exactly like the Mono plugin.
//
// Pawn discovery ladder (first hit wins; all overridable via config):
//   1. Configured GameObject tag (default "Player")
//   2. Camera.main transform root (flatscreen: camera sits on the player)
// Re-resolves automatically if the pawn is destroyed (scene change).
//
// The object NAME stays the stable role name ("player" by default) —
// receivers match motion frames to discovery by it. What is actually
// being tracked is surfaced as the discovery typeLabel: the pawn's
// GameObject name with Unity instantiation suffixes stripped
// ("Player(Clone)" / "Player (2)" -> "Player"), updated on every
// (re)resolution; a changed label re-sends discovery per the standard's
// re-send-on-change rule. Config ObjectTypeLabel overrides.
using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using Ogs.Telemetry;

namespace Ogs.BepInEx
{
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class OgsTelemetryPlugin : BasePlugin
{
    public const string PluginGuid = "ogs.telemetry";
    public const string PluginName = "OGS-TS Telemetry";
    public const string PluginVersion = "0.1.0";

    // set before AddComponent runs, so the component's OnEnable (which
    // fires synchronously inside AddComponent) can read config through it
    internal static OgsTelemetryPlugin Instance = null!;

    internal ConfigEntry<string> _group = null!;
    internal ConfigEntry<int> _port = null!;
    internal ConfigEntry<int> _ttl = null!;
    internal ConfigEntry<double> _motionHz = null!;
    internal ConfigEntry<string> _objectName = null!;
    internal ConfigEntry<string> _typeLabel = null!;
    internal ConfigEntry<string> _instanceLabel = null!;
    internal ConfigEntry<string> _pawnTag = null!;
    internal ConfigEntry<bool> _useCameraRoot = null!;
    internal ConfigEntry<bool> _dynamics = null!;
    internal ConfigEntry<bool> _pauseDetect = null!;
    internal ConfigEntry<bool> _gravityFromPhysics = null!;

    public override void Load()
    {
        Instance = this;

        _group = Config.Bind("Transport", "Group", "239.255.17.17",
                             "UDP multicast group");
        _port = Config.Bind("Transport", "Port", 40123, "UDP multicast port");
        _ttl = Config.Bind("Transport", "Ttl", 1,
                           "Multicast TTL (1 = local subnet)");
        _motionHz = Config.Bind("Telemetry", "MotionHz", 60.0,
                                "Motion frame rate cap");
        _objectName = Config.Bind("Telemetry", "ObjectName", "player",
                                  "Object name in discovery");
        _instanceLabel = Config.Bind("Telemetry", "InstanceLabel", "",
                                     "Names this instance in receivers' session pickers " +
                                     "when more than one can run on a network (e.g. Rig 2)");
        _typeLabel = Config.Bind("Telemetry", "ObjectTypeLabel", "",
                                 "Object typeLabel in discovery (display " +
                                 "flavor). Empty = auto: the tracked pawn's " +
                                 "GameObject name with instantiation " +
                                 "suffixes stripped");
        _pawnTag = Config.Bind("Pawn", "Tag", "Player",
                               "GameObject tag that marks the player pawn");
        _useCameraRoot = Config.Bind("Pawn", "CameraRootFallback", true,
                                     "If the tag finds nothing, use the main " +
                             "camera's root transform (flatscreen pawn proxy)");
        _dynamics = Config.Bind("Telemetry", "Dynamics", true,
                                "Send the bodyDynamics union variant " +
                                "(specific force + angular rates, derived " +
                                "from the pawn pose — the direct motion-cueing " +
                                "inputs). Takes effect when the pawn is " +
                                "(re)resolved");
        _pauseDetect = Config.Bind("Telemetry", "PauseDetection", true,
                                   "Report the game as paused (OGS-TS " +
                                   "session.paused, CONVENTIONS C-11) while " +
                                   "Time.timeScale is 0, so motion rigs come " +
                                   "to rest instead of freezing mid-move");
        _gravityFromPhysics = Config.Bind("Telemetry", "GravityFromPhysics", true,
                                          "Declare the game's Physics.gravity as " +
                                          "the session gravity (OGS-TS CONVENTIONS " +
                                          "C-12), following changes, so derived " +
                                          "heave reacts to the game's real gravity");

        AddComponent<OgsBehaviour>();
        Log.LogInfo("[ogs] OGS-TS telemetry (IL2CPP) loaded: pawn '" +
                    _objectName.Value + "' -> " + _group.Value + ":" +
                    _port.Value);
    }

    internal static string GameName()
    {
        try
        {
            // Paths.ProcessName is the game's actual executable name; do NOT
            // derive from the plugin location — mod managers deploy plugins
            // into per-game hash folders under AppData
            var name = Paths.ProcessName;
            if (!string.IsNullOrEmpty(name)) return name;
            return "unknown-game";
        }
        catch
        {
            return "unknown-game";
        }
    }
}

// Injected component carrying the Unity tick. AddComponent (called from
// Load) registers the type with IL2CPP and forwards Unity messages here;
// managed fields and the publisher live on the CoreCLR side. Attached to
// BepInEx's manager object, so it survives scene changes.
public class OgsBehaviour : MonoBehaviour
{
    TelemetryPublisher _publisher = null!;
    TelemetrySender _sender = null!;
    Transform _pawn = null!;
    float _lastResolveAttempt = -1f;
    bool _warnedNoPawn;
    bool _wasPaused;
    float _lastGravityPoll = -1f;
    Func<System.Numerics.Vector3?>? _readGravity;

    // pose → body-dynamics derivation (vendored core, same semantics as
    // the C++ twin in ogs-ts-unreal and the Mono flavor)
    BodyDynamicsDeriver _deriver = new();

    void OnEnable()
    {
        var p = OgsTelemetryPlugin.Instance;
        _sender = new TelemetrySender(p._group.Value, p._port.Value,
                                      p._ttl.Value, loopback: true);
        _publisher = new TelemetryPublisher(_sender, OgsTelemetryPlugin.GameName(),
                                            p._motionHz.Value);
        _publisher.DeclareObject(p._objectName.Value);
        _publisher.SetInstance(p._instanceLabel.Value);
        _publisher.DeclarePause();
        if (!string.IsNullOrEmpty(p._typeLabel.Value))
            _publisher.SetObjectTypeLabel(p._typeLabel.Value); // config wins
        OgsTelemetryPlugin.Instance.Log.LogInfo(
            "[ogs] OGS-TS telemetry enabled: pawn '" + p._objectName.Value +
            "' -> " + p._group.Value + ":" + p._port.Value);
    }

    void OnDisable()
    {
        _publisher?.Dispose();
        _publisher = null!;
        _sender?.Dispose();
        _sender = null!;
    }

    void Update()
    {
        var pub = _publisher;
        if (pub == null) return;

        if (_pawn == null && Time.time - _lastResolveAttempt > 1f)
        {
            _lastResolveAttempt = Time.time;
            _pawn = ResolvePawn();
            if (_pawn != null)
            {
                pub.BindPose(() => SafePos(), () => SafeRot());
                var p = OgsTelemetryPlugin.Instance;
                if (p._dynamics.Value)
                    pub.BindBodyDynamics(() =>
                        (_deriver.SpecificForce, _deriver.AngularVelocity));
                _deriver = new BodyDynamicsDeriver(); // fresh state per pawn
                if (string.IsNullOrEmpty(p._typeLabel.Value))
                    pub.SetObjectTypeLabel(CleanPawnName(_pawn.name));
                _warnedNoPawn = false;
            }
        }
        // derivation runs every frame the pawn exists (not just at the
        // publisher's cadence), so derivative denominators stay small
        // C-11: Update keeps running while Time.timeScale is 0, so the
        // stream continues through a pause on real time, reporting the
        // pause and deriving nothing across it
        bool paused = OgsTelemetryPlugin.Instance._pauseDetect.Value
                      && Time.timeScale == 0f;
        if (paused != pub.Paused) pub.SetPaused(paused);
        // C-12: the game's own gravity, polled at 1 Hz (it rarely changes;
        // SetGravity re-sends discovery only on change)
        if (OgsTelemetryPlugin.Instance._gravityFromPhysics.Value
            && Time.realtimeSinceStartup - _lastGravityPoll > 1f)
        {
            _lastGravityPoll = Time.realtimeSinceStartup;
            _readGravity ??= PhysicsGravityReader();
            if (_readGravity?.Invoke() is { } g) pub.SetGravity(g);
        }
        _deriver.GravityVector = pub.EffectiveGravity;
        if (_pawn != null && !paused)
        {
            if (_wasPaused)
                _deriver.Reset(SafePos(), SafeRot(), (double)Time.time);
            else
                _deriver.Sample((double)Time.time, SafePos(), SafeRot());
        }
        _wasPaused = paused;
        pub.Tick((double)Time.realtimeSinceStartup);
    }

    // pose getters that survive scene changes and menu phases: a destroyed
    // transform (scene unload) re-triggers resolution, and a degenerate
    // quaternion (menu-state fallback reads) ships identity instead of a
    // zero norm that trips receiver validation. Interop's proxy structs
    // mirror Unity's field layout — convert per component, as on Mono.
    System.Numerics.Vector3 SafePos()
    {
        try { var p = _pawn.position; return new System.Numerics.Vector3(p.x, p.y, p.z); }
        catch { _pawn = null!; return default; }
    }

    System.Numerics.Quaternion SafeRot()
    {
        try
        {
            var r = _pawn.rotation;
            if (r.x == 0f && r.y == 0f && r.z == 0f && r.w == 0f)
                return System.Numerics.Quaternion.Identity; // menu-phase garbage
            return new System.Numerics.Quaternion(r.x, r.y, r.z, r.w);
        }
        catch { _pawn = null!; return System.Numerics.Quaternion.Identity; }
    }

    Transform ResolvePawn()
    {
        var p = OgsTelemetryPlugin.Instance;
        try
        {
            var byTag = GameObject.FindWithTag(p._pawnTag.Value);
            if (byTag != null) return byTag.transform;
        }
        catch { /* tag not defined in this game */ }

        if (p._useCameraRoot.Value)
        {
            var cam = Camera.main;
            if (cam != null) return cam.transform.root;
        }

        if (!_warnedNoPawn)
        {
            _warnedNoPawn = true;
            OgsTelemetryPlugin.Instance.Log.LogWarning(
                "[ogs] no pawn found (tag '" + p._pawnTag.Value + "' / camera " +
                "root); retrying every second — set Pawn.Tag in the config");
        }
        return null!;
    }

    // "Player(Clone)" / "Player (Clone)" / "Player (2)" -> "Player": the
    // class-level name stays stable across respawn/instantiation, matching
    // the schema's stable-name requirement (instance-suffixed names would
    // churn discovery re-sends on every respawn)
    // Physics.gravity through reflection: UnityEngine.PhysicsModule is
    // absent from games without 3D physics (a direct reference would fail
    // to load) and is not among this flavor's compile refs; the interop
    // proxy struct keeps Unity's x/y/z fields. Unity's frame is OGS-TS
    // native, so the vector passes through. Null = no physics module
    // (retried at the next poll).
    static Func<System.Numerics.Vector3?>? PhysicsGravityReader()
    {
        try
        {
            var physics = Type.GetType("UnityEngine.Physics, UnityEngine.PhysicsModule", false);
            if (physics == null)
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if ((physics = asm.GetType("UnityEngine.Physics", false)) != null) break;
            var prop = physics?.GetProperty("gravity",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var vt = prop?.PropertyType;
            var fx = vt?.GetField("x"); var fy = vt?.GetField("y"); var fz = vt?.GetField("z");
            if (prop == null || fx == null || fy == null || fz == null) return null;
            return () =>
            {
                try
                {
                    var v = prop.GetValue(null);
                    return new System.Numerics.Vector3(
                        (float)fx.GetValue(v)!, (float)fy.GetValue(v)!, (float)fz.GetValue(v)!);
                }
                catch { return null; }
            };
        }
        catch { return null; }
    }

    static string CleanPawnName(string name)
    {
        var n = name.TrimEnd();
        if (n.EndsWith("(Clone)", StringComparison.Ordinal))
            n = n.Substring(0, n.Length - "(Clone)".Length).TrimEnd();
        if (n.EndsWith(")", StringComparison.Ordinal))
        {
            var cut = n.Length - 1;              // Unity duplicate suffix " (2)"
            while (cut > 0 && char.IsDigit(n[cut - 1])) cut--;
            if (cut >= 2 && n[cut - 1] == '(' && n[cut - 2] == ' ')
                n = n.Substring(0, cut - 2).TrimEnd();
        }
        return n;
    }
}
}
