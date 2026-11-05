// UnityEngineStubs.cs — compile-only stubs of the small Unity API surface
// this plugin touches. Built as an assembly NAMED "UnityEngine.CoreModule"
// so the plugin binds to the game's real module at runtime by assembly
// identity; signatures mirror stable, ancient Unity APIs only.
// NOT for editor use; extend only with what the plugin actually references.
#pragma warning disable 0649
#nullable enable
using System;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z)
        { this.x = x; this.y = y; this.z = z; }
    }

    public struct Quaternion
    {
        public float x, y, z, w;
    }

    public class Object
    {
        public string name { get { return ""; } } // ancient stable API; binds at runtime
        public static Object[] FindObjectsOfType(Type type) { return new Object[0]; }
        public int GetInstanceID() { return 0; }
        public static implicit operator bool(Object o) => o != null;
        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b);
        public static bool operator !=(Object a, Object b) => !ReferenceEquals(a, b);
        public override bool Equals(object o) => ReferenceEquals(this, o);
        public override int GetHashCode() => base.GetHashCode();
    }

    public class Component : Object
    {
        public Transform transform { get { return new Transform(); } }
        public string tag { get { return ""; } }
        public Component[] GetComponents(Type type) { return new Component[0]; }
    }

    public class Transform : Component
    {
        // stub properties return dummies; real calls bind at runtime
        public Vector3 position { get { return default; } }
        public Quaternion rotation { get { return default; } }
        public Transform root { get { return new Transform(); } }
        public Transform parent { get { return null; } }
    }

    public class GameObject : Object
    {
        public static GameObject FindWithTag(string tag) { return new GameObject(); }
        public static GameObject[] FindGameObjectsWithTag(string tag) { return new GameObject[0]; }
        public Transform transform { get { return new Transform(); } }
    }

    public class Behaviour : Component { public bool enabled { get { return true; } } }
    public class MonoBehaviour : Behaviour { }

    public sealed class Camera : Behaviour
    {
        public static Camera main { get { return new Camera(); } }
        public static Camera[] allCameras { get { return new Camera[0]; } }
        public float depth { get { return 0f; } }
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
    }

    public static class Time
    {
        public static float time => 0f;
        public static float timeScale => 1f;
        public static float realtimeSinceStartup => 0f;
    }
}
