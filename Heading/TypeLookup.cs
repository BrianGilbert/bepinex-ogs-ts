// TypeLookup.cs — resolve a game type from a configured name
// (Pawn.ComponentType) by scanning the loaded assemblies. Engine-agnostic
// (System only) and shared by both plugin flavors; each flavor then keeps
// the candidates that derive from UnityEngine.Component and finds live
// instances through its own engine API (UnityEngine on Mono, the
// Il2CppInterop proxies on IL2CPP).
//
// A name matches a type's full name ("car.Car") or its simple name
// ("Car"). Full-name matches come first; several types can share a simple
// name (a game may have a MonoBehaviour `Car` and a data class `Car`), so
// callers filter the list instead of taking the first hit.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Ogs.BepInEx
{
internal static class TypeLookup
{
    public static List<Type> FindAll(string name)
    {
        var full = new List<Type>();
        var simple = new List<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t.FullName == name) full.Add(t);
                else if (t.Name == name) simple.Add(t);
            }
        }
        full.AddRange(simple);
        return full;
    }
}
}
