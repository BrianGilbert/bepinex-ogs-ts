// DiscoveryChecks.cs — drives PawnDiscovery with simulated scenes at the
// plugin's 10 Hz sampling: a chase camera among distractors (static props,
// a frozen logical player object, an independent ball, an NPC at another
// speed), a first-person camera, a standing camera, and a camera that
// follows nothing.
using System;
using System.Collections.Generic;
using System.Numerics;
using Ogs.BepInEx;

static class DiscoveryChecks
{
    static int _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} discovery: {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _fail++;
    }

    // a car driving a wobbling circle: position and heading at time t
    static Vector3 CarPos(double t, double speed = 20, double radius = 60)
    {
        double a = speed * t / radius;
        return new Vector3((float)(radius * Math.Sin(a)), 0f, (float)(radius * (1 - Math.Cos(a))));
    }

    static Vector3 Forward(double t, double speed = 20, double radius = 60)
    {
        double a = speed * t / radius;
        return new Vector3((float)Math.Cos(a), 0f, (float)Math.Sin(a));
    }

    // candidate ids for the scenes: index meaning is fixed so tests can name them
    const int Car = 0, Wheel1 = 1, Wheel2 = 2, Frozen = 3, Ball = 4, Npc = 5, PropStart = 6;

    static Vector3[] Props() => new[]
    {
        new Vector3(30, 0, 10), new Vector3(-40, 0, 25), new Vector3(60, 0, 80),
        new Vector3(5, 0, -30), new Vector3(100, 0, 40), new Vector3(20, 0, 60),
    };

    // chase camera behind the car, smoothed (it lags the car a little)
    static (int pick, float score) ChaseScene(double seconds, bool carMoves = true,
        int carDepth = 0, int wheelDepth = 1, bool carHasBody = false, bool wheelsUnderCar = false)
    {
        int propCount = Props().Length;
        var depths = new int[PropStart + propCount];
        depths[Car] = carDepth; depths[Wheel1] = wheelDepth; depths[Wheel2] = wheelDepth;
        var parents = new int[depths.Length];
        for (int i = 0; i < parents.Length; i++) parents[i] = -1;
        if (wheelsUnderCar) { parents[Wheel1] = Car; parents[Wheel2] = Car; }
        var body = new bool[depths.Length];
        body[Car] = carHasBody;
        var disc = new PawnDiscovery(depths, parents, body);
        var camPos = CarPos(0) - Forward(0) * 8f + new Vector3(0, 3, 0);
        int pick = -1; float score = 0;
        for (double t = 0; t <= seconds; t += 0.1)
        {
            double tt = carMoves ? t : 0;
            var car = CarPos(tt);
            var target = car - Forward(tt) * 8f + new Vector3(0, 3, 0);
            camPos = Vector3.Lerp(camPos, target, 0.35f);          // camera smoothing lag
            var c = new Vector3[depths.Length];
            c[Car] = car;
            c[Wheel1] = car + new Vector3(1, -0.5f, 1.5f);
            c[Wheel2] = car + new Vector3(-1, -0.5f, -1.5f);
            c[Frozen] = Vector3.Zero;                              // logical player root at the origin
            c[Ball] = new Vector3((float)(40 * Math.Sin(t * 1.3)), 1f, (float)(30 * Math.Cos(t * 0.9)) + 30f);
            c[Npc] = CarPos(t, speed: 26, radius: 45) + new Vector3(15, 0, 0);
            var props = Props();
            for (int i = 0; i < propCount; i++) c[PropStart + i] = props[i];
            disc.AddSample(camPos, c);
            pick = disc.Evaluate(out score);
        }
        return (pick, score);
    }

    public static bool Run()
    {
        // chase camera: the car body, not its wheels, props, the frozen root, the ball or the NPC
        var (pick, score) = ChaseScene(6);
        Check("Chase camera: picks the car body", pick == Car, $"picked {pick}, score {score:F3}");

        // wheels shallower in the hierarchy than the body: the Rigidbody decides
        var wheelsHigh = ChaseScene(6, carDepth: 3, wheelDepth: 1, carHasBody: true);
        Check("Wheels shallower than the body: the physics body wins", wheelsHigh.pick == Car,
              $"picked {wheelsHigh.pick}, score {wheelsHigh.score:F3}");

        // no Rigidbody info, but the body contains the wheels
        var parentOf = ChaseScene(6, carDepth: 3, wheelDepth: 1, wheelsUnderCar: true);
        Check("The object containing the other tied objects wins", parentOf.pick == Car,
              $"picked {parentOf.pick}, score {parentOf.score:F3}");

        // a car that does not move: the camera has no travel, so no decision
        var still = ChaseScene(6, carMoves: false);
        Check("Standing camera: no decision (nothing to learn)", still.pick == -1);

        // first-person: the pawn root walks with the camera at eye height
        {
            int propCount = Props().Length;
            var depths = new int[2 + propCount];            // 0 = pawn root, 1 = frozen logical player
            var disc = new PawnDiscovery(depths);
            int p = -1; float sc = 0;
            for (double t = 0; t <= 6; t += 0.1)
            {
                var pawn = CarPos(t, speed: 5, radius: 30);
                var cam = pawn + new Vector3(0, 1.6f, 0);
                var c = new Vector3[depths.Length];
                c[0] = pawn; c[1] = Vector3.Zero;
                var props = Props();
                for (int i = 0; i < propCount; i++) c[2 + i] = props[i];
                disc.AddSample(cam, c);
                p = disc.Evaluate(out sc);
            }
            Check("First-person walk: picks the pawn root", p == 0, $"picked {p}, score {sc:F3}");
        }

        // the camera travels but follows nothing among the candidates
        {
            var depths = new int[4];
            var disc = new PawnDiscovery(depths);
            int p = -1;
            for (double t = 0; t <= 6; t += 0.1)
            {
                var cam = CarPos(t);
                var c = new[] { Vector3.Zero, new Vector3(50, 0, 50),
                                new Vector3((float)(30 * Math.Sin(t)), 0, 20),
                                new Vector3(80, 0, (float)(10 * t)) };
                disc.AddSample(cam, c);
                p = disc.Evaluate(out _);
            }
            Check("Nothing follows the camera: no confident answer", p == -1);
        }

        // too few samples: wait
        {
            var disc = new PawnDiscovery(new int[1]);
            for (int i = 0; i < PawnDiscovery.MinSamples - 1; i++)
                disc.AddSample(new Vector3(i * 2f, 0, 0), new[] { new Vector3(i * 2f, 0, 5) });
            Check("Too few samples: no decision", disc.Evaluate(out _) == -1);
        }

        // a candidate that vanishes (destroyed object) is ignored, not a crash
        {
            var disc = new PawnDiscovery(new int[2]);
            for (int i = 0; i < 30; i++)
            {
                var cam = new Vector3(i * 2f, 0, 0);
                disc.AddSample(cam, new[] { new Vector3(float.NaN, float.NaN, float.NaN), new Vector3(i * 2f, 0, 5) });
            }
            Check("A destroyed candidate is skipped", disc.Evaluate(out _) == 1);
        }

        return _fail == 0;
    }
}
