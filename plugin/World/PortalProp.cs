#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.WorldScreen.World
{
    /// <summary>
    /// One placed portal's in-world beacon (summoning-stone look): a genuine 3D RING of light standing up off
    /// the ground (a cylinder wall of vertical panels) over a faint flat floor glyph, a small tiered PEDESTAL at
    /// its centre, a translucent light CONE flaring upward from the pedestal, a star EMBLEM with the owner name
    /// at the cone's top, and a few drifting MOTES. Procedural + shader-safe (world-space <see cref="Canvas"/> +
    /// <see cref="RawImage"/>/<see cref="Text"/> with generated <see cref="Texture2D"/>s — NO <c>Shader.Find</c>,
    /// mirroring the proven WorldScreen render path; the <c>UI/Default</c> shader is <c>Cull Off</c>, so the wall
    /// panels read from every angle). The 3D ring spins about its vertical axis; the pedestal/cone/emblem/motes
    /// billboard toward the player. Iterated in the UI sandbox (story <c>portal-beacon</c>) so the look is tuned
    /// without an in-game relogin.
    /// </summary>
    internal sealed partial class PortalProp
    {
        private const float RingDiameterM = 4.2f;   // ground ring (smaller, tighter)
        private const float RingRadiusM = RingDiameterM / 2f;
        private const int RingSegments = 40;         // many vertical panels → a smooth circle wall
        private const float WallHeightM = 0.55f;     // how tall the ring wall rises off the ground

        // 3D pedestal — three stacked stone drums (real side faces + real top caps), then a flared 3D cone.
        private const int TierSides = 8;             // faces per drum (rounder = more)
        private const float PedTopYM = 0.82f;        // top of the pedestal (cone starts here)
        private const float ConeTopRM = 0.85f;       // cone glow half-width up top (narrower beacon)
        private const float ConeHM = 1.8f;           // cone height above the pedestal top (shorter beacon)
        private const float NameYM = PedTopYM + 0.7f; // name floats low, near the pedestal (not up high)
        private const float NameWorldWidthM = 1.15f;  // name's fixed WORLD width — stays readable at any BeaconScale

        private const float BeaconScale = 0.34f;     // uniform shrink of the whole beacon (fits a ~1.4m ring)
        private static readonly Color Tint = new Color(0.42f, 0.92f, 1.0f, 1f); // bright cyan
        private const int MoteCount = 4;

        private GameObject? _root;
        private GameObject? _floorGo;   // faint flat floor glyph under the wall
        private GameObject? _ringGroup; // the 3D cylinder wall of light, spun about Y
        private GameObject? _pedestalGo;// the 3D stone pedestal (fixed geometry, not billboarded)
        private GameObject? _facing;    // billboarded group (cone glow + name + motes)
        private GameObject? _emblemGo;
        private RawImage? _floorImg;
        private RawImage? _coneImg;
        private RawImage? _gemImg;
        private Text? _label;
        private readonly List<RawImage> _wallPanels = new List<RawImage>(RingSegments);
        private readonly GameObject?[] _motes = new GameObject?[MoteCount];
        private Quaternion _flatRot = Quaternion.identity;

        public void Place(Vector3 pos, string text)
        {
            EnsureCreated();
            _root!.transform.position = pos + Vector3.up * 0.04f;
            if (_label != null) _label.text = text;
        }

        /// <summary>Per-frame: billboard the upright parts, spin/breathe the ring, breathe the cone, bob the
        /// emblem, drift the motes. <paramref name="time"/> is a seconds clock (e.g. <c>Time.time</c>).</summary>
        public void Tick(Camera? cam, float time)
        {
            if (_root == null) return;

            if (_facing != null && cam != null)
            {
                var dir = _facing.transform.position - cam.transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 1e-4f)
                    _facing.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            }

            if (_ringGroup != null) _ringGroup.transform.localRotation = Quaternion.Euler(0f, time * 22f, 0f);
            if (_wallPanels.Count > 0)
            {
                float wallA = 0.72f + 0.20f * Mathf.Sin(time * 2.0f);
                for (int i = 0; i < _wallPanels.Count; i++)
                    if (_wallPanels[i] != null) _wallPanels[i].color = Alpha(Tint, wallA);
            }
            if (_floorImg != null) _floorImg.color = Alpha(Tint, 0.32f + 0.12f * Mathf.Sin(time * 2.0f));

            // The soft cone glow just breathes (it billboards with the _facing group).
            if (_coneImg != null) _coneImg.color = Alpha(Tint, 0.50f + 0.14f * Mathf.Sin(time * 2.4f + 0.6f));
            if (_gemImg != null) _gemImg.color = Alpha(new Color(0.75f, 0.98f, 1f, 1f), 0.7f + 0.3f * Mathf.Sin(time * 3.0f));

            if (_emblemGo != null)
                _emblemGo.transform.localPosition = new Vector3(0f, NameYM + 0.05f * Mathf.Sin(time * 1.6f), 0.001f);

            for (int i = 0; i < _motes.Length; i++)
            {
                var m = _motes[i];
                if (m == null) continue;
                float phase = (time * 0.35f + i / (float)_motes.Length) % 1f; // 0..1 rising loop
                float ang = i * 1.9f;
                float rad = 0.5f + 0.25f * Mathf.Sin(time * 0.8f + i);
                m.transform.localPosition = new Vector3(Mathf.Cos(ang) * rad, PedTopYM + phase * (ConeHM + 0.4f), Mathf.Sin(ang) * 0.15f);
                var img = m.GetComponent<RawImage>();
                if (img != null) img.color = Alpha(Tint, 0.75f * Mathf.Sin(phase * Mathf.PI)); // fade in/out over the rise
            }
        }

        public void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null; _floorGo = null; _ringGroup = null; _pedestalGo = null;
                _facing = null; _emblemGo = null;
                _floorImg = null; _coneImg = null; _gemImg = null; _label = null;
                _wallPanels.Clear();
                for (int i = 0; i < _motes.Length; i++) _motes[i] = null;
            }
        }

        private static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("StellarPortalBeacon");
            _root.transform.localScale = Vector3.one * BeaconScale; // shrink the whole beacon uniformly
            UnityEngine.Object.DontDestroyOnLoad(_root);

            // Faint flat floor glyph, lying in the ground plane, under the 3D wall.
            _floorGo = WorldCanvas("Floor", _root.transform, 256, 256, RingDiameterM / 256f);
            _flatRot = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            _floorGo.transform.localRotation = _flatRot;
            _floorImg = _floorGo.AddComponent<RawImage>();
            _floorImg.texture = FloorTex();

            // Genuine 3D ring: a cylinder wall of vertical light panels standing off the ground. Each panel is a
            // world-space RawImage (UI/Default shader is Cull Off, so it reads from both sides — shader-safe in
            // the sandbox AND the game). The whole group spins about Y in Tick.
            BuildRingWall();

            // Genuine 3D stone pedestal (fixed — real side faces + top caps you see from above, NOT a billboard).
            BuildPedestal3D(_root.transform);

            // Billboarded group — the light cone, name and motes turn to face the player.
            _facing = new GameObject("Facing");
            _facing.transform.SetParent(_root.transform, false);

            // Soft light cone rising from the pedestal: a SINGLE billboarded glow (not panels), so the beam reads
            // smoothly from every angle. Set slightly back (+z is away from camera after the billboard) so the gem
            // and name draw in front of it.
            var cone = WorldCanvas("Cone", _facing.transform, 96, 128, 1f);
            cone.transform.localPosition = new Vector3(0f, PedTopYM + ConeHM / 2f, 0.05f);
            cone.transform.localScale = new Vector3((2f * ConeTopRM) / 96f, ConeHM / 128f, 1f);
            _coneImg = cone.AddComponent<RawImage>();
            _coneImg.texture = ConeGlowTex();
            _coneImg.color = Alpha(Tint, 0.55f);

            // Glowing gem orb on the pedestal top (on the Y axis, so it stays centred as the group billboards).
            var gem = WorldCanvas("Gem", _facing.transform, 40, 40, 0.42f / 40f);
            gem.transform.localPosition = new Vector3(0f, PedTopYM + 0.14f, 0f);
            _gemImg = gem.AddComponent<RawImage>();
            _gemImg.texture = GemTex();

            // Owner name (NO background plate) floating low near the pedestal; outlined so it stays readable
            // over the light. Only the text billboards.
            _emblemGo = WorldCanvas("Name", _facing.transform, 240, 90, 1.9f / 240f);
            _emblemGo.transform.localPosition = new Vector3(0f, NameYM, 0.001f);
            // Counter the beacon shrink so the name keeps a fixed readable world size (240px wide canvas).
            _emblemGo.transform.localScale = Vector3.one * (NameWorldWidthM / (240f * BeaconScale));
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(_emblemGo.transform, false);
            _label = labelGo.AddComponent<Text>();
            _label.font = ResolveFont();
            _label.alignment = TextAnchor.MiddleCenter;
            _label.horizontalOverflow = HorizontalWrapMode.Overflow;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.color = Color.white;
            _label.fontSize = 26;
            Stretch(_label.rectTransform);
            var outline = labelGo.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0.06f, 0.10f, 0.9f);   // dark halo for legibility
            outline.effectDistance = new Vector2(1.6f, -1.6f);

            // Drifting motes.
            for (int i = 0; i < _motes.Length; i++)
            {
                var m = WorldCanvas("Mote" + i, _facing.transform, 24, 24, 0.16f / 24f);
                m.AddComponent<RawImage>().texture = MoteTex();
                _motes[i] = m;
            }
        }

        // Builds the 3D ring: RingSegments vertical light panels evenly around a circle of RingRadiusM, each
        // standing WallHeightM tall with its face pointing radially outward. Parented under a group that Tick
        // spins about Y. Each panel's UI/Default material is Cull Off, so the wall reads from every viewing angle.
        private void BuildRingWall()
        {
            _ringGroup = new GameObject("RingWall");
            _ringGroup.transform.SetParent(_root!.transform, false);

            float arc = 2f * Mathf.PI * RingRadiusM / RingSegments;
            float panelW = arc * 1.35f;   // overlap blends the panels into a smooth continuous ring
            var wallTex = WallTex();
            for (int i = 0; i < RingSegments; i++)
            {
                float ang = i * (2f * Mathf.PI / RingSegments);
                var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                var panel = WorldCanvas("Wall" + i, _ringGroup.transform, 64, 64, 1f);
                panel.transform.localPosition = dir * RingRadiusM + Vector3.up * (WallHeightM / 2f);
                panel.transform.localRotation = Quaternion.LookRotation(dir, Vector3.up);
                panel.transform.localScale = new Vector3(panelW / 64f, WallHeightM / 64f, 1f);
                var img = panel.AddComponent<RawImage>();
                img.texture = wallTex;
                img.color = Alpha(Tint, 0.72f);
                _wallPanels.Add(img);
            }
        }

        // A world-space canvas child sized w×h pixels, scaled to metres (uniform unless overridden after).
        private static GameObject WorldCanvas(string name, Transform parent, int w, int h, float scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(w, h);
            go.transform.localScale = Vector3.one * scale;
            return go;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        // ---- procedural textures (built once, shared) ----

        private static Texture2D? _floor, _wall, _cone, _mote;

        // The faint flat glyph on the ground under the 3D wall: an outer rim, a faint inner ring, and a soft
        // disc glow — reads as a summoning circle that the light wall rises from.
        private static Texture2D FloorTex()
        {
            if (_floor != null) return _floor;
            const int N = 256;
            _floor = Build(N, N, (x, y) =>
            {
                float c = (N - 1) / 2f, R = (N / 2f) - 2f;
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / R;
                if (d > 1f) return 0f;
                float rim = Mathf.Exp(-Mathf.Pow((d - 0.88f) / 0.060f, 2f));         // soft, wide outer rim (pulled in)
                float inner = Mathf.Exp(-Mathf.Pow((d - 0.58f) / 0.030f, 2f)) * 0.4f; // faint inner ring
                float fill = (1f - d) * 0.08f;                                        // soft disc glow
                float outerFade = Mathf.Clamp01((1f - d) / 0.18f);                    // long taper to nothing at the edge
                return Mathf.Clamp01((rim + inner + fill) * outerFade);
            });
            return _floor;
        }

        // One vertical light panel of the 3D ring wall: brightest at the foot (a crisp base line + a rising
        // glow), fading upward, with softened side edges so adjacent panels blend into a continuous curtain.
        private static Texture2D WallTex()
        {
            if (_wall != null) return _wall;
            const int W = 64, H = 64;
            _wall = Build(W, H, (x, y) =>
            {
                float up = y / (float)(H - 1);                                   // 0 base … 1 top
                float fx = Mathf.Abs(x / (float)(W - 1) - 0.5f) * 2f;            // 0 centre … 1 edge
                float rise = Mathf.Exp(-up * 2.4f);                              // bright at the ground, fading up
                float baseLine = Mathf.Exp(-Mathf.Pow((up - 0.05f) / 0.06f, 2f)); // soft bright line at the foot
                float edge = Mathf.Exp(-Mathf.Pow(fx * 1.15f, 2f));            // soft Gaussian sides → seamless ring
                float topFade = Mathf.Clamp01((1f - up) / 0.3f);              // fade the top 30% to zero → no hard top edge
                return Mathf.Clamp01(edge * (0.8f * rise + 0.85f * baseLine) * topFade);
            });
            return _wall;
        }

        // The soft light-cone glow (one billboarded quad): a bright core at the pedestal foot that flares out and
        // fades toward the top, with Gaussian sides — a smooth beam with no panels/streaks from any angle.
        private static Texture2D ConeGlowTex()
        {
            if (_cone != null) return _cone;
            const int W = 96, H = 128;
            _cone = Build(W, H, (x, y) =>
            {
                float up = y / (float)(H - 1);                       // 0 foot … 1 top
                float fx = Mathf.Abs(x / (float)(W - 1) - 0.5f);      // 0 axis … 0.5 quad side
                float half = Mathf.Lerp(0.10f, 0.40f, up);           // flares out but stays inside the quad (< 0.5)
                float t = fx / half;                                  // 0 axis … 1 edge of the flare
                float radial = Mathf.Exp(-Mathf.Pow(t * 1.7f, 2f));  // soft core → ~0 well before the quad side
                float vert = Mathf.Lerp(0.95f, 0f, Mathf.Pow(up, 0.75f)); // bright foot → fully 0 at the top (no edge)
                return Mathf.Clamp01(radial * vert);
            });
            return _cone;
        }

        private static Texture2D MoteTex()
        {
            if (_mote != null) return _mote;
            const int N = 24;
            _mote = Build(N, N, (x, y) =>
            {
                float c = (N - 1) / 2f, R = N / 2f;
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / R;
                return Mathf.Clamp01(Mathf.Exp(-Mathf.Pow(d / 0.5f, 2f)));
            });
            return _mote;
        }

        private static Texture2D Build(int w, int h, Func<int, int, float> alpha, Color? baseColor = null)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var col = baseColor ?? Tint;
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = new Color(col.r, col.g, col.b, alpha(x, y));
            t.SetPixels(px);
            t.Apply(false);
            return t;
        }

        private static Font? ResolveFont()
        {
            Font? f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { } }
            if (f == null) { try { var all = Resources.FindObjectsOfTypeAll<Font>(); if (all != null && all.Length > 0) f = all[0]; } catch (Exception) { } }
            if (f == null) { try { f = Font.CreateDynamicFontFromOSFont("Arial", 16); } catch (Exception) { } }
            return f;
        }
    }
}
