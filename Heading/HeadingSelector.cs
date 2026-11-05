// HeadingSelector.cs — which rotation the plugin publishes: the pawn's, or
// the camera's heading. Engine-agnostic (System.Numerics only), compiled
// into both plugin flavors so Mono and IL2CPP cannot drift apart.
//
// Why it exists: the pawn is the controlled body (KB ruling
// C-20261010-MBP-08), but in some games the body does not turn with the
// view. On foot in Subnautica the player root stays at identity while the
// camera rig turns, so the published heading never changed. Quaternion
// components are the same in Unity and System.Numerics, and the Hamilton
// product `a * b` (apply b, then a) matches Unity's, so the compositions
// below are valid for Unity quaternions passed through unchanged.
//
// Modes (config Pawn.RotationSource):
//   Pawn       the pawn's rotation, untouched (default)
//   CameraYaw  camera heading + the pawn's own tilt (pawn rotation with its
//              yaw removed). Looking around steers the heading but does not
//              pitch or roll the rig
//   Camera     the camera's full rotation
//   Auto       Pawn while the body follows the view; CameraYaw while the
//              body stays put as the camera turns (on foot). Decided from
//              observed motion, so it needs no per-game knowledge
//
// Auto detection: yaw travel of body and camera is accumulated with an
// exponential decay (DecaySeconds). Camera travel with a still body means
// the body is decoupled from the view; the body moving along with the
// camera means it follows it (swimming, vehicles, first-person pawns).
// Hysteresis and a minimum dwell stop flicker, and a heading offset that
// decays away (BlendSeconds) keeps the published yaw continuous across a
// switch instead of stepping.
using System;
using System.Numerics;

namespace Ogs.BepInEx
{
public enum RotationSource { Pawn, CameraYaw, Camera, Auto }

/// <summary>Auto-mode thresholds (degrees / seconds). The defaults suit
/// on-foot Subnautica; raise <see cref="EnterCameraDeg"/> in games where
/// head-look in vehicles trips the switch.</summary>
public sealed class HeadingTuning
{
    public float EnterCameraDeg = 20f;     // camera travel needed to consider the body decoupled
    public float StillBodyDeg = 6f;        // ... while body travel stays under this
    public float StillBodyRatio = 0.2f;    // ... and under this share of the camera's
    public float ExitBodyDeg = 12f;        // body travel that says "it follows the view again"
    public float ExitBodyRatio = 0.5f;     // ... as a share of camera travel
    public float DecaySeconds = 1.5f;      // memory of the travel accumulators
    public float MinDwellSeconds = 1.0f;   // minimum time between switches
    public float BlendSeconds = 0.5f;      // decay of the continuity offset
}

public sealed class HeadingSelector
{
    readonly RotationSource _mode;
    readonly HeadingTuning _t;
    bool _haveLast;
    double _lastT;
    float _lastPawnYaw, _lastCamYaw, _lastOutYaw;
    float _pawnTravel, _camTravel;               // decayed, degrees
    double _lastSwitch = double.NegativeInfinity;
    float _offset;                               // radians, decays to 0

    public HeadingSelector(RotationSource mode, HeadingTuning? tuning = null)
    {
        _mode = mode;
        _t = tuning ?? new HeadingTuning();
    }

    public RotationSource Mode => _mode;

    /// <summary>True while the published heading is the camera's. For Auto
    /// this is the live decision; for the fixed modes it is constant.</summary>
    public bool UsingCamera { get; private set; }

    /// <summary>Resolve the rotation to publish. <paramref name="cam"/> is
    /// null when no camera is available (the pawn is published).</summary>
    public Quaternion Resolve(double t, Quaternion pawn, Quaternion? cam)
    {
        pawn = Normalize(pawn);
        if (_mode == RotationSource.Pawn || cam is null)
        {
            UsingCamera = false;
            _haveLast = false;      // no camera to compare against: start over when it returns
            _offset = 0f;
            return pawn;
        }

        var camQ = Normalize(cam.Value);
        float pawnYaw = YawOf(pawn, _haveLast ? _lastPawnYaw : 0f);
        float camYaw = YawOf(camQ, _haveLast ? _lastCamYaw : 0f);
        double dt = _haveLast ? Math.Max(0.0, Math.Min(0.25, t - _lastT)) : 0.0;

        bool wantCamera;
        switch (_mode)
        {
            case RotationSource.Camera:
            case RotationSource.CameraYaw:
                wantCamera = true;
                break;
            default: // Auto
                wantCamera = DecideAuto(t, dt, pawnYaw, camYaw);
                break;
        }

        Quaternion basis = _mode == RotationSource.Camera && wantCamera
            ? camQ
            : wantCamera ? Compose(camYaw, pawn, pawnYaw) : pawn;

        if (wantCamera != UsingCamera && _haveLast)
        {
            // continuity: start from where the last published heading was
            _offset = WrapPi(_lastOutYaw - YawOf(basis, _lastOutYaw));
            _lastSwitch = t;
        }
        UsingCamera = wantCamera;

        if (dt > 0) _offset *= (float)Math.Exp(-dt / Math.Max(0.01f, _t.BlendSeconds));
        var outQ = Math.Abs(_offset) > 1e-4f
            ? Normalize(YawQuat(_offset) * basis)
            : basis;

        _lastOutYaw = YawOf(outQ, _lastOutYaw);
        _lastPawnYaw = pawnYaw; _lastCamYaw = camYaw;
        _lastT = t; _haveLast = true;
        return outQ;
    }

    bool DecideAuto(double t, double dt, float pawnYaw, float camYaw)
    {
        if (_haveLast)
        {
            float decay = (float)Math.Exp(-dt / Math.Max(0.01f, _t.DecaySeconds));
            _pawnTravel = _pawnTravel * decay + Deg(Math.Abs(WrapPi(pawnYaw - _lastPawnYaw)));
            _camTravel = _camTravel * decay + Deg(Math.Abs(WrapPi(camYaw - _lastCamYaw)));
        }
        if (t - _lastSwitch < _t.MinDwellSeconds) return UsingCamera;

        if (!UsingCamera)
        {
            bool bodyStill = _pawnTravel < _t.StillBodyDeg && _pawnTravel < _t.StillBodyRatio * _camTravel;
            return _camTravel > _t.EnterCameraDeg && bodyStill;
        }
        bool bodyFollows = _pawnTravel > _t.ExitBodyDeg && _pawnTravel > _t.ExitBodyRatio * _camTravel;
        return !bodyFollows;
    }

    // camera heading + the pawn's tilt (its rotation with its own yaw removed)
    static Quaternion Compose(float camYaw, Quaternion pawn, float pawnYaw)
    {
        var tilt = YawQuat(-pawnYaw) * pawn;
        return Normalize(YawQuat(camYaw) * tilt);
    }

    // rotation about +Y by `yaw` radians (Unity's yaw sense: +Z turns toward +X)
    static Quaternion YawQuat(float yaw)
    {
        float h = yaw * 0.5f;
        return new Quaternion(0f, (float)Math.Sin(h), 0f, (float)Math.Cos(h));
    }

    // heading of the forward axis projected on the ground plane; holds the
    // previous value when looking (almost) straight up or down
    static float YawOf(Quaternion q, float fallback)
    {
        var f = Vector3.Transform(Vector3.UnitZ, q);
        if (f.X * f.X + f.Z * f.Z < 0.0025f) return fallback;
        return (float)Math.Atan2(f.X, f.Z);
    }

    static Quaternion Normalize(Quaternion q)
    {
        float n = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return n < 1e-6f ? Quaternion.Identity : new Quaternion(q.X / n, q.Y / n, q.Z / n, q.W / n);
    }

    static float WrapPi(float a)
    {
        const float TwoPi = (float)(Math.PI * 2);
        a %= TwoPi;
        if (a > (float)Math.PI) a -= TwoPi;
        else if (a < -(float)Math.PI) a += TwoPi;
        return a;
    }

    static float Deg(float rad) => rad * (180f / (float)Math.PI);

    public static bool TryParse(string? s, out RotationSource mode)
    {
        mode = RotationSource.Pawn;
        return !string.IsNullOrWhiteSpace(s) && Enum.TryParse(s!.Trim(), true, out mode)
               && Enum.IsDefined(typeof(RotationSource), mode);
    }
}
}
