using UnityEngine;

namespace SoulstoneCoop.Lobby;

/// <summary>
/// Phase 9: one of the other players, as this side sees it. Created when the slot joins (Net.Players.Joined) and
/// dropped after every module cleaned its own per-player state (Players.Forgotten). Holds what several modules
/// share: who it is (Hello), where it is (LobbyState) and its ghost. Module-specific state (interpolation, proxy,
/// remote caster, health bar...) lives in each module, keyed by the same slot.
/// </summary>
public sealed class RemotePlayer
{
    public readonly byte Slot;
    public PlayerInfo Info = new();
    public LobbyPlace Place = LobbyPlace.Menu;

    // ---- ghost (CoopBehaviour / VisualGhostFactory) ----
    public GameObject? Ghost;
    /// <summary>The identity the current ghost was built for (VisualGhostFactory.KeyFor(Info) at build time).</summary>
    public string GhostKey = "";
    /// <summary>The ghost shows this player's own character (false: the local-clone fallback).</summary>
    public bool GhostFromRemote;
    /// <summary>The character model's own Animator on the current ghost (locomotion, cast CrossFade, rebuild check).</summary>
    public Animator? ModelAnimator;
    public int GhostRebuilds, IdentityRebuilds;
    /// <summary>The skin id the ghost's character model was built from (AscensionSync builds the other models with it).</summary>
    public string GhostSkinId = "";
    /// <summary>Bumped whenever ModelAnimator changes (new ghost, or AscensionSync swapped the model): whoever caches the
    /// ghost's animators (PlayerSync, RemoteCaster) takes them again.</summary>
    public int ModelVersion;

    // ---- positions (PlayerSync) ----
    /// <summary>Positions received for the current ghost (0 = none yet: the proxy and the remote caster wait for one).</summary>
    public int PositionsReceived;
    public Vector3 LastPosition;

    public RemotePlayer(byte slot) => Slot = slot;

    public bool IsHost => Slot == Net.Players.HostSlot;
    public bool InRun => Place == LobbyPlace.InRun;
    public bool ChangingScene => Place == LobbyPlace.Loading || Place == LobbyPlace.Transition;

    public string Name => Info.Known && Info.Name.Length > 0 ? Info.Name : FallbackName(Slot);

    /// <summary>"Host" / "Player 3" (local language) until that player's Hello brings its Steam name.</summary>
    public static string FallbackName(byte slot) =>
        slot == Net.Players.HostSlot ? Ui.Loc.T("name.host") : Ui.Loc.T("name.player", ("n", slot + 1));

    /// <summary>Where this player is: its ghost, once a position of this run placed it.</summary>
    public bool TryGetPosition(out Vector3 pos, out Quaternion rot)
    {
        var g = Ghost;
        if (g == null || PositionsReceived == 0)
        {
            pos = default; rot = default;
            return false;
        }
        var t = g.transform;
        pos = t.position;
        rot = t.rotation;
        return true;
    }
}
