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
//   0. Config Pawn.ComponentType, when set: the object carrying that game
//      component (a comma-separated list is tried in order; nearest the
//      camera when several exist). No fallback — an explicit choice
//   1. Configured GameObject tag (default "Player")
//   2. The main camera's transform root (flatscreen: camera sits on the
//      player); Camera.main, else the enabled camera with the highest depth
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
    internal ConfigEntry<Ogs.Telemetry.Schema.ObjectType> _objectType = null!;
    internal ConfigEntry<string> _instanceLabel = null!;
    internal ConfigEntry<string> _pawnTag = null!;
    internal ConfigEntry<bool> _useCameraRoot = null!;
    internal ConfigEntry<bool> _dynamics = null!;
    internal ConfigEntry<bool> _pauseDetect = null!;
    internal ConfigEntry<bool> _gravityFromPhysics = null!;
    internal ConfigEntry<string> _rotationSource = null!;
    internal ConfigEntry<string> _pawnComponentTypeName = null!, _pawnPick = null!;
    internal ConfigEntry<bool> _survey = null!, _autoDiscover = null!;
    internal ConfigEntry<string> _learnedType = null!;
    internal ConfigEntry<float> _hEnterCameraDeg = null!, _hStillBodyDeg = null!, _hStillBodyRatio = null!,
                                _hExitBodyDeg = null!, _hExitBodyRatio = null!, _hDecaySeconds = null!,
                                _hMinDwellSeconds = null!, _hBlendSeconds = null!;

    public override void Load()
    {
        Instance = this;
        var preset = GamePresets.For(GameName());
        if (preset != null)
            Log.LogInfo("[ogs] built-in preset for '" + GameName() + "' seeds config defaults (ComponentType '" +
                        preset.ComponentType + "', RotationSource " + preset.RotationSource + ")");

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
        _objectType = Config.Bind("Telemetry", "ObjectType", Ogs.Telemetry.Schema.ObjectType.Vehicle,
                                  "What the tracked object is (Vehicle, Aircraft, Watercraft, " +
                                  "Spacecraft, Humanoid, Camera, Creature, Other). Receivers " +
                                  "pick a cueing profile by it");
        _pawnTag = Config.Bind("Pawn", "Tag", "Player",
                               "GameObject tag that marks the player pawn");
        _useCameraRoot = Config.Bind("Pawn", "CameraRootFallback", true,
                                     "If the tag finds nothing, use the main " +
                             "camera's root transform (flatscreen pawn proxy)");
        _pawnComponentTypeName = Config.Bind("Pawn", "ComponentType", preset?.ComponentType ?? "",
            "Track the object carrying a game component instead of using the tag / " +
            "camera lookup. Type names from the game's code, simple (Car) or full " +
            "(car.Car); a comma-separated list is tried in order, the first with a " +
            "live instance wins. Empty = off. Use when the game has no Player tag " +
            "and no MainCamera-tagged camera");
        _pawnPick = Config.Bind("Pawn", "Pick", "NearestCamera",
            new ConfigDescription("Which instance ComponentType tracks when several exist: " +
                "NearestCamera (the one the camera follows, i.e. the local player) or First",
                new AcceptableValueList<string>("NearestCamera", "First")));
        _survey = Config.Bind("Pawn", "Survey", false,
            "Log what surrounds the camera (cameras, and the nearest objects with " +
            "their components) once per pawn binding and every 60 s while no pawn is " +
            "found. Use it to pick Pawn.ComponentType or Pawn.Tag for a new game");
        _autoDiscover = Config.Bind("Pawn", "AutoDiscover", true,
            "When the tag lookup finds nothing, observe the objects around the camera " +
            "for a few seconds of gameplay and track the one the camera follows (moves " +
            "with it at a steady distance). No game knowledge needed; the camera must " +
            "travel to give a result. Until then the camera root is tracked, as without " +
            "this option, and the discovered body replaces it");
        _learnedType = Config.Bind("Pawn", "LearnedComponentType", "",
            "Written by the plugin: the game component type AutoDiscover found on the " +
            "player's body once it proved it moves, tried first next time (instant, no " +
            "observation). Never overwrites ComponentType. Clear it to forget");
        _rotationSource = Config.Bind("Pawn", "RotationSource", preset?.RotationSource ?? "Pawn",
                                      "Where the published rotation comes from. " +
                                      "Pawn = the pawn's own rotation. CameraYaw = " +
                                      "the camera's heading plus the pawn's tilt. " +
                                      "Camera = the camera's full rotation. Auto = " +
                                      "Pawn while the body follows the view, " +
                                      "CameraYaw while it stays put as the camera " +
                                      "turns (on foot). Use CameraYaw or Auto for " +
                                      "games whose on-foot body does not turn " +
                                      "with the view. Takes effect when the pawn " +
                                      "is (re)resolved");
        // Auto-mode thresholds (Pawn.RotationSource = Auto). Defaults suit
        // on-foot Subnautica; raise EnterCameraDeg when head-look in a
        // game's vehicles trips the switch. Take effect when the pawn is
        // (re)resolved
        var d = new HeadingTuning();
        _hEnterCameraDeg = Config.Bind("Heading", "EnterCameraDeg", d.EnterCameraDeg,
            new ConfigDescription("Degrees the camera must turn (within DecaySeconds) while the body stays " +
                                  "still before the heading follows the camera. Raise it to ignore head-look",
                                  new AcceptableValueRange<float>(1f, 360f)));
        _hStillBodyDeg = Config.Bind("Heading", "StillBodyDeg", d.StillBodyDeg,
            new ConfigDescription("The body counts as still while it turns less than this many degrees",
                                  new AcceptableValueRange<float>(0f, 90f)));
        _hStillBodyRatio = Config.Bind("Heading", "StillBodyRatio", d.StillBodyRatio,
            new ConfigDescription("... and less than this share of the camera's turn (0-1)",
                                  new AcceptableValueRange<float>(0f, 1f)));
        _hExitBodyDeg = Config.Bind("Heading", "ExitBodyDeg", d.ExitBodyDeg,
            new ConfigDescription("Degrees the body must turn to hand the heading back to the pawn",
                                  new AcceptableValueRange<float>(1f, 360f)));
        _hExitBodyRatio = Config.Bind("Heading", "ExitBodyRatio", d.ExitBodyRatio,
            new ConfigDescription("... as at least this share of the camera's turn (0-1)",
                                  new AcceptableValueRange<float>(0f, 1f)));
        _hDecaySeconds = Config.Bind("Heading", "DecaySeconds", d.DecaySeconds,
            new ConfigDescription("Memory of the turn measurement, in seconds",
                                  new AcceptableValueRange<float>(0.1f, 30f)));
        _hMinDwellSeconds = Config.Bind("Heading", "MinDwellSeconds", d.MinDwellSeconds,
            new ConfigDescription("Minimum seconds between heading-source switches",
                                  new AcceptableValueRange<float>(0f, 30f)));
        _hBlendSeconds = Config.Bind("Heading", "BlendSeconds", d.BlendSeconds,
            new ConfigDescription("Seconds over which the heading eases across a switch (no step)",
                                  new AcceptableValueRange<float>(0.05f, 10f)));
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
    float _lastNoPawnWarn = -100f;
    float _pawnPickedAt;
    System.Numerics.Vector3 _pawnPickPos;
    bool _poseChecked = true;
    System.Collections.Generic.List<Type> _pawnTypes = null!;
    int _pickedTypeIndex = -1;
    float _lastUpgradeCheck;
    float _nextSurveyAt = 20f;
    string _lastCounts = "";
    // ---- Pawn.AutoDiscover (see Shared/PawnDiscovery.cs) ----
    PawnDiscovery _disc = null!;
    System.Collections.Generic.List<Transform> _discCands = new System.Collections.Generic.List<Transform>();
    System.Collections.Generic.HashSet<int> _discRejected = new System.Collections.Generic.HashSet<int>();
    Transform _discCamera = null!;
    Transform _discResult = null!;
    float _discStarted, _discNextSample, _discNextEval;
    int _discLastBest = -1;
    bool _discGaveUp, _tagMissed, _pawnAuto, _pawnLearned, _pawnViaTag, _pawnProvisional, _provisionalPick;
    int _tagCount;
    float _lastTagCheck;
    string _sessionLearned = "", _discLearnName = "", _pawnTypesKey = null!;
    System.Numerics.Vector3 _pickCamPos;
    bool _wasPaused;
    float _lastGravityPoll = -1f;
    Func<System.Numerics.Vector3?>? _readGravity;
    HeadingSelector _heading = new HeadingSelector(RotationSource.Pawn);
    Transform _cam = null!;
    float _lastCamResolve = -1f;
    bool _headingOnCamera;
    System.Numerics.Quaternion _rot = System.Numerics.Quaternion.Identity;

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
        _publisher.DeclareObject(p._objectName.Value, "", p._objectType.Value);
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

        if ((_pawn == null || _pawnProvisional) && OgsTelemetryPlugin.Instance._autoDiscover.Value && _tagMissed && !_discGaveUp && _discResult == null)
            StepDiscovery();
        if (_pawn == null && Time.time - _lastResolveAttempt > 1f)
        {
            _lastResolveAttempt = Time.time;
            _pawn = ResolvePawn();
            if (_pawn != null) BindPawn();
        }
        else if (_pawn != null && _pawnProvisional && _discResult != null)
        {
            var found = _discResult;
            _discResult = null!;
            _pawn = found;
            _pawnAuto = true;
            OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] auto-discovered pawn '" + found.name + "' replaces the provisional camera root");
            BindPawn();
        }
        else if (_pawn != null && _pickedTypeIndex > 0 &&
                 Time.realtimeSinceStartup - _lastUpgradeCheck > 2f)
        {
            // a lower-priority Pawn.ComponentType was picked (e.g. the menu
            // garage car): switch as soon as a higher-priority one exists
            _lastUpgradeCheck = Time.realtimeSinceStartup;
            try
            {
                var current = _pawn;
                var better = TryPick(_pickedTypeIndex);
                if (better != null && better != current)
                {
                    _pawn = better;
                    OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] switched to higher-priority pawn '" + better.name + "'");
                    BindPawn();
                }
            }
            catch { /* transient scene teardown: try again next time */ }
        }
        else if (_pawn != null && _pawnViaTag && Time.realtimeSinceStartup - _lastTagCheck > 2f)
        {
            // several tagged objects: follow the one that has become clearly
            // nearer the camera (e.g. the local car once the race starts)
            _lastTagCheck = Time.realtimeSinceStartup;
            try
            {
                var near = PickTagged(OgsTelemetryPlugin.Instance._pawnTag.Value, false);
                if (_tagCount > 1 && near != null && near != _pawn && ClearlyNearer(near, _pawn))
                {
                    _pawn = near;
                    OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] switched to '" + near.name + "', now the nearest the camera of " +
                           _tagCount + " tagged objects");
                    BindPawn();
                }
            }
            catch { /* transient scene teardown: try again next time */ }
        }
        // Pawn.Survey: log what surrounds the camera, once per pawn binding
        // and every 60 s while no pawn is found
        if (OgsTelemetryPlugin.Instance._survey.Value && Time.realtimeSinceStartup >= _nextSurveyAt)
        {
            _nextSurveyAt = _pawn == null ? Time.realtimeSinceStartup + 60f : float.MaxValue;
            RunSurvey();
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
        // one rotation per frame, shared by the deriver and the publisher
        // (the heading selector is stateful — it must see each frame once)
        if (_pawn != null && !paused) _rot = ReadRotation();
        if (_pawn != null && !paused)
        {
            if (_wasPaused)
                _deriver.Reset(SafePos(), _rot, (double)Time.time);
            else
                _deriver.Sample((double)Time.time, SafePos(), _rot);
        }
        // one-time sanity check: a pawn that never moves is usually the wrong
        // object (a logical player root sitting at the origin)
        if (!_poseChecked && _pawn != null && !paused &&
            Time.realtimeSinceStartup - _pawnPickedAt > 10f)
        {
            _poseChecked = true;
            var now = SafePos();
            PersistLearnedIfMoving(now);
            if (now == _pawnPickPos && _pawnAuto && CameraTravelled())
            {
                if (_pawnLearned)
                {
                    _sessionLearned = "";
                    OgsTelemetryPlugin.Instance._learnedType.Value = "";
                    OgsTelemetryPlugin.Instance.Log.LogWarning("[ogs] learned pawn type did not move while the camera did; forgotten");
                }
                OgsTelemetryPlugin.Instance.Log.LogWarning("[ogs] auto-picked pawn '" + _pawn.name + "' did not move while the camera did; " +
                       "rejected, discovering again");
                _discRejected.Add(_pawn.GetInstanceID());
                _pawn = null!;
                _tagMissed = true;
                _discGaveUp = false;
                _disc = null!;
            }
            else
            if (now == _pawnPickPos)
                OgsTelemetryPlugin.Instance.Log.LogWarning("[ogs] pawn '" + _pawn.name + "' has not moved in 10 s (pos " +
                    now.X.ToString("F1") + ", " + now.Y.ToString("F1") + ", " + now.Z.ToString("F1") +
                    "). Fine if you are idle in a menu; if you are driving, it is the wrong " +
                    "object: try another Pawn.ComponentType");
            else
                OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] pawn '" + _pawn.name + "' is moving (pos " +
                    now.X.ToString("F1") + ", " + now.Y.ToString("F1") + ", " + now.Z.ToString("F1") + ")");
        }
        _wasPaused = paused;
        pub.Tick((double)Time.realtimeSinceStartup);
    }

    // bind the publisher to the (new) pawn: pose, dynamics, label, sanity check
    void BindPawn()
    {
        var pub = _publisher;
        var p = OgsTelemetryPlugin.Instance;
        _heading = new HeadingSelector(ParseRotationSource(p), ReadTuning(p));
        _headingOnCamera = false;
        _rot = ReadRotation();
        pub.BindPose(() => SafePos(), () => _rot);
        _pawnPickedAt = Time.realtimeSinceStartup;
        _pawnPickPos = SafePos();
        _poseChecked = false;
        OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] pawn '" + _pawn.name + "' pose at pick: pos (" +
            _pawnPickPos.X.ToString("F1") + ", " + _pawnPickPos.Y.ToString("F1") + ", " +
            _pawnPickPos.Z.ToString("F1") + ")");
        if (p._dynamics.Value)
            pub.BindBodyDynamics(() =>
                (_deriver.SpecificForce, _deriver.AngularVelocity));
        _deriver = new BodyDynamicsDeriver(); // fresh state per pawn
        if (string.IsNullOrEmpty(p._typeLabel.Value))
            pub.SetObjectTypeLabel(CleanPawnName(_pawn.name));
        _lastNoPawnWarn = -100f;
        _nextSurveyAt = Time.realtimeSinceStartup + 10f;
        _pawnProvisional = _provisionalPick;
        _provisionalPick = false;
        if (!_pawnProvisional)       // a provisional bind leaves discovery running
        {
            _disc = null!; _discResult = null!; _discGaveUp = false; _tagMissed = false; _discLastBest = -1;
        }
        try
        {
            var bc = BestCamera();
            var bp = bc == null ? default : bc.transform.position;
            _pickCamPos = new System.Numerics.Vector3(bp.x, bp.y, bp.z);
        }
        catch { _pickCamPos = default; }
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

    // The rotation to publish: the pawn's, or the camera's heading, per
    // Pawn.RotationSource (see HeadingSelector). The camera is only looked
    // up when the mode needs it.
    System.Numerics.Quaternion ReadRotation()
    {
        var pawnRot = SafeRot();
        if (_heading.Mode == RotationSource.Pawn) return pawnRot;
        var r = _heading.Resolve((double)Time.time, pawnRot, SafeCameraRot());
        if (_heading.UsingCamera != _headingOnCamera)
        {
            _headingOnCamera = _heading.UsingCamera;
            OgsTelemetryPlugin.Instance.Log.LogInfo(
                "[ogs] heading source: " + (_headingOnCamera ? "camera" : "pawn") +
                " (" + _heading.Mode + ")");
        }
        return r;
    }

    System.Numerics.Quaternion? SafeCameraRot()
    {
        try
        {
            if (_cam == null)
            {
                if (Time.realtimeSinceStartup - _lastCamResolve < 1f) return null;
                _lastCamResolve = Time.realtimeSinceStartup;
                var c = BestCamera();
                if (c == null) return null;
                _cam = c.transform;
            }
            var r = _cam.rotation;
            if (r.x == 0f && r.y == 0f && r.z == 0f && r.w == 0f) return null;
            return new System.Numerics.Quaternion(r.x, r.y, r.z, r.w);
        }
        catch { _cam = null!; return null; }
    }

    static HeadingTuning ReadTuning(OgsTelemetryPlugin p) => new HeadingTuning
    {
        EnterCameraDeg = p._hEnterCameraDeg.Value,
        StillBodyDeg = p._hStillBodyDeg.Value,
        StillBodyRatio = p._hStillBodyRatio.Value,
        ExitBodyDeg = p._hExitBodyDeg.Value,
        ExitBodyRatio = p._hExitBodyRatio.Value,
        DecaySeconds = p._hDecaySeconds.Value,
        MinDwellSeconds = p._hMinDwellSeconds.Value,
        BlendSeconds = p._hBlendSeconds.Value,
    };

    static RotationSource ParseRotationSource(OgsTelemetryPlugin p)
    {
        if (HeadingSelector.TryParse(p._rotationSource.Value, out var mode)) return mode;
        p.Log.LogWarning("[ogs] Pawn.RotationSource '" + p._rotationSource.Value +
                         "' is not one of Pawn, CameraYaw, Camera, Auto; using Pawn");
        return RotationSource.Pawn;
    }

    Transform ResolvePawn()
    {
        _pawnAuto = false;
        _pawnLearned = false;
        _pawnViaTag = false;
        var p = OgsTelemetryPlugin.Instance;
        // an explicit component type is a deliberate choice: don't fall
        // back to something else (a camera root would silently track the
        // wrong object)
        var typeName = (p._pawnComponentTypeName.Value ?? "").Trim();
        if (typeName.Length > 0) return ResolveByComponent(typeName);
        _pickedTypeIndex = -1;
        _pawnAuto = false;

        try
        {
            var byTag = PickTagged(p._pawnTag.Value, true);
            if (byTag != null) { _pawnViaTag = true; return byTag; }
        }
        catch { /* tag not defined in this game */ }

        // a type learned by AutoDiscover (earlier this session, or saved by a
        // previous launch): instant, and a miss just falls through to discovery
        if (OgsTelemetryPlugin.Instance._autoDiscover.Value && _discResult == null)
        {
            var learned = LearnedType();
            if (learned.Length > 0)
            {
                var viaLearned = ResolveByComponent(learned, true);
                if (viaLearned != null)
                {
                    _pawnAuto = true;
                    _pawnLearned = true;
                    return viaLearned;
                }
            }
        }

        if (OgsTelemetryPlugin.Instance._autoDiscover.Value && !_discGaveUp)
        {
            _tagMissed = true;                       // lets StepDiscovery run every frame
            if (_discResult != null)
            {
                var found = _discResult;
                _discResult = null!;
                _pawnAuto = true;
                return found;
            }
            // still observing: track the camera root meanwhile (what happens without
            // AutoDiscover), so a game where it was already the right body loses
            // nothing; the discovered body replaces it
            if (p._useCameraRoot.Value)
            {
                var provisional = BestCamera();
                if (provisional != null) { _provisionalPick = true; return provisional.transform.root; }
            }
            return null!;
        }

        if (p._useCameraRoot.Value)
        {
            var cam = BestCamera();
            if (cam != null) return cam.transform.root;
        }

        WarnNoPawn("tag '" + p._pawnTag.Value + "' not found and no camera; " +
                      "set Pawn.Tag, or Pawn.ComponentType to a component on the player object");
        return null!;
    }

    // Camera.main needs the MainCamera tag; fall back to the enabled camera
    // with the highest depth (games with their own camera rigs often skip the tag)
    static Camera BestCamera()
    {
        try
        {
            var main = Camera.main;
            if (main != null) return main;
            Camera best = null!;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || !c.enabled) continue;
                if (best == null || c.depth > best.depth) best = c;
            }
            return best;
        }
        catch { return null!; }
    }

    // Pawn.ComponentType is a comma-separated list of type names in priority
    // order, tried each attempt; the first with a live instance wins (several
    // instances: the one nearest the camera, or the first, per Pawn.Pick)
    Transform ResolveByComponent(string typeNames, bool quiet = false)
    {
        try
        {
            if (_pawnTypes == null || _pawnTypesKey != typeNames)
            {
                _pawnTypesKey = typeNames;
                _pawnTypes = new System.Collections.Generic.List<Type>();
                foreach (var raw in typeNames.Split(','))
                {
                    var name = raw.Trim();
                    if (name.Length == 0) continue;
                    Type match = null!;
                    foreach (var t in TypeLookup.FindAll(name))
                        if (typeof(Component).IsAssignableFrom(t)) { match = t; break; }
                    if (match == null)
                    {
                        if (!quiet) OgsTelemetryPlugin.Instance.Log.LogWarning("[ogs] Pawn.ComponentType '" + name + "' matches no Component type in the game's assemblies");
                    }
                    else
                    {
                        _pawnTypes.Add(match);
                        OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] pawn component type: " + match.FullName);
                    }
                }
            }
            if (_pawnTypes.Count == 0)
            {
                if (!quiet) WarnNoPawn("none of the Pawn.ComponentType names matches a Component type");
                return null!;
            }
            var best = TryPick(_pawnTypes.Count);
            if (best == null && !quiet) WarnNoPawn("no active instance yet (" + _lastCounts + ")");
            return best;
        }
        catch (Exception e)
        {
            if (!quiet) WarnNoPawn("component lookup failed: " + e.Message);
            return null!;
        }
    }

    // Best instance among the first `limit` types of the priority list; on
    // success records which type won (_pickedTypeIndex) so a lower-priority
    // pick can be upgraded later
    Transform TryPick(int limit)
    {
        var cam = OgsTelemetryPlugin.Instance._pawnPick.Value == "First" ? null! : BestCamera();
        var counts = "";
        for (int i = 0; i < limit && i < _pawnTypes.Count; i++)
        {
            var type = _pawnTypes[i];
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(type));
            Transform best = null!;
            float bestD = float.MaxValue;
            int n = 0;
            foreach (var o in found)
            {
                var c = o?.TryCast<Component>();
                if (c == null) continue;
                n++;
                var t = c.transform;
                if (cam == null) { if (best == null) best = t; continue; }
                var d = SqrDistance(t, cam.transform);
                if (d < bestD) { bestD = d; best = t; }
            }
            counts += type.Name + ":" + n + " ";
            if (best != null)
            {
                _pickedTypeIndex = i;
                OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] pawn: '" + best.name + "' via " + type.FullName +
                       " (" + n + " in scene, pick " + OgsTelemetryPlugin.Instance._pawnPick.Value + ")");
                return best;
            }
        }
        _lastCounts = counts.TrimEnd();
        return null!;
    }

    static float SqrDistance(Transform a, Transform b)
    {
        var p = a.position; var q = b.position;
        float dx = p.x - q.x, dy = p.y - q.y, dz = p.z - q.z;
        return dx * dx + dy * dy + dz * dz;
    }

    // ---- Pawn.AutoDiscover ----------------------------------------------
    // Observe the objects around the camera for a few seconds of gameplay and
    // pick the one the camera follows (PawnDiscovery has the scoring). Runs
    // every frame while no pawn is bound and the tag lookup missed.
    void StepDiscovery()
    {
        float now = Time.realtimeSinceStartup;
        if (_disc == null && !StartDiscovery(now)) return;
        if (now < _discNextSample) return;
        _discNextSample = now + 0.1f;
        if (_discCamera == null) { _disc = null!; return; }   // camera destroyed: start over

        var cp = _discCamera.position;
        var cands = new System.Numerics.Vector3[_discCands.Count];
        for (int i = 0; i < cands.Length; i++)
        {
            try { var p = _discCands[i].position; cands[i] = new System.Numerics.Vector3(p.x, p.y, p.z); }
            catch { cands[i] = new System.Numerics.Vector3(float.NaN, float.NaN, float.NaN); }
        }
        _disc.AddSample(new System.Numerics.Vector3(cp.x, cp.y, cp.z), cands);
        if (now < _discNextEval) return;
        _discNextEval = now + 0.5f;

        int best = _disc.Evaluate(out var score);
        if (best >= 0 && best == _discLastBest)          // same answer twice running
        {
            _discResult = _discCands[best];
            _discLearnName = PickLearnableType(_discResult);
            OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] auto-discovered pawn '" + _discResult.name + "' (score " +
                   score.ToString("F3") + ", " + _discCands.Count + " candidates). Components: " +
                   ComponentNames(_discResult) + ". To pin it, set Pawn.ComponentType to one of " +
                   "its game component types");
            if (_discLearnName.Length > 0)
                OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] pawn type '" + _discLearnName + "' will be remembered (this session and " +
                       "Pawn.LearnedComponentType) once it proves it moves");
            _disc = null!;
            return;
        }
        _discLastBest = best;
        if (now - _discStarted > 45f)
        {
            _discGaveUp = true;
            _disc = null!;
            OgsTelemetryPlugin.Instance.Log.LogWarning("[ogs] auto-discovery found nothing the camera follows in 45 s " +
                   "(is the camera moving?); falling back to the camera root");
        }
    }

    bool StartDiscovery(float now)
    {
        var cam = BestCamera();
        if (cam == null) return false;
        var camRoot = cam.transform.root;
        var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<float, Transform>>();
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Transform>()))
        {
            var t = o?.TryCast<Transform>();
            if (t == null || t.root == camRoot || _discRejected.Contains(t.GetInstanceID())) continue;
            { var sd = SqrDistance(t, cam.transform); if (!float.IsNaN(sd)) list.Add(new System.Collections.Generic.KeyValuePair<float, Transform>(sd, t)); }
        }
        if (list.Count == 0) return false;
        list.Sort((a, b) => a.Key.CompareTo(b.Key));

        // the 200 nearest, plus the ancestors of the 60 nearest (the body is
        // often a parent of the near child, e.g. the chassis above a wheel)
        _discCands.Clear();
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < list.Count && _discCands.Count < 200; i++)
            if (seen.Add(list[i].Value.GetInstanceID())) _discCands.Add(list[i].Value);
        for (int i = 0; i < list.Count && i < 60; i++)
        {
            var anc = list[i].Value.parent;
            for (int hops = 0; anc != null && hops < 32 && _discCands.Count < 400; hops++, anc = anc.parent)
            {
                if (anc.root == camRoot) break;
                if (_discRejected.Contains(anc.GetInstanceID())) continue;
                if (seen.Add(anc.GetInstanceID())) _discCands.Add(anc);
            }
        }
        var depths = new int[_discCands.Count];
        var parents = new int[_discCands.Count];
        var body = new bool[_discCands.Count];
        var index = new System.Collections.Generic.Dictionary<int, int>();
        for (int i = 0; i < _discCands.Count; i++) index[_discCands[i].GetInstanceID()] = i;
        for (int i = 0; i < depths.Length; i++)
        {
            int d = 0;
            var x = _discCands[i];
            while (x.parent != null && d < 32) { x = x.parent; d++; }
            depths[i] = d;
            parents[i] = -1;
            var par = _discCands[i].parent;
            if (par != null && index.TryGetValue(par.GetInstanceID(), out var pi)) parents[i] = pi;
            // a physics body (the chassis) beats the wheels and meshes that ride on it
            var names = ComponentNames(_discCands[i]);
            body[i] = names.Contains("Rigidbody") || names.Contains("CharacterController");
        }
        _disc = new PawnDiscovery(depths, parents, body);
        _discCamera = cam.transform;
        _discStarted = now;
        _discNextSample = now;
        _discNextEval = now + 1f;
        _discLastBest = -1;
        OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] auto-discovery: observing " + _discCands.Count +
               " objects around camera '" + cam.name + "'; the camera must move to give a result");
        return true;
    }

    // true when the camera moved at least one world unit since the pawn was picked
    bool CameraTravelled()
    {
        try
        {
            var bc = BestCamera();
            if (bc == null) return false;
            var p = bc.transform.position;
            var now = new System.Numerics.Vector3(p.x, p.y, p.z);
            return System.Numerics.Vector3.Distance(now, _pickCamPos) >= 1f;
        }
        catch { return false; }
    }

    static string ComponentNames(Transform t)
    {
        var names = "";
        try
        {
            foreach (var comp in t.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<Component>()))
            {
                if (comp == null) continue;
                var n = ComponentTypeName(comp);
                if (n != "Transform") names += n + " ";
            }
        }
        catch { }
        return names.TrimEnd();
    }

    // The most specific game component on the discovered body: engine types
    // are skipped, and of the rest the one with the fewest live instances
    // wins (a broad component that sits on many objects would match the
    // wrong thing next time). "" when nothing suitable.
    string PickLearnableType(Transform t)
    {
        try
        {
            string best = "";
            int bestCount = int.MaxValue;
            foreach (var comp in t.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<Component>()))
            {
                if (comp == null) continue;
                string full;
                try { full = comp.GetIl2CppType().FullName; } catch { continue; }
                if (full.StartsWith("UnityEngine.") || full.StartsWith("Unity.") || full.StartsWith("System.") ||
                    full.StartsWith("Il2Cpp")) continue;
                Type match = null!;
                foreach (var cand in TypeLookup.FindAll(full))
                    if (typeof(Component).IsAssignableFrom(cand)) { match = cand; break; }
                if (match == null) continue;
                int count = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(match)).Length;
                if (count > 0 && count < bestCount) { best = full; bestCount = count; }
            }
            return best;
        }
        catch { return ""; }
    }

    string LearnedType() =>
        _sessionLearned.Length > 0 ? _sessionLearned : (OgsTelemetryPlugin.Instance._learnedType.Value ?? "").Trim();

    // once an auto-picked body has proven it moves, save its type for the next launch
    void PersistLearnedIfMoving(System.Numerics.Vector3 now)
    {
        var p = OgsTelemetryPlugin.Instance;
        if (_pawn == null || !_pawnAuto || _pawnLearned || _discLearnName.Length == 0) return;
        if (now == _pawnPickPos) return;                       // not proven yet
        _sessionLearned = _discLearnName;                      // next scene / vehicle, this session
        if ((p._learnedType.Value ?? "") == _discLearnName) return;
        p._learnedType.Value = _discLearnName;
        OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] learned pawn type '" + _discLearnName + "' (saved to Pawn.LearnedComponentType; " +
               "clear it to forget)");
    }

    // Among the objects carrying Pawn.Tag, the one nearest the camera: several
    // tagged objects can exist (other cars, ghosts, menu showcases) and
    // FindWithTag returns whichever comes first, while the camera follows the
    // local player. With one tagged object, or no camera, this is the old
    // behaviour. `announce` logs the choice when several were considered.
    Transform PickTagged(string tag, bool announce)
    {
        var tagged = GameObject.FindGameObjectsWithTag(tag);
        _tagCount = tagged.Length;
        if (tagged.Length == 0) return null!;
        var cam = BestCamera();
        Transform best = null!;
        float bestD = float.MaxValue;
        foreach (var go in tagged)
        {
            if (go == null) continue;
            var t = go.transform;
            if (cam == null) return t;
            var d = SqrDistance(t, cam.transform);
            if (!float.IsNaN(d) && d < bestD) { bestD = d; best = t; }
        }
        if (best == null) best = tagged[0].transform;
        if (announce && tagged.Length > 1)
            OgsTelemetryPlugin.Instance.Log.LogInfo("[ogs] pawn: '" + best.name + "' is the nearest the camera of " +
                   tagged.Length + " '" + tag + "'-tagged objects");
        return best;
    }

    // true when `a` is clearly nearer the camera than `b` (half the distance),
    // so a re-check doesn't flip between two similarly distant objects
    bool ClearlyNearer(Transform a, Transform b)
    {
        var cam = BestCamera();
        if (cam == null) return false;
        return SqrDistance(a, cam.transform) < 0.25f * SqrDistance(b, cam.transform);
    }

    // Pawn.Survey: log what surrounds the camera, to choose Pawn.ComponentType
    // or Pawn.Tag for a game the generic lookup misses
    void RunSurvey()
    {
        var log = OgsTelemetryPlugin.Instance.Log;
        try
        {
            var cam = BestCamera();
            var main = Camera.main;
            log.LogInfo("[ogs] survey: pawn " + (_pawn == null ? "unresolved" : "'" + _pawn.name + "'") +
                ", Camera.main " + (main == null ? "none" : "'" + main.name + "'") +
                ", chosen camera " + (cam == null ? "none" : "'" + cam.name + "' depth " + cam.depth));
            foreach (var c in Camera.allCameras)
                if (c != null)
                    log.LogInfo("[ogs] survey: camera '" + c.name + "' tag '" + c.tag + "' depth " +
                        c.depth + " enabled " + c.enabled + " root '" + c.transform.root.name + "'");
            if (cam == null) return;
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<float, Transform>>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Transform>()))
            {
                var t = o?.TryCast<Transform>();
                if (t == null || t.root == cam.transform.root) continue;
                { var sd = SqrDistance(t, cam.transform); if (!float.IsNaN(sd)) list.Add(new System.Collections.Generic.KeyValuePair<float, Transform>(sd, t)); }
            }
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            log.LogInfo("[ogs] survey: " + list.Count + " active transforms; nearest the camera:");
            for (int i = 0; i < list.Count && i < 15; i++)
            {
                var t = list[i].Value;
                var names = "";
                foreach (var comp in t.GetComponents(Il2CppInterop.Runtime.Il2CppType.Of<Component>()))
                {
                    if (comp == null) continue;
                    var n = ComponentTypeName(comp);
                    if (n != "Transform") names += n + " ";
                }
                log.LogInfo("[ogs] survey:   " + Math.Sqrt(list[i].Key).ToString("F1") + " m  '" +
                    t.name + "' (root '" + t.root.name + "')  " + names.TrimEnd());
            }
        }
        catch (Exception e) { log.LogWarning("[ogs] survey failed: " + e.Message); }
    }

    // the engine-side class name of a component proxy
    static string ComponentTypeName(Component c)
    {
        try { return c.GetIl2CppType().Name; }
        catch { return c.GetType().Name; }
    }

    // at most one line per 30 s while the pawn is unresolved
    void WarnNoPawn(string detail)
    {
        if (Time.realtimeSinceStartup - _lastNoPawnWarn < 30f) return;
        _lastNoPawnWarn = Time.realtimeSinceStartup;
        OgsTelemetryPlugin.Instance.Log.LogWarning("[ogs] no pawn yet (" + detail + "); retrying every second");
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
