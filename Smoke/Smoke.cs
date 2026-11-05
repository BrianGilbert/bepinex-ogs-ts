// Smoke.cs — standalone verification of the plugin's wire path, no BepInEx
// and no Unity required: drives the exact vendored core publisher with a
// synthetic pawn (Unity frame == OGS-TS frame, so values pass through
// unmapped) and prints frame counts. Exit 0 = pass. Run TelemetryReceive
// beside it to watch 'synthetic_pawn' on the wire.
//
// Also encodes every conformance-corpus frame with THIS repo's vendored
// core and byte-compares against the standard's committed vectors —
// guards the vendored writers against drift.
//
// Corpus dir: sibling OGS-Telemetry-Standard (local dev) or nested
// checkout at the repo root (CI: path: OGS-Telemetry-Standard).
using System;
using System.IO;
using System.Numerics;
using Ogs.Telemetry;

using var sender = new TelemetrySender();
using var pub = new TelemetryPublisher(sender, "bepinex_smoke", motionHz: 60);
pub.DeclareObject("synthetic_pawn");
var rpm = pub.DeclareChannel("engine.rpm", Unit.Rpm, "Synthetic", 0, 9000);
pub.BindChannelRaw(rpm, () => 6500.0 * (0.5 + 0.5 * Math.Sin(0.0)));

var deriver = new BodyDynamicsDeriver();
pub.BindBodyDynamics(() => (deriver.SpecificForce, deriver.AngularVelocity));

// runtime-tracked label swap (injected-mod pattern): name stays
// "synthetic_pawn", the discovery typeLabel updates mid-stream
bool labelSwapped = false;

const double dt = 1.0 / 60.0;
const double yawRate = 90.0;                                  // deg/s
const double w = yawRate * Math.PI / 180.0;                  // rad/s
const double r = 10.0;                                       // metres
double t = 0; int frames = 0;
for (int i = 0; i < 150; i++)
{
    // pawn circling horizontally at 10 m, nose tangential, turning
    // nose-right at yawRate — centripetal ω²r points +sway in the body
    // frame the whole time (Unity/OGS ground plane is X-Z, Y up — NOT
    // UE's X-Y)
    double yr = w * t;
    Vector3 pos = new(10f * MathF.Cos((float)yr), 0f, 10f * MathF.Sin((float)yr));
    float heading = (float)(yr + Math.PI / 2.0);
    Quaternion q = new(0f, MathF.Sin(heading / 2f), 0f, MathF.Cos(heading / 2f));
    deriver.Sample(t, pos, q);
    pub.BindPose(() => pos, () => q);
    if (i == 75) { pub.SetObjectTypeLabel("BP_SyntheticPawn_C"); labelSwapped = true; }
    t += dt;
    pub.Tick(t);
    frames++;
}
if (frames != 150 || !labelSwapped) { Console.WriteLine("FAIL frame count"); return 1; }
Console.WriteLine("smoke: 150 ticks published (discovery + motion sent, typeLabel swapped at tick 75)");
// the label is object identity: a swap after discovery starts a new generation
if (pub.Generation != 1) { Console.WriteLine($"FAIL generation {pub.Generation} after the label swap, expected 1"); return 1; }
Console.WriteLine("PASS typeLabel swap after discovery starts generation 1");

// ---- derived body dynamics assertions (plugin wiring, delta velocity) ----
{
    const float g = BodyDynamicsDeriver.Gravity;
    Vector3 av = deriver.AngularVelocity;
    Vector3 sf = deriver.SpecificForce;
    Console.WriteLine($"dynamics: av=({av.X:F4}, {av.Y:F4}, {av.Z:F4}) " +
                      $"sf=({sf.X:F4}, {sf.Y:F4}, {sf.Z:F4}) " +
                      $"[expect av.y={w:F4}, sf.y={g:F4}, horiz={w * w * r:F4}]");
    bool ok = true;
    ok &= Math.Abs(av.Y - w) < 0.05 && Math.Abs(av.X) < 0.01 && Math.Abs(av.Z) < 0.01;
    Console.WriteLine((Math.Abs(av.Y - w) < 0.05 && Math.Abs(av.X) < 0.01 && Math.Abs(av.Z) < 0.01)
        ? "PASS dynamics: yaw rate ≈ 90°/s nose-right (+y, C-4)"
        : "FAIL dynamics: yaw rate");
    ok &= Math.Abs(sf.Y - g) < 0.2;
    Console.WriteLine(Math.Abs(sf.Y - g) < 0.2
        ? "PASS dynamics: heave ≈ +1 g (accelerometer-style)"
        : "FAIL dynamics: heave");
    // centripetal on +sway; surge leakage bounded by the accel LP phase
    // lag atan(ωτ) on the co-rotating vector (documented filter property)
    bool sway = Math.Abs(sf.X - (float)(w * w * r)) < 1.5f &&
                Math.Abs(sf.Z) < (float)(w * w * r) * 0.25f;
    ok &= sway;
    Console.WriteLine(sway
        ? "PASS dynamics: centripetal ω²r on +sway (C-4 right-turn)"
        : "FAIL dynamics: centripetal");
    float horiz = MathF.Sqrt(sf.X * sf.X + sf.Z * sf.Z);
    ok &= Math.Abs(horiz - (float)(w * w * r)) < 1.5f;
    Console.WriteLine(Math.Abs(horiz - (float)(w * w * r)) < 1.5f
        ? "PASS dynamics: horizontal specific force ≈ centripetal ω²r"
        : "FAIL dynamics: horizontal");
    if (!ok) return 1;
}

// ---- conformance corpus guard (vendored writers vs golden vectors) ----
int corpusFail = 0;
void CorpusCheck(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} corpus: {name}");
    if (!ok) corpusFail++;
}
if (!HeadingChecks.Run()) return 1;
if (!DiscoveryChecks.Run()) return 1;
string? corpusDir = null;
for (var d = new DirectoryInfo(AppContext.BaseDirectory);
     d != null && corpusDir == null; d = d.Parent!)
{
    string?[] candidates =
    {
        d.Parent is DirectoryInfo p
            ? Path.Combine(p.FullName, "OGS-Telemetry-Standard", "conformance") : null,
        Path.Combine(d.FullName, "OGS-Telemetry-Standard", "conformance"),
    };
    foreach (var c in candidates)
        if (c != null && Directory.Exists(c)) { corpusDir = c; break; }
}
if (corpusDir == null)
{
    Console.WriteLine("FAIL corpus: OGS-Telemetry-Standard/conformance not found "
                    + "(sibling or nested checkout)");
    return 1;
}

foreach (var vector in ConformanceVectors.All())
{
    var hex = string.Concat(File.ReadAllLines(
        Path.Combine(corpusDir!, "vectors", vector.Id + ".hex")));
    CorpusCheck(vector.Id + " encode",
        vector.Encode().AsSpan().SequenceEqual(Convert.FromHexString(hex)));
}
if (corpusFail > 0) return 1;
return 0;
