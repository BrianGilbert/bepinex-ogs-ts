// GamePresets.cs — built-in defaults for games whose pawn / heading need
// more than the generic lookup. Engine-agnostic and shared by both plugin
// flavors; keyed by the game's process name (what BepInEx's
// Paths.ProcessName reports, case-insensitive, no ".exe").
//
// A preset only seeds the DEFAULT of a config entry: BepInEx writes the
// default into ogs.telemetry.cfg on first run, and from then on the value
// in the file wins. So a user's edit always overrides a preset, and a
// config file written before a preset existed keeps its old values
// (delete the file to re-seed).
//
// Add a game here only once its settings are verified in-game.
using System;

namespace Ogs.BepInEx
{
internal sealed class GamePreset
{
    /// <summary>Pawn.ComponentType default ("" = the generic tag / camera lookup).</summary>
    public string ComponentType = "";
    /// <summary>Pawn.RotationSource default (Pawn | CameraYaw | Camera | Auto).</summary>
    public string RotationSource = "Pawn";
}

internal static class GamePresets
{
    static readonly (string Process, GamePreset Preset)[] Table =
    {
        // car body = the CarPhysics component's object; the garage car
        // (ChassisWithWheels) stands in while no race car exists. No
        // Player tag and no MainCamera-tagged camera in this game
        ("Turbo Golf Racing", new GamePreset { ComponentType = "CarPhysics, ChassisWithWheels" }),

        // on foot the player root stays put while only the camera rig turns
        ("SubnauticaZero", new GamePreset { RotationSource = "Auto" }),
    };

    public static GamePreset? For(string processName)
    {
        foreach (var (process, preset) in Table)
            if (string.Equals(process, processName, StringComparison.OrdinalIgnoreCase))
                return preset;
        return null;
    }
}
}
