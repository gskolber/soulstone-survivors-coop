using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SoulstoneCoop.Skills;

/// <summary>
/// 1.6.0 diagnostic: which reference fields of a game object are null, by their real offset (il2cpp_field_get_offset of
/// the interop's NativeFieldInfoPtr_ fields). Made for the Machinist's crystals: TemporaryRotatingSkill.DoActivate
/// (0x56A5B0) throws on a null at one of +0x90, +0xE0, +0x130, +0x258, +0x260, +0x268, +0x288 on our replica only;
/// logging the copy and the owner's own instance side by side names the field that's missing. Raw pointer reads: only
/// with [Debug] PowersProbe (the log-only diagnostic switch), off by default.
/// </summary>
internal static unsafe class FieldDump
{
    private static readonly HashSet<string> Logged = new();

    internal static void Log(Il2CppSystem.Object obj, string label, params int[] offsets)
    {
        if (Plugin.PowersProbe == null || !Plugin.PowersProbe.Value) return;
        if (obj == null || !Logged.Add(label)) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            for (var t = obj.GetType(); t != null && t != typeof(MonoBehaviour) && t != typeof(Il2CppSystem.Object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!f.Name.StartsWith("NativeFieldInfoPtr_") || f.FieldType != typeof(IntPtr)) continue;
                    var fi = (IntPtr)f.GetValue(null)!;
                    if (fi == IntPtr.Zero) continue;
                    int off = (int)IL2CPP.il2cpp_field_get_offset(fi);
                    if (offsets.Length > 0 && Array.IndexOf(offsets, off) < 0) continue;
                    int kind = IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_field_get_type(fi));
                    // string, class, generic instance, object, array, szarray: a pointer we can test
                    bool reference = kind == 0x0e || kind == 0x12 || kind == 0x15 || kind == 0x1c || kind == 0x14 || kind == 0x1d;
                    string val = !reference ? "value" : (*(IntPtr*)((byte*)obj.Pointer + off) == IntPtr.Zero ? "NULL" : "set");
                    sb.Append($" {t.Name}.{f.Name.Substring("NativeFieldInfoPtr_".Length)}@0x{off:X}={val}");
                }
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][casts] Fields of {label}:{sb}");
        }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][casts] Fields of {label}: {ex.Message}"); }
    }

    /// <summary>The offsets TemporaryRotatingSkill.DoActivate null-checks before its first call.</summary>
    internal static readonly int[] RotatingOffsets = { 0x90, 0xE0, 0x130, 0x258, 0x260, 0x268, 0x288 };
}
