using System;
using System.Numerics;
using Ogs.BepInEx;

static class DynamicsChecks
{
    const double PhysicsHz = 50.0;
    const double Fps = 144.0;
    static int _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} dynamics: {name}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
        if (!ok) _fail++;
    }

    static (PawnDynamics dynamics, float peak, int fallbacks, double firstFallback) Drive(Func<double, Vector3?>? velocityAt,
        double speed, double acceleration = 0, bool stepped = true, float secondsAlreadyPlayed = 0, Func<double, double>? travelTime = null)
    {
        var dynamics = new PawnDynamics();
        dynamics.Restart(secondsAlreadyPlayed, Vector3.Zero, Quaternion.Identity);
        float peak = 0;
        double previous = 0, firstFallback = -1;
        int fallbacks = 0;
        bool wasEngine = false;
        for (int i = 1; i <= 1440; i++)
        {
            double now = (i + 0.1 * Math.Sin(i * 1.7)) / Fps;
            double physics = stepped ? Math.Floor(now * PhysicsHz) / PhysicsHz : now;
            double travelled = travelTime?.Invoke(physics) ?? physics;
            var position = new Vector3(0, 0, (float)(speed * travelled + 0.5 * acceleration * travelled * travelled));
            dynamics.Sample((float)(now - previous), position, Quaternion.Identity, velocityAt?.Invoke(physics));
            previous = now;
            if (wasEngine && !dynamics.UsingEngineVelocity)
            {
                fallbacks++;
                if (firstFallback < 0) firstFallback = now;
            }
            wasEngine = dynamics.UsingEngineVelocity;
            if (i > Fps) peak = Math.Max(peak, Math.Abs(dynamics.SpecificForce.Z));
        }
        return (dynamics, peak, fallbacks, firstFallback);
    }

    struct EngineVector { public float x, y, z; }
    sealed class OldBody { public EngineVector velocity => new EngineVector { x = 1, y = 2, z = 3 }; }
    sealed class NewBody
    {
        public EngineVector linearVelocity => new EngineVector { x = 4, y = 5, z = 6 };
        public EngineVector velocity => throw new NotSupportedException();
    }
    sealed class StrippedBody { public EngineVector velocity => throw new NotSupportedException(); }

    public static bool Run()
    {
        var fromPosition = Drive(null, 68);
        Check("uneven stepping from position alone spikes (the field symptom)",
              fromPosition.peak > 100, $"peak {fromPosition.peak:F1}");

        var fromBody = Drive(_ => new Vector3(0, 0, 68), 68);
        Check("uneven stepping with body velocity stays quiet",
              fromBody.peak < 0.5f && fromBody.dynamics.UsingEngineVelocity, $"peak {fromBody.peak:F2}");

        var accelerating = Drive(t => new Vector3(0, 0, (float)(30 + 10 * t)), 30, 10);
        Check("real acceleration comes through the body velocity",
              Math.Abs(accelerating.dynamics.SpecificForce.Z - 10) < 2,
              $"surge {accelerating.dynamics.SpecificForce.Z:F1}");

        var smooth = Drive(null, 30, 10, stepped: false);
        var dead = Drive(_ => Vector3.Zero, 30, 10, stepped: false);
        Check("a body velocity stuck at zero falls back to position",
              !dead.dynamics.UsingEngineVelocity &&
              Math.Abs(dead.dynamics.SpecificForce.Z - smooth.dynamics.SpecificForce.Z) < 0.5f,
              $"surge {dead.dynamics.SpecificForce.Z:F1}, position alone {smooth.dynamics.SpecificForce.Z:F1}");

        var wrong = Drive(_ => new Vector3(60, 0, 0), 68);
        Check("a body velocity in the wrong direction is rejected", !wrong.dynamics.UsingEngineVelocity);

        var parked = Drive(_ => Vector3.Zero, 0);
        Check("a parked body's zero velocity is believed", parked.dynamics.UsingEngineVelocity);

        var longSession = Drive(null, 68, 0, stepped: false, secondsAlreadyPlayed: 4 * 3600);
        Check("smooth motion stays quiet four hours into a session",
              longSession.peak < 1, $"peak {longSession.peak:F2}");

        for (int offset = 0; offset < 5; offset++)
        {
            double from = 3 + offset * 0.1;
            var brief = Drive(t => new Vector3(0, 0, t >= from && t < from + 0.9 ? 0 : 68), 68);
            Check($"a 0.9 s disagreement from {from:F1} s keeps body velocity", brief.fallbacks == 0);
        }
        var apart = Drive(t => new Vector3(0, 0, (t >= 2 && t < 2.9) || (t >= 6 && t < 6.9) ? 0 : 68), 68);
        Check("separate brief disagreements do not add up", apart.fallbacks == 0);
        var lasting = Drive(t => new Vector3(0, 0, t >= 3 ? 0 : 68), 68);
        Check("a lasting disagreement falls back after 1 to 2.1 s",
              lasting.firstFallback >= 4 && lasting.firstFallback <= 5.1, $"at {lasting.firstFallback:F2} s");

        bool kept = true;
        float frozenPeak = 0;
        for (int offset = 0; offset < 5; offset++)
        {
            double from = 3 + offset * 0.1;
            var frozen = Drive(_ => new Vector3(0, 0, 68), 68, stepped: false,
                travelTime: t => t < from ? t : t < from + 1.5 ? from : t - 1.5);
            kept &= frozen.fallbacks == 0;
            frozenPeak = Math.Max(frozenPeak, frozen.peak);
        }
        Check("a briefly frozen pose keeps body velocity", kept);
        Check("a briefly frozen pose causes no surge spike", frozenPeak < 0.5f, $"peak {frozenPeak:F2}");
        var stuck = Drive(_ => new Vector3(0, 0, 68), 68, stepped: false, travelTime: t => Math.Min(t, 3));
        Check("a pose that stays frozen is eventually not believed",
              stuck.firstFallback > 6 && stuck.firstFallback <= 8.5, $"at {stuck.firstFallback:F2} s");
        var huge = Drive(_ => new Vector3(0, 0, 6800), 68, stepped: false);
        Check("a velocity 100 times too big is still rejected",
              huge.firstFallback > 0 && huge.firstFallback <= 5.7, $"at {huge.firstFallback:F2} s");

        var old = new OldBody();
        Check("reads Rigidbody.velocity",
              VelocityReader.For(typeof(OldBody), old)?.Invoke() == new Vector3(1, 2, 3));
        Check("prefers linearVelocity",
              VelocityReader.For(typeof(NewBody), new NewBody())?.Invoke() == new Vector3(4, 5, 6));
        Check("a body whose velocity cannot be read gives no reader",
              VelocityReader.For(typeof(StrippedBody), new StrippedBody()) == null);

        return _fail == 0;
    }
}
