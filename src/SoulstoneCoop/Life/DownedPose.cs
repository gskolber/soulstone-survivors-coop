using SoulstoneCoop.Lobby;
using UnityEngine;

namespace SoulstoneCoop.Life;

/// <summary>
/// 1.6.0 (docs/caido-marcadores-notas.md): a downed player lies on the ground on every screen. Since 1.4.1 there's no
/// ragdoll any more (its bones fell through some maps), so the body stood still. Now, while down, the character model
/// (the transform that holds its Animator: ours from ModifiedCharacter.GetAnimator, a partner's ghost from
/// RemotePlayer.ModelAnimator) is turned onto its back, lifted a little so it doesn't sink into the ground, and its
/// animation is frozen. Re-applied every frame (an ascension model swap or the ghost's locomotion could touch it),
/// undone exactly on revive or when the player leaves. Purely visual: positions, hitboxes and the revive are untouched.
/// </summary>
public static class DownedPose
{
    private const float Lift = 0.3f;
    private static readonly Quaternion OnTheBack = Quaternion.Euler(-90f, 0f, 0f);

    private sealed class Laid
    {
        public Animator A = null!;
        public Transform T = null!;
        public Quaternion Rot;
        public Vector3 Pos;
        public float Speed;
        public bool Used;
    }

    private static readonly Dictionary<IntPtr, Laid> ByAnimator = new();
    private static readonly List<IntPtr> Drop = new();
    private static int _errors;

    /// <param name="active">In a co-op match and the run isn't ending (CoopLife.Tick).</param>
    public static void Tick(bool active)
    {
        try
        {
            foreach (var l in ByAnimator.Values) l.Used = false;
            if (active)
            {
                if (CoopLife.LocalDowned)
                {
                    var local = GameState.LocalPlayerEntity;
                    Animator? a = null;
                    try { a = local != null && local._character != null ? local._character.GetAnimator() : null; } catch { }
                    if (a != null) Lay(a);
                }
                foreach (var r in CoopSession.Remotes)
                    if (r.Ghost != null && r.ModelAnimator != null && CoopLife.IsRemoteDown(r.Slot)) Lay(r.ModelAnimator);
            }
            Drop.Clear();
            foreach (var kv in ByAnimator)
            {
                if (kv.Value.Used) continue;
                Restore(kv.Value);
                Drop.Add(kv.Key);
            }
            foreach (var k in Drop) ByAnimator.Remove(k);
        }
        catch (Exception ex)
        {
            if (_errors++ < 5) Plugin.StaticLog.LogWarning($"[SoulstoneCoop][life] Downed pose: {ex.Message}");
        }
    }

    private static void Lay(Animator a)
    {
        if (!ByAnimator.TryGetValue(a.Pointer, out var l))
        {
            var t = a.transform;
            l = new Laid { A = a, T = t, Rot = t.localRotation, Pos = t.localPosition, Speed = a.speed };
            ByAnimator[a.Pointer] = l;
        }
        l.Used = true;
        if (l.T == null || l.A == null) return;
        var want = l.Rot * OnTheBack;
        if (l.T.localRotation != want) l.T.localRotation = want;
        var pos = l.Pos + Vector3.up * Lift;
        if (l.T.localPosition != pos) l.T.localPosition = pos;
        if (l.A.speed != 0f) l.A.speed = 0f;
    }

    private static void Restore(Laid l)
    {
        try
        {
            if (l.T != null) { l.T.localRotation = l.Rot; l.T.localPosition = l.Pos; }
            if (l.A != null) l.A.speed = l.Speed;
        }
        catch { /* the model is gone with its scene */ }
    }
}
