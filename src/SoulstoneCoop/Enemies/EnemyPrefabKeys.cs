using Il2CppInterop.Runtime;
using UnityEngine;

namespace SoulstoneCoop.Enemies;

/// <summary>
/// Stable cross-machine key for an enemy prefab. Both sides run EnemyPrefabsUtil.Setup for the same
/// map, so the tier lists (EnemyPrefabByPowerWithData.PrefabAddress / group PrefabAddresses) match.
/// Key formats:
///   "Some/Prefab/Address"    - a PrefabAddress from the tier lists (preferred; carries PrefabData).
///   "res:Some/Resources/Path" - phase 4D: anything else that lives under Resources (bosses, ethereals, void
///                               hunters, VoidPresence, loot goblin, minerals, pillars). Loaded with
///                               ResourcesUtil.Load&lt;Entity&gt; on the client.
///   "sum:&lt;ownerNetId&gt;:Name"  - phase 4D: a summon whose prefab is a direct reference in the owner's skill
///                               (SummonEntity*Effect.PossiblePrefabs), not a Resources path.
///   "@PrefabName"             - last resort (name lookup in the lists and in loaded assets).
/// See docs/fase4d-investigacao.md, section 6.
/// </summary>
public static class EnemyPrefabKeys
{
    public const string NamePrefix = "@";
    public const string ResPrefix = "res:";
    public const string SumPrefix = "sum:";

    // Where the game keeps the non-list prefabs (docs/fase4d-investigacao.md, section 2).
    private static readonly string[] ProbeFolders = { "Creeps/", "Bosses/", "Bosses/Ethereals/", "Minerals/", "Pillars/" };

    public sealed class Resolved
    {
        public Entity Prefab = null!;
        public EnemyPrefabData? PrefabData; // null when the key didn't come from the tier lists
    }

    private static readonly Dictionary<IntPtr, string> HostKeyByPrefabPtr = new();
    private static readonly Dictionary<string, Resolved> ClientCache = new();

    // Host: prefab name -> Resources path, from the game's own data (strings only), plus probe results.
    private static readonly Dictionary<string, string> ResIndex = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> ResProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _resIndexBuilt;

    public static void Reset()
    {
        HostKeyByPrefabPtr.Clear();
        ClientCache.Clear();
        ResIndex.Clear();
        ResProbeCache.Clear();
        _resIndexBuilt = false;
        OutsideListsResolved = 0;
    }

    public static string StripClone(string name)
    {
        int i = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return (i >= 0 ? name.Substring(0, i) : name).Trim();
    }

    private static string LastSegment(string address)
    {
        int i = address.LastIndexOf('/');
        return i >= 0 ? address.Substring(i + 1) : address;
    }

    private static IEnumerable<Il2CppSystem.Collections.Generic.List<EnemyPrefabByPowerWithData>?> SingleLists()
    {
        yield return EnemyPrefabsUtil.Tier1; yield return EnemyPrefabsUtil.Tier2; yield return EnemyPrefabsUtil.Tier3;
        yield return EnemyPrefabsUtil.Tier4; yield return EnemyPrefabsUtil.Tier5;
        yield return EnemyPrefabsUtil.Tier1Elite; yield return EnemyPrefabsUtil.Tier2Elite; yield return EnemyPrefabsUtil.Tier3Elite;
        yield return EnemyPrefabsUtil.Tier4Elite; yield return EnemyPrefabsUtil.Tier5Elite;
    }

    private static IEnumerable<Il2CppSystem.Collections.Generic.List<EnemyPrefabGroupByPowerWithData>?> GroupLists()
    {
        yield return EnemyPrefabsUtil.Tier1EliteGroups; yield return EnemyPrefabsUtil.Tier2EliteGroups;
        yield return EnemyPrefabsUtil.Tier3EliteGroups; yield return EnemyPrefabsUtil.Tier4EliteGroups;
        yield return EnemyPrefabsUtil.Tier5EliteGroups;
    }

    /// <summary>True once EnemyPrefabsUtil.Setup has filled at least one tier list.</summary>
    public static bool ListsReady()
    {
        foreach (var l in SingleLists())
            if (l != null && l.Count > 0) return true;
        return false;
    }

    // ---------------- host ----------------

    /// <summary>Host: key for an entity. <paramref name="prefab"/> is the prefab captured from
    /// EnemyPrefabsUtil.SpawnEnemyEntity when available.</summary>
    public static string HostKeyFor(Entity instance, Entity? prefab, ushort summonOwnerNetId = 0)
    {
        if (prefab != null)
        {
            if (HostKeyByPrefabPtr.TryGetValue(prefab.Pointer, out var cached))
                return cached.StartsWith(NamePrefix, StringComparison.Ordinal) && summonOwnerNetId != 0
                    ? SumKey(summonOwnerNetId, cached.Substring(NamePrefix.Length)) : cached;
            string pname = StripClone(prefab.name);
            string? key = FindAddressByLoadedPrefab(prefab) ?? FindAddressByName(pname) ?? FindResPath(pname, prefab);
            key ??= NamePrefix + pname;
            HostKeyByPrefabPtr[prefab.Pointer] = key;
            return key.StartsWith(NamePrefix, StringComparison.Ordinal) && summonOwnerNetId != 0 ? SumKey(summonOwnerNetId, pname) : key;
        }

        string name = StripClone(instance.gameObject.name);
        string? k = FindAddressByName(name) ?? FindResPath(name, null);
        if (k != null) return k;
        return summonOwnerNetId != 0 ? SumKey(summonOwnerNetId, name) : NamePrefix + name;
    }

    private static string SumKey(ushort owner, string name) => $"{SumPrefix}{owner}:{name}";

    /// <summary>"res:&lt;path&gt;" for a prefab that lives under Resources, or null. First the game's own data
    /// (Global.BossPrefabs, Global.VoidHunterPrefabs, the active special-entities affix, the loot goblin), then
    /// a probe of the folders those spawners use; the probe only accepts the asset if it is the same prefab
    /// (when we know it) or at least has the same name.</summary>
    private static string? FindResPath(string name, Entity? prefab)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!_resIndexBuilt) BuildResIndex();
        if (ResIndex.TryGetValue(name, out var path)) return ResPrefix + path;
        if (ResProbeCache.TryGetValue(name, out var probed)) return probed;

        string? found = null;
        foreach (var folder in ProbeFolders)
        {
            Entity? loaded = null;
            try { loaded = ResourcesUtil.Load<Entity>(folder + name); } catch { /* not there */ }
            if (loaded == null) continue;
            if (prefab != null && loaded.Pointer != prefab.Pointer) continue;
            if (!string.Equals(StripClone(loaded.name), name, StringComparison.OrdinalIgnoreCase)) continue;
            found = ResPrefix + folder + name;
            break;
        }
        ResProbeCache[name] = found;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][enemies] Host: key for '{name}' -> {found ?? "(not under Resources)"}");
        return found;
    }

    private static void AddRes(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        string last = LastSegment(path!);
        if (!ResIndex.ContainsKey(last)) ResIndex[last] = path!;
    }

    private static void BuildResIndex()
    {
        _resIndexBuilt = true;
        try
        {
            var bosses = Global.BossPrefabs?.BossPrefabs;
            if (bosses != null)
                for (int i = 0; i < bosses.Length; i++)
                {
                    var names = bosses[i]?.Addresses;
                    if (names == null) continue;
                    for (int j = 0; j < names.Count; j++)
                    {
                        // Same concatenation as EnemyPrefabsUtil.FillBossPools (0x6E0C70).
                        AddRes("Bosses/" + names[j]);
                        AddRes("Bosses/Ethereals/" + names[j] + "-Ethereal");
                    }
                }
            var hunters = Global.VoidHunterPrefabs?.VoidHunterPrefabs;
            if (hunters != null)
                for (int i = 0; i < hunters.Length; i++)
                {
                    var addrs = hunters[i]?.PrefabAddresses;
                    if (addrs == null) continue;
                    for (int j = 0; j < addrs.Length; j++) AddRes("Creeps/" + addrs[j]); // GetVoidHunterPrefabsExcludingId
                }
            var comps = GameManagerUtil.GameData?.GameManagerComponents;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    var affix = c.TryCast<GameManagerAffixSpecialEntitiesComponent>()?._affix;
                    var addrs = affix?.Addresses;
                    if (addrs != null) for (int j = 0; j < addrs.Length; j++) AddRes(addrs[j]);
                    var goblin = c.TryCast<GameManagerLootEnemySpawnerComponent>();
                    if (goblin != null) AddRes(goblin._prefabAddress);
                }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][enemies] Host: Resources key index built ({ResIndex.Count} names).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][enemies] Host: building the Resources key index: {ex.Message}");
        }
    }

    private static string? FindAddressByLoadedPrefab(Entity prefab)
    {
        foreach (var list in SingleLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var p = e._prefab; // field, not the Prefab getter: don't force-load every enemy
                if (p != null && p.Pointer == prefab.Pointer) return e.PrefabAddress;
            }
        }
        return null;
    }

    private static string? FindAddressByName(string name)
    {
        foreach (var list in SingleLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var p = e._prefab;
                if (p != null && StripClone(p.name) == name) return e.PrefabAddress;
                var addr = e.PrefabAddress;
                if (addr != null && LastSegment(addr) == name) return addr;
            }
        }
        foreach (var list in GroupLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var addrs = list[i].PrefabAddresses;
                if (addrs == null) continue;
                for (int j = 0; j < addrs.Length; j++)
                    if (addrs[j] != null && LastSegment(addrs[j]) == name) return addrs[j];
            }
        }
        return null;
    }

    // ---------------- client ----------------

    /// <summary>Issue #6: tier-list keys this client had to load itself, because the host rolled a variant we didn't.</summary>
    public static int OutsideListsResolved;

    /// <summary>Client: prefab (+ PrefabData when known) for a key, or null. <paramref name="msg"/> carries the
    /// host's EnemyPrefabData type (issue #6), for a tier-list key that isn't in our own lists.</summary>
    public static Resolved? ClientResolve(string key, in EnemySpawnMsg msg)
    {
        if (ClientCache.TryGetValue(key, out var cached) && cached.Prefab != null) return cached;

        Resolved? r;
        if (key.StartsWith(ResPrefix, StringComparison.Ordinal))
        {
            string path = key.Substring(ResPrefix.Length);
            var prefab = ResourcesUtil.Load<Entity>(path);
            r = prefab != null ? new Resolved { Prefab = prefab } : ResolveByName(LastSegment(path));
        }
        else if (key.StartsWith(SumPrefix, StringComparison.Ordinal))
        {
            r = ResolveSummon(key.Substring(SumPrefix.Length));
            if (r == null) return null; // the owner may not be a puppet yet: don't cache the miss
        }
        else if (key.StartsWith(NamePrefix, StringComparison.Ordinal))
            r = ResolveByName(key.Substring(NamePrefix.Length));
        else
            r = ResolveByAddress(key) ?? ResolveOutsideLists(key, msg) ?? ResolveByName(LastSegment(key));

        if (r != null) ClientCache[key] = r;
        return r;
    }

    /// <summary>Issue #6: EnemyPrefabsUtil.FillTierList (0x6E1B80) keeps ONE of the map's variants per tier
    /// (Enemy-Prefabs-Per-Map-Dungeon-Tier-01a..01d), picked with RandomUtils.Range on each machine on its own, and only
    /// preloads that one. So the host's enemy may be in none of our lists, and not loaded either: it was skipped as
    /// unresolved. A list address is a Resources path (FillTierList preloads it with ResourcesUtil.Preload), and the
    /// PrefabData is the entry of the same table FillTierList reads (EnemyPrefabsSO.TryGetPrefabDataByType, 0x4FEE20:
    /// first PrefabDatas entry with that Type, IsElite and IsEndless).</summary>
    private static Resolved? ResolveOutsideLists(string address, in EnemySpawnMsg msg)
    {
        if (!msg.HasPrefabType) return null;
        Entity? prefab = null;
        try { prefab = ResourcesUtil.Load<Entity>(address); } catch { /* not a Resources path */ }
        if (prefab == null) return null;

        EnemyPrefabData? data = null;
        var datas = Global.EnemyPrefabs?.PrefabDatas;
        if (datas != null)
            for (int i = 0; i < datas.Length && data == null; i++)
            {
                var d = datas[i]?.Data;
                if (d != null && (int)d.Type == msg.PrefabType && d.IsElite == msg.DataIsElite && d.IsEndless == msg.DataIsEndless) data = d;
            }
        OutsideListsResolved++;
        Plugin.StaticLog.LogInfo(
            $"[SoulstoneCoop][puppets] '{address}' is not in our tier lists (the host rolled another variant): " +
            $"loaded from Resources, prefabData={(data != null)} (type {msg.PrefabType} elite={msg.DataIsElite} endless={msg.DataIsEndless}).");
        return new Resolved { Prefab = prefab, PrefabData = data };
    }

    /// <summary>"&lt;ownerNetId&gt;:Name": the prefab is in the owner puppet's summon skills (its PossiblePrefabs are
    /// loaded with it), else anything already loaded with that name.</summary>
    private static Resolved? ResolveSummon(string rest)
    {
        int colon = rest.IndexOf(':');
        if (colon <= 0) return null;
        string name = rest.Substring(colon + 1);
        if (ushort.TryParse(rest.Substring(0, colon), out ushort owner))
        {
            var ownerEntity = ClientPuppets.TryGetPuppetEntity(owner);
            if (ownerEntity != null)
            {
                var p = FindInSummonEffects(ownerEntity, name);
                if (p != null) return new Resolved { Prefab = p };
            }
        }
        return ResolveByName(name);
    }

    private static Entity? FindInSummonEffects(Entity owner, string name)
    {
        foreach (var e in owner.GetComponentsInChildren<SummonEntityEffect>(true))
        {
            var p = Match(e.PossiblePrefabs, name);
            if (p != null) return p;
            var seq = e.TryCast<SummonSequenceEffect>();
            if (seq?.AvailablePrefabs != null)
                for (int i = 0; i < seq.AvailablePrefabs.Count; i++)
                    if (seq.AvailablePrefabs[i] != null && StripClone(seq.AvailablePrefabs[i].name) == name) return seq.AvailablePrefabs[i];
        }
        foreach (var e in owner.GetComponentsInChildren<SummonEntityPositionalEffect>(true))
        {
            var p = Match(e.PossiblePrefabs, name);
            if (p != null) return p;
        }
        foreach (var e in owner.GetComponentsInChildren<SummonEntityAndForgetPositionalEffect>(true))
        {
            var p = Match(e.PossiblePrefabs, name);
            if (p != null) return p;
        }
        return null;
    }

    private static Entity? Match(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity>? arr, string name)
    {
        if (arr == null) return null;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] != null && StripClone(arr[i].name) == name) return arr[i];
        return null;
    }

    private static EnemyPrefabData? PrefabDataAt(int index)
    {
        var datas = EnemyPrefabsUtil._prefabDatas;
        if (datas == null || index < 0 || index >= datas.Count) return null;
        return datas[index];
    }

    private static Resolved? ResolveByAddress(string address)
    {
        foreach (var list in SingleLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.PrefabAddress != address) continue;
                var prefab = e.Prefab; // loads it if needed
                if (prefab == null) continue;
                return new Resolved { Prefab = prefab, PrefabData = PrefabDataAt(e.PrefabDataIndex) };
            }
        }
        foreach (var list in GroupLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                var addrs = g.PrefabAddresses;
                if (addrs == null) continue;
                for (int j = 0; j < addrs.Length; j++)
                {
                    if (addrs[j] != address) continue;
                    var prefab = ResourcesUtil.Load<Entity>(address);
                    if (prefab == null) continue;
                    int dataIndex = g.PrefabDataIndex != null && j < g.PrefabDataIndex.Length ? g.PrefabDataIndex[j] : -1;
                    return new Resolved { Prefab = prefab, PrefabData = PrefabDataAt(dataIndex) };
                }
            }
        }
        return null;
    }

    private static Resolved? ResolveByName(string name)
    {
        foreach (var list in SingleLists())
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                var p = e._prefab;
                bool match = (p != null && StripClone(p.name) == name)
                          || (e.PrefabAddress != null && LastSegment(e.PrefabAddress) == name);
                if (!match) continue;
                var prefab = e.Prefab;
                if (prefab != null) return new Resolved { Prefab = prefab, PrefabData = PrefabDataAt(e.PrefabDataIndex) };
            }
        }

        // Anything else already loaded as an asset (prefabs have no valid scene).
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Entity>());
        for (int i = 0; i < all.Length; i++)
        {
            var ent = all[i]?.TryCast<Entity>();
            if (ent == null || ent.gameObject.scene.IsValid()) continue;
            if (ent.name == name) return new Resolved { Prefab = ent };
        }
        return null;
    }
}
