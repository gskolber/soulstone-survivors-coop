using Il2CppInterop.Runtime;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SoulstoneCoop;

/// <summary>
/// Debug hotkey, any window (docs/passivas-notas.md): F2 in a match gives YOUR hero its mini-game mechanic for this
/// run, even if that node isn't unlocked in your skill tree, so every hero's passive can be tested without grinding.
///
/// Each hero's mechanic is one skill tree node whose class is SkillTree&lt;X&gt;SO and which adds &lt;X&gt;Component to the
/// player when applied (DemonicBinding, GhostShip, AncestralRitual, MachineSpirit, CursedShrine, ArcanePower,
/// CallOfTheWild, ChaosEnergy, DivineInspiration, ElementalOverload, Rage, Reanimation, YinYang, ...). F2 loads the
/// local character's tree (Resources skill-tree-survivors-character/&lt;char&gt;-skill-tree, talents in subfolders),
/// and applies every such node through the game's own SkillTreeItemSO.Apply(entity, 1) unless the player already has
/// that component (no double mechanic). Only this run, only on this machine: nothing is saved to the profile.
/// </summary>
public static class MechanicDebug
{
    private static bool _prevF2;

    public static void Tick()
    {
        if (NetworkManager.Role == NetRole.None) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        bool f2 = kb.f2Key.isPressed;
        bool pressed = f2 && !_prevF2;
        _prevF2 = f2;
        if (!pressed || Ui.CoopUi.IsTyping) return;
        try { Apply(); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop][debug] F2: {ex}"); }
    }

    private static void Apply()
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null || !GameManagerUtil.HasGameManager || !CoopSession.LocalInRun)
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][debug] F2 (hero mechanic): only inside a match.");
            return;
        }
        string ch = CoopSession.Local.CharacterName;
        if (ch.Length == 0) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][debug] F2: local character unknown."); return; }
        string c = ch.ToLowerInvariant();
        string path = $"skill-tree-survivors-character/{c}-skill-tree";
        var all = Resources.LoadAll(path, Il2CppType.Of<SkillTreeItemSO>());
        int n = all?.Length ?? 0;
        if (n == 0) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][debug] F2: no skill tree at '{path}'."); return; }

        var have = new HashSet<string>();
        var comps = local.GetComponentsInChildren(Il2CppType.Of<Component>(), true);
        for (int i = 0; i < comps.Length; i++)
            if (comps[i] != null) have.Add(comps[i].GetIl2CppType().Name);

        int applied = 0, skipped = 0;
        var done = new HashSet<string>();
        for (int i = 0; i < n; i++)
        {
            var item = all![i]?.TryCast<SkillTreeItemSO>();
            if (item == null) continue;
            string type = item.GetIl2CppType().Name;
            if (!type.StartsWith("SkillTree") || !type.EndsWith("SO") || type == "SkillTreeStatsSO") continue;
            string comp = type.Substring("SkillTree".Length, type.Length - "SkillTree".Length - 2) + "Component";
            if (!done.Add(type)) continue;
            if (!ComponentTypeExists(comp)) continue; // a modifier node, not a mechanic
            if (have.Contains(comp))
            {
                skipped++;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][debug] F2: {ch} already has {comp} ('{item.name}').");
                continue;
            }
            try
            {
                item.Apply(local, 1);
                applied++;
                bool now = local.GetComponentInChildren(Il2CppType.From(FindType(comp)!), true) != null;
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][debug] F2: applied '{item.name}' ({type}) to {ch}: {comp} {(now ? "present" : "NOT present")} after Apply.");
            }
            catch (Exception ex)
            {
                Plugin.StaticLog.LogError($"[SoulstoneCoop][debug] F2: applying '{item.name}' ({type}): {ex.Message}");
            }
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][debug] F2: {ch}: {applied} mechanic(s) applied, {skipped} already present ({n} tree assets).");
    }

    private static readonly Dictionary<string, Type?> TypeCache = new();

    /// <summary>The interop type of a game component by its name (Assembly-CSharp, global namespace).</summary>
    private static Type? FindType(string name)
    {
        if (TypeCache.TryGetValue(name, out var t)) return t;
        t = typeof(Entity).Assembly.GetType(name);
        TypeCache[name] = t;
        return t;
    }

    private static bool ComponentTypeExists(string name)
    {
        var t = FindType(name);
        return t != null && typeof(Component).IsAssignableFrom(t);
    }
}
