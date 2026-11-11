using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.Rendering;

namespace SoulstoneCoop.Life;

/// <summary>
/// What marks a downed player on every screen (docs/caido-marcadores-notas.md):
/// - a ring on the ground with the revive radius (CoopLife.ReviveRadius) around the body, pulsing red, and a green arc
///   over it that fills with the revive progress (CoopLife.ProgressOf, the host's count);
/// - a line from the body to the nearest living player (who is coming), and, on a living player's screen, a line
///   from each downed ally to us, so a body off screen can still be found.
/// The local downed player sees the same: its own ring and the lines to the allies.
/// Plain LineRenderers of our own (no Entity, no collider), under one object kept across scenes; a material from the
/// first shader the build has (Sprites/Default, URP unlit), else a copy of a trail's material. If none can be made,
/// the markers turn off for good and the floating texts (CoopLife.FeedbackTick) are what's left.
/// </summary>
public static class DownedMarkers
{
    private const int RingSegments = 64;
    private const float RingWidth = 0.24f, ProgressWidth = 0.34f, LinkWidth = 0.18f; // 1.4.1: thicker, easier to spot in a crowd
    private const float BeaconHeight = 9f, BeaconWidth = 0.55f; // 1.4.1: a pulsing pillar over the body
    private const float GroundLift = 0.15f;
    private static readonly Color DownRed = new(0.95f, 0.28f, 0.22f, 1f);
    private static readonly Color ReviveGreen = new(0.35f, 1f, 0.5f, 1f);

    private sealed class Ring
    {
        public LineRenderer? Circle, Arc, Beacon;
        public bool Used;
    }

    private sealed class Link
    {
        public LineRenderer? Line;
        public bool Used;
    }

    private struct Spot { public byte Slot; public Vector3 Pos; }

    private static GameObject? _root;
    private static Material? _material;
    private static bool _vertexColors;
    private static bool _failed;
    private static readonly Dictionary<byte, Ring> Rings = new();
    private static readonly Dictionary<int, Link> Links = new();
    private static readonly List<Spot> Down = new();
    private static readonly List<Spot> Living = new();
    private static readonly Vector3[] RingPoints = new Vector3[RingSegments];
    private static readonly Vector3[] ArcPoints = new Vector3[RingSegments + 1];

    /// <param name="active">In a co-op match and the run isn't ending (CoopLife.Tick).</param>
    public static void Tick(bool active)
    {
        if (_failed) return;
        try { TickCore(active && !Ui.Cutscene.Running); } // 1.9.0: hidden during boss cutscenes
        catch (Exception ex)
        {
            _failed = true;
            Plugin.StaticLog.LogError($"[SoulstoneCoop][life] Downed markers turned off: {ex}");
            try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
            _root = null;
            Rings.Clear();
            Links.Clear();
        }
    }

    private static void TickCore(bool active)
    {
        Down.Clear();
        Living.Clear();
        if (active) Collect();
        if (Down.Count == 0)
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            return;
        }
        if (!EnsureRoot()) return;
        if (!_root!.activeSelf) _root.SetActive(true);

        foreach (var r in Rings.Values) r.Used = false;
        foreach (var l in Links.Values) l.Used = false;

        float pulse = 0.55f + 0.4f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5f));
        foreach (var d in Down)
        {
            var ring = RingFor(d.Slot);
            ring.Used = true;
            DrawRing(ring, d.Pos, CoopLife.ProgressOf(d.Slot), pulse);
            DrawBeacon(ring, d.Pos, pulse);

            // The nearest living player: most likely the one coming to revive.
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < Living.Count; i++)
            {
                float sq = Flat(Living[i].Pos - d.Pos);
                if (sq < bestD) { bestD = sq; best = i; }
            }
            if (best >= 0) DrawLink(d, Living[best]);
            // On a living player's screen: every downed ally is tied to us too.
            foreach (var l in Living)
                if (l.Slot == Players.MySlot) { DrawLink(d, l); break; }
        }

        foreach (var r in Rings.Values)
        {
            if (r.Used) continue;
            SetOn(r.Circle, false);
            SetOn(r.Arc, false);
            SetOn(r.Beacon, false);
        }
        foreach (var l in Links.Values) if (!l.Used) SetOn(l.Line, false);
    }

    private static void Collect()
    {
        var local = GameState.LocalPlayerEntity;
        if (local != null)
        {
            if (CoopLife.LocalDowned) Down.Add(new Spot { Slot = Players.MySlot, Pos = local.transform.position });
            else if (!CoopLife.LocalOut) Living.Add(new Spot { Slot = Players.MySlot, Pos = local.transform.position });
        }
        foreach (var r in CoopSession.Remotes)
        {
            var ghost = r.Ghost;
            if (ghost == null || !r.InRun) continue;
            var spot = new Spot { Slot = r.Slot, Pos = ghost.transform.position };
            // 1.4.1: a body that sank through the ground on its owner's machine (older builds) is drawn at our height.
            if (local != null && spot.Pos.y < local.transform.position.y - 3f)
                spot.Pos = new Vector3(spot.Pos.x, local.transform.position.y, spot.Pos.z);
            if (CoopLife.IsRemoteDown(r.Slot)) Down.Add(spot);
            else Living.Add(spot);
        }
    }

    private static float Flat(Vector3 v) => v.x * v.x + v.z * v.z;

    // ---------------- drawing ----------------

    private static void DrawRing(Ring ring, Vector3 center, float progress, float pulse)
    {
        float radius = CoopLife.ReviveRadius;
        float y = center.y + GroundLift;
        for (int i = 0; i < RingSegments; i++)
        {
            float a = i * Mathf.PI * 2f / RingSegments;
            RingPoints[i] = new Vector3(center.x + Mathf.Sin(a) * radius, y, center.z + Mathf.Cos(a) * radius);
        }
        var circle = ring.Circle!;
        circle.positionCount = RingSegments;
        for (int i = 0; i < RingSegments; i++) circle.SetPosition(i, RingPoints[i]);
        var c = DownRed;
        c.a = pulse;
        Paint(circle, c, c);
        SetOn(circle, true);

        var arc = ring.Arc!;
        if (progress <= 0.01f) { SetOn(arc, false); return; }
        int n = Mathf.Clamp(Mathf.CeilToInt(RingSegments * progress), 2, RingSegments) + 1;
        float r2 = radius;
        for (int i = 0; i < n; i++)
        {
            float a = progress * Mathf.PI * 2f * i / (n - 1);
            ArcPoints[i] = new Vector3(center.x + Mathf.Sin(a) * r2, y + 0.01f, center.z + Mathf.Cos(a) * r2);
        }
        arc.positionCount = n;
        for (int i = 0; i < n; i++) arc.SetPosition(i, ArcPoints[i]);
        Paint(arc, ReviveGreen, ReviveGreen);
        SetOn(arc, true);
    }

    private static void DrawBeacon(Ring ring, Vector3 center, float pulse)
    {
        var beam = ring.Beacon!;
        beam.positionCount = 2;
        beam.SetPosition(0, center + Vector3.up * GroundLift);
        beam.SetPosition(1, center + Vector3.up * BeaconHeight);
        var bottom = DownRed;
        bottom.a = 0.35f + 0.5f * pulse;
        var top = DownRed;
        top.a = 0f;
        Paint(beam, bottom, top);
        SetOn(beam, true);
    }

    private static void DrawLink(Spot down, Spot living)
    {
        if (down.Slot == living.Slot) return;
        int key = (down.Slot << 8) | living.Slot;
        if (!Links.TryGetValue(key, out var link) || link.Line == null)
        {
            link = new Link { Line = MakeLine($"Link_{down.Slot}_{living.Slot}", LinkWidth, flat: false) };
            Links[key] = link;
        }
        if (link.Used) return;
        link.Used = true;
        var line = link.Line!;
        line.positionCount = 2;
        line.SetPosition(0, down.Pos + Vector3.up * (GroundLift + 0.2f));
        line.SetPosition(1, living.Pos + Vector3.up * (GroundLift + 0.2f));
        var start = DownRed;
        start.a = 0.9f;
        var end = GhostOverhead.ColorFor(living.Slot);
        end.a = 0.75f;
        Paint(line, start, end);
        SetOn(line, true);
    }

    private static Ring RingFor(byte slot)
    {
        if (Rings.TryGetValue(slot, out var r) && r.Circle != null && r.Arc != null && r.Beacon != null) return r;
        r = new Ring
        {
            Circle = MakeLine($"Ring_{slot}", RingWidth, flat: true, loop: true),
            Arc = MakeLine($"Revive_{slot}", ProgressWidth, flat: true),
            Beacon = MakeLine($"Beacon_{slot}", BeaconWidth, flat: false),
        };
        Rings[slot] = r;
        return r;
    }

    private static void SetOn(LineRenderer? lr, bool on)
    {
        if (lr != null && lr.enabled != on) lr.enabled = on;
    }

    private static void Paint(LineRenderer lr, Color start, Color end)
    {
        lr.startColor = start;
        lr.endColor = end;
        if (_vertexColors) return; // Sprites/Default multiplies the vertex colors by a white _Color
        var m = lr.material;
        if (m == null) return;
        var avg = Color.Lerp(start, end, 0.5f);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", avg);
        if (m.HasProperty("_Color")) m.SetColor("_Color", avg);
    }

    // ---------------- building ----------------

    /// <param name="flat">On the ground (the width spreads horizontally) instead of facing the camera.</param>
    private static LineRenderer MakeLine(string name, float width, bool flat, bool loop = false)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root!.transform, false);
        if (flat) go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f); // its Z points up: TransformZ lies flat
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.loop = loop;
        lr.widthMultiplier = width;
        lr.numCapVertices = flat ? 0 : 2;
        lr.numCornerVertices = 2;
        lr.alignment = flat ? LineAlignment.TransformZ : LineAlignment.View;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.sharedMaterial = _material;
        if (!_vertexColors) lr.material = new Material(_material); // its own color
        lr.enabled = false;
        return lr;
    }

    private static bool EnsureRoot()
    {
        if (_root != null) return true;
        _material ??= FindMaterial(out _vertexColors);
        if (_material == null)
        {
            _failed = true;
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][life] Downed markers: no usable line material in this build - only the floating texts mark a downed player.");
            return false;
        }
        _root = new GameObject("SoulstoneCoop_DownedMarkers");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        Rings.Clear();
        Links.Clear();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][life] Downed markers ready (material '{_material.name}', shader '{(_material.shader != null ? _material.shader.name : "?")}', revive radius {CoopLife.ReviveRadius} u).");
        return true;
    }

    /// <summary>The line material (PassiveObjectSync's stand-in markers use it too); null if the build has none.</summary>
    internal static Material? LineMaterial(out bool vertexColors)
    {
        _material ??= FindMaterial(out _vertexColors);
        vertexColors = _vertexColors;
        return _material;
    }

    private static Material? FindMaterial(out bool vertexColors)
    {
        vertexColors = false;
        foreach (var name in new[] { "Sprites/Default", "Universal Render Pipeline/Particles/Unlit", "Universal Render Pipeline/Unlit", "Unlit/Color" })
        {
            try
            {
                var sh = Shader.Find(name);
                if (sh == null) continue;
                vertexColors = name == "Sprites/Default" || name.Contains("Particles");
                var m = new Material(sh) { name = "SoulstoneCoop_DownedMarker" };
                if (vertexColors && m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
                if (vertexColors && m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
                return m;
            }
            catch { /* next */ }
        }
        try
        {
            // Last resort: a trail of the game's own (dashes, projectiles): vertex-colored by design.
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TrailRenderer>(true))
            {
                var src = t != null ? t.sharedMaterial : null;
                if (src == null) continue;
                vertexColors = true;
                return new Material(src) { name = "SoulstoneCoop_DownedMarker" };
            }
        }
        catch { /* none */ }
        return null;
    }
}
