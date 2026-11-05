// PawnDiscovery.cs — find the player's body without game knowledge, from
// what the camera does. Engine-agnostic (System.Numerics only) and shared
// by both plugin flavors; each flavor enumerates candidate transforms and
// feeds positions in.
//
// The signal: in nearly every game the camera is attached to the player's
// body (a chase rig or a first-person eye), so the body is the object that
// moves when the camera moves and keeps a near-constant distance from it.
// Static scenery fails the movement test, independent movers (a ball, other
// cars) fail the constant-distance test, and a frozen logical player object
// fails both. Names, tags and component types are never consulted.
//
// Each candidate is scored over a sliding window of samples:
//   distance term  = std(|candidate - camera|) / (mean + 1)
//   movement term  = 0.5 * | path(candidate) / path(camera) - 1 |
// (lower is better). Children of the body (wheels, meshes) score the same
// as the body itself, so among candidates within TieMargin of the best the
// body is chosen by (1) a physics body (Rigidbody / CharacterController) over
// one without, (2) the object that contains the most of the other tied
// candidates, (3) the shallowest. A wheel is a leaf with no body of its own;
// the chassis carries the Rigidbody and the wheels hang under it. Nothing is decided until the camera
// has actually travelled (MinCameraPath); a standing camera carries no
// information about what it follows.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ogs.BepInEx
{
internal sealed class PawnDiscovery
{
    public const int MinSamples = 10;          // ~1 s at the 10 Hz sampling
    public const int MaxSamples = 40;          // ~4 s window
    public const float MinCameraPath = 1.0f;   // world units the camera must travel
    public const float MaxScore = 0.25f;       // above this: not confident
    public const float TieMargin = 0.05f;      // scores this close count as equal
    public const float MinValidFraction = 0.9f;

    readonly int[] _depth;
    readonly int[] _parent;      // index of each candidate's parent in the list, -1 = none / not a candidate
    readonly bool[] _body;       // candidate carries a physics body
    readonly List<Vector3> _cam = new List<Vector3>();
    readonly List<Vector3[]> _cand = new List<Vector3[]>();

    /// <param name="depths">hierarchy depth of each candidate (0 = a root)</param>
    /// <param name="parents">index of each candidate's parent within the list (-1 = none)</param>
    /// <param name="hasBody">whether each candidate carries a physics body</param>
    public PawnDiscovery(int[] depths, int[]? parents = null, bool[]? hasBody = null)
    {
        _depth = depths;
        _parent = parents ?? NewFilled(depths.Length, -1);
        _body = hasBody ?? new bool[depths.Length];
    }

    static int[] NewFilled(int n, int v) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

    public int CandidateCount => _depth.Length;
    public int SampleCount => _cam.Count;

    /// <summary>One sample: the camera position and each candidate's position
    /// (NaN components = unavailable this sample, e.g. destroyed).</summary>
    public void AddSample(Vector3 camera, Vector3[] candidates)
    {
        if (candidates.Length != _depth.Length) throw new ArgumentException("candidate count changed");
        _cam.Add(camera);
        _cand.Add(candidates);
        if (_cam.Count > MaxSamples) { _cam.RemoveAt(0); _cand.RemoveAt(0); }
    }

    static bool Valid(Vector3 v) => !float.IsNaN(v.X) && !float.IsNaN(v.Y) && !float.IsNaN(v.Z);

    /// <summary>Index of the best candidate, or -1 when there is no confident
    /// answer yet (too few samples, the camera has not travelled, or no
    /// candidate is attached to it).</summary>
    public int Evaluate(out float score)
    {
        score = float.MaxValue;
        int n = _cam.Count;
        if (n < MinSamples) return -1;

        float camPath = 0f;
        for (int k = 1; k < n; k++) camPath += Vector3.Distance(_cam[k], _cam[k - 1]);
        if (camPath < MinCameraPath) return -1;

        var scores = new float[_depth.Length];
        float best = float.MaxValue;
        for (int i = 0; i < _depth.Length; i++)
        {
            scores[i] = float.MaxValue;
            int valid = 0;
            float path = 0f, sum = 0f, sumSq = 0f;
            Vector3? prev = null;
            for (int k = 0; k < n; k++)
            {
                var p = _cand[k][i];
                if (!Valid(p)) { prev = null; continue; }
                valid++;
                float d = Vector3.Distance(p, _cam[k]);
                sum += d; sumSq += d * d;
                if (prev.HasValue) path += Vector3.Distance(p, prev.Value);
                prev = p;
            }
            if (valid < n * MinValidFraction) continue;
            float mean = sum / valid;
            float variance = Math.Max(0f, sumSq / valid - mean * mean);
            float distanceTerm = (float)Math.Sqrt(variance) / (mean + 1f);
            float movementTerm = 0.5f * Math.Abs(path / camPath - 1f);
            scores[i] = distanceTerm + movementTerm;
            if (scores[i] < best) best = scores[i];
        }
        if (best > MaxScore) return -1;

        // body vs its children: same motion, so choose among the tied set
        var tied = new List<int>();
        for (int i = 0; i < scores.Length; i++)
            if (scores[i] <= best + TieMargin) tied.Add(i);

        // how many other tied candidates sit under each one
        var contains = new int[scores.Length];
        foreach (int j in tied)
        {
            int a = _parent[j];
            for (int hops = 0; a >= 0 && hops < 64; hops++, a = _parent[a]) contains[a]++;
        }

        int pick = -1;
        foreach (int i in tied)
        {
            if (pick < 0) { pick = i; continue; }
            if (_body[i] != _body[pick]) { if (_body[i]) pick = i; continue; }
            if (contains[i] != contains[pick]) { if (contains[i] > contains[pick]) pick = i; continue; }
            if (_depth[i] != _depth[pick]) { if (_depth[i] < _depth[pick]) pick = i; continue; }
            if (scores[i] < scores[pick]) pick = i;
        }
        score = scores[pick];
        return pick;
    }
}
}
