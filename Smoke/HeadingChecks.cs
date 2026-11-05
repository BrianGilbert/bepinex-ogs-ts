// HeadingChecks.cs — drives HeadingSelector with synthetic body/camera
// motion at 60 Hz: the on-foot case (body still, camera turning), the
// swimming/vehicle case (body follows the view), the hand-over between
// them, and the fixed modes.
using System;
using System.Numerics;
using Ogs.BepInEx;

static class HeadingChecks
{
    static int _fail;

    static Quaternion Yaw(double deg) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(deg * Math.PI / 180));
    static Quaternion Pitch(double deg) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(deg * Math.PI / 180));

    static double HeadingDeg(Quaternion q)
    {
        var f = Vector3.Transform(Vector3.UnitZ, q);
        return Math.Atan2(f.X, f.Z) * 180 / Math.PI;
    }

    static double Wrap(double d) { d %= 360; return d > 180 ? d - 360 : d < -180 ? d + 360 : d; }

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} heading: {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _fail++;
    }

    public static bool Run()
    {
        const double dt = 1.0 / 60.0;

        // Pawn mode: the pawn rotation, untouched
        {
            var sel = new HeadingSelector(RotationSource.Pawn);
            var pawn = Yaw(30) * Pitch(10);
            var r = sel.Resolve(0, pawn, Yaw(120));
            Check("Pawn mode returns the pawn rotation",
                  Math.Abs(Quaternion.Dot(r, pawn)) > 0.9999f && !sel.UsingCamera);
        }

        // Camera mode: the camera's full rotation
        {
            var sel = new HeadingSelector(RotationSource.Camera);
            var cam = Yaw(75) * Pitch(-20);
            var r = sel.Resolve(0, Quaternion.Identity, cam);
            Check("Camera mode returns the camera rotation", Math.Abs(Quaternion.Dot(r, cam)) > 0.9999f);
        }

        // CameraYaw: camera heading, the pawn's own tilt kept
        {
            var sel = new HeadingSelector(RotationSource.CameraYaw);
            var pawn = Yaw(10) * Pitch(20);
            var r = sel.Resolve(0, pawn, Yaw(90));
            double heading = HeadingDeg(Yaw(90) * Pitch(20));
            var fp = Vector3.Transform(Vector3.UnitZ, pawn);
            var fr = Vector3.Transform(Vector3.UnitZ, r);
            Check("CameraYaw takes the camera heading", Math.Abs(Wrap(HeadingDeg(r) - heading)) < 0.5,
                  $"heading {HeadingDeg(r):F1} expected {heading:F1}");
            Check("CameraYaw keeps the pawn's tilt", Math.Abs(fr.Y - fp.Y) < 0.001,
                  $"forward.y {fr.Y:F4} vs pawn {fp.Y:F4}");
        }

        // Auto, on foot: body still, camera turning one full circle
        {
            var sel = new HeadingSelector(RotationSource.Auto);
            double t = 0, maxStep = 0, prev = 0, camDeg = 0;
            Quaternion last = Quaternion.Identity;
            bool switched = false;
            for (int i = 0; i < 60 * 8; i++, t += dt)
            {
                if (i >= 60) camDeg += 90 * dt;           // static for 1 s, then 90 deg/s
                last = sel.Resolve(t, Quaternion.Identity, Yaw(camDeg));
                double h = HeadingDeg(last);
                if (i > 0) maxStep = Math.Max(maxStep, Math.Abs(Wrap(h - prev)));
                prev = h;
                switched |= sel.UsingCamera;
            }
            Check("Auto switches to the camera heading on foot", switched && sel.UsingCamera);
            Check("Auto on foot tracks the camera heading",
                  Math.Abs(Wrap(HeadingDeg(last) - camDeg)) < 3,
                  $"published {HeadingDeg(last):F1} vs camera {Wrap(camDeg):F1}");
            Check("Auto switch does not step the heading", maxStep < 8,
                  $"largest per-frame step {maxStep:F2} deg");
        }

        // Auto, swimming / vehicle / first person: body follows the view
        {
            var sel = new HeadingSelector(RotationSource.Auto);
            double t = 0, a = 0;
            bool everCamera = false;
            Quaternion last = Quaternion.Identity;
            for (int i = 0; i < 60 * 8; i++, t += dt)
            {
                a += 90 * dt;
                last = sel.Resolve(t, Yaw(a) * Pitch(15), Yaw(a) * Pitch(15));
                everCamera |= sel.UsingCamera;
            }
            Check("Auto stays on the pawn when the body follows the view", !everCamera);
            Check("Auto in that case publishes the pawn rotation",
                  Math.Abs(Wrap(HeadingDeg(last) - a)) < 0.5);
        }

        // Auto, hand-over: on foot, then the body starts following (swim)
        {
            var sel = new HeadingSelector(RotationSource.Auto);
            double t = 0, cam = 0, body = 0, maxStep = 0, prev = 0;
            Quaternion last = Quaternion.Identity;
            bool wasCamera = false, backToPawn = false;
            for (int i = 0; i < 60 * 14; i++, t += dt)
            {
                cam += 90 * dt;
                if (i >= 60 * 6) body = cam;              // from 6 s the body follows
                last = sel.Resolve(t, Yaw(body), Yaw(cam));
                double h = HeadingDeg(last);
                if (i > 0) maxStep = Math.Max(maxStep, Math.Abs(Wrap(h - prev)));
                prev = h;
                if (i < 60 * 6 && sel.UsingCamera) wasCamera = true;
                if (i > 60 * 12 && !sel.UsingCamera) backToPawn = true;
            }
            Check("Auto hands over camera -> pawn when the body starts to follow", wasCamera && backToPawn);
            Check("Auto hand-over does not step the heading", maxStep < 8, $"largest step {maxStep:F2} deg");
        }

        // Auto, no camera available: the pawn, no exceptions
        {
            var sel = new HeadingSelector(RotationSource.Auto);
            var r = sel.Resolve(0, Yaw(40), null);
            Check("Auto without a camera publishes the pawn", Math.Abs(Wrap(HeadingDeg(r) - 40)) < 0.5);
        }

        // Tuning: a higher trigger keeps small head-looks on the pawn,
        // a lower one switches sooner
        {
            double Switched(HeadingTuning tuning, double lookDeg)
            {
                var sel = new HeadingSelector(RotationSource.Auto, tuning);
                double t = 0, cam = 0, at = -1;
                for (int i = 0; i < 60 * 6; i++, t += dt)
                {
                    if (i >= 30 && cam < lookDeg) cam += 60 * dt;     // turn lookDeg at 60 deg/s
                    sel.Resolve(t, Quaternion.Identity, Yaw(cam));
                    if (at < 0 && sel.UsingCamera) at = t;
                }
                return at;
            }
            double dflt = Switched(new HeadingTuning(), 90);
            double early = Switched(new HeadingTuning { EnterCameraDeg = 8 }, 90);
            double none = Switched(new HeadingTuning { EnterCameraDeg = 40 }, 30);   // 30 deg look < 40 deg trigger
            Check("A lower EnterCameraDeg switches sooner", early > 0 && early < dflt,
                  $"8 deg at {early:F2}s, default at {dflt:F2}s");
            Check("A higher EnterCameraDeg ignores a small head-look", none < 0);
        }

        // Pawn.ComponentType lookup: full name first, then simple name; unknown -> empty
        {
            var full = Ogs.BepInEx.TypeLookup.FindAll("System.String");
            var simple = Ogs.BepInEx.TypeLookup.FindAll("HeadingSelector");
            Check("TypeLookup finds a type by full name", full.Count > 0 && full[0] == typeof(string));
            Check("TypeLookup finds a type by simple name", simple.Count == 1 && simple[0] == typeof(HeadingSelector));
            Check("TypeLookup returns nothing for an unknown name",
                  Ogs.BepInEx.TypeLookup.FindAll("NoSuchTypeAnywhere123").Count == 0);
        }

        // built-in presets: keyed by process name, case-insensitive, unknown -> none
        {
            var tgr = Ogs.BepInEx.GamePresets.For("turbo golf racing");
            var sub = Ogs.BepInEx.GamePresets.For("SubnauticaZero");
            Check("Preset: Turbo Golf Racing seeds the car component list",
                  tgr != null && tgr.ComponentType.StartsWith("CarPhysics") && tgr.RotationSource == "Pawn");
            Check("Preset: Subnautica Below Zero seeds RotationSource Auto",
                  sub != null && sub.RotationSource == "Auto" && sub.ComponentType.Length == 0);
            Check("Preset: an unknown game has none", Ogs.BepInEx.GamePresets.For("SomeOtherGame") == null);
        }

        Check("RotationSource parses case-insensitively",
              HeadingSelector.TryParse(" camerayaw ", out var m) && m == RotationSource.CameraYaw
              && !HeadingSelector.TryParse("bogus", out _));

        return _fail == 0;
    }
}
