#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.WorldScreen.World
{
    /// <summary>
    /// One placed portal's in-world beacon (summoning-stone look): a thin glowing RING on the ground, a small
    /// tiered PEDESTAL at its centre, a translucent light CONE flaring upward from the pedestal, a star EMBLEM
    /// with the owner name at the cone's top, and a few drifting MOTES. Procedural + shader-safe (world-space
    /// <see cref="Canvas"/> + <see cref="RawImage"/>/<see cref="Text"/> with generated <see cref="Texture2D"/>s
    /// — NO <c>Shader.Find</c>, mirroring the proven WorldScreen render path). The ring stays flat; the
    /// pedestal/cone/emblem/motes billboard toward the player. Iterated in the UI sandbox (story
    /// <c>portal-beacon</c>) so the look is tuned without an in-game relogin.
    /// </summary>
    internal sealed class PortalProp
    {
        private const float RingDiameterM = 5.0f;   // big thin ground ring
        private const float PedestalWM = 0.85f, PedestalHM = 1.05f;
        private const float ConeWTopM = 2.5f, ConeHM = 2.9f;
        private const float ConeBaseYM = 0.55f;      // cone starts atop the pedestal
        private static readonly Color Tint = new Color(0.42f, 0.92f, 1.0f, 1f); // bright cyan
        private const int MoteCount = 4;

        private GameObject? _root;
        private GameObject? _ringGo;   // flat, spun
        private GameObject? _facing;   // billboarded group (pedestal + cone + emblem + motes)
        private GameObject? _emblemGo;
        private RawImage? _ringImg;
        private RawImage? _coneImg;
        private Text? _label;
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

            if (_ringGo != null) _ringGo.transform.localRotation = _flatRot * Quaternion.Euler(0f, 0f, time * 18f);
            if (_ringImg != null) _ringImg.color = Alpha(Tint, 0.78f + 0.22f * Mathf.Sin(time * 2.0f));
            if (_coneImg != null) _coneImg.color = Alpha(Tint, 0.62f + 0.16f * Mathf.Sin(time * 2.4f + 0.6f));
            if (_emblemGo != null)
                _emblemGo.transform.localPosition = new Vector3(0f, ConeBaseYM + ConeHM + 0.45f + 0.06f * Mathf.Sin(time * 1.6f), 0.001f);

            for (int i = 0; i < _motes.Length; i++)
            {
                var m = _motes[i];
                if (m == null) continue;
                float phase = (time * 0.35f + i / (float)_motes.Length) % 1f; // 0..1 rising loop
                float ang = i * 1.9f;
                float rad = 0.5f + 0.25f * Mathf.Sin(time * 0.8f + i);
                m.transform.localPosition = new Vector3(Mathf.Cos(ang) * rad, 0.5f + phase * (ConeHM + 0.6f), Mathf.Sin(ang) * 0.15f);
                var img = m.GetComponent<RawImage>();
                if (img != null) img.color = Alpha(Tint, 0.75f * Mathf.Sin(phase * Mathf.PI)); // fade in/out over the rise
            }
        }

        public void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null; _ringGo = null; _facing = null; _emblemGo = null; _ringImg = null; _coneImg = null; _label = null;
                for (int i = 0; i < _motes.Length; i++) _motes[i] = null;
            }
        }

        private static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("StellarPortalBeacon");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            // Flat ground ring.
            _ringGo = WorldCanvas("Ring", _root.transform, 256, 256, RingDiameterM / 256f);
            _flatRot = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            _ringGo.transform.localRotation = _flatRot;
            _ringImg = _ringGo.AddComponent<RawImage>();
            _ringImg.texture = RingTex();

            // Billboarded group.
            _facing = new GameObject("Facing");
            _facing.transform.SetParent(_root.transform, false);

            // Pedestal at the base.
            var ped = WorldCanvas("Pedestal", _facing.transform, 128, 160, PedestalWM / 128f);
            ped.transform.localPosition = new Vector3(0f, PedestalHM / 2f, 0.002f);
            ped.transform.localScale = new Vector3(PedestalWM / 128f, PedestalHM / 160f, 1f);
            ped.AddComponent<RawImage>().texture = PedestalTex();

            // Flaring light cone from the pedestal top.
            var cone = WorldCanvas("Cone", _facing.transform, 128, 160, ConeWTopM / 128f);
            cone.transform.localPosition = new Vector3(0f, ConeBaseYM + ConeHM / 2f, 0.001f);
            cone.transform.localScale = new Vector3(ConeWTopM / 128f, ConeHM / 160f, 1f);
            _coneImg = cone.AddComponent<RawImage>();
            _coneImg.texture = ConeTex();

            // Star emblem + owner name at the cone top.
            _emblemGo = WorldCanvas("Emblem", _facing.transform, 240, 90, 1.9f / 240f);
            _emblemGo.transform.localPosition = new Vector3(0f, ConeBaseYM + ConeHM + 0.45f, 0.001f);
            var emblemBg = new GameObject("EmblemBg");
            emblemBg.transform.SetParent(_emblemGo.transform, false);
            var ebg = emblemBg.AddComponent<RawImage>();
            ebg.texture = EmblemTex();
            Stretch(ebg.rectTransform);
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

            // Drifting motes.
            for (int i = 0; i < _motes.Length; i++)
            {
                var m = WorldCanvas("Mote" + i, _facing.transform, 24, 24, 0.16f / 24f);
                m.AddComponent<RawImage>().texture = MoteTex();
                _motes[i] = m;
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

        private static Texture2D? _ring, _cone, _ped, _emblem, _mote;

        private static Texture2D RingTex()
        {
            if (_ring != null) return _ring;
            const int N = 256;
            _ring = Build(N, N, (x, y) =>
            {
                float c = (N - 1) / 2f, R = (N / 2f) - 2f;
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / R;
                float line = Mathf.Exp(-Mathf.Pow((d - 0.96f) / 0.022f, 2f)); // thin bright rim
                return d > 1f ? 0f : Mathf.Clamp01(line);
            });
            return _ring;
        }

        private static Texture2D ConeTex()
        {
            if (_cone != null) return _cone;
            const int W = 128, H = 160;
            _cone = Build(W, H, (x, y) =>
            {
                float up = y / (float)(H - 1);                 // 0 base … 1 top
                float half = Mathf.Lerp(0.10f, 0.48f, up);      // flares outward toward the top
                float fx = Mathf.Abs(x / (float)(W - 1) - 0.5f);
                if (fx > half) return 0f;
                float t = fx / half;                            // 0 centre … 1 wall
                float wall = 0.18f + 0.6f * Mathf.Pow(t, 1.7f); // brighter toward the cone walls
                float fade = 0.95f - 0.35f * up;                // slightly fainter near the top
                return Mathf.Clamp01(wall * fade);
            });
            return _cone;
        }

        private static Texture2D PedestalTex()
        {
            if (_ped != null) return _ped;
            const int W = 128, H = 160;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[W * H];
            // three stacked tiers (widest at the bottom), each with a lighter top face + shaded sides.
            (float y0, float y1, float half)[] tiers =
            {
                (0.00f, 0.34f, 0.46f),
                (0.34f, 0.64f, 0.34f),
                (0.64f, 0.92f, 0.23f),
            };
            var stone = new Color(0.34f, 0.60f, 0.70f);      // blue-grey stone
            var lip   = new Color(0.62f, 0.90f, 1.00f);      // lit top edge of each tier
            var gemC  = new Color(0.70f, 0.98f, 1.00f);      // bright gem
            for (int y = 0; y < H; y++)
            {
                float up = y / (float)(H - 1);
                for (int x = 0; x < W; x++)
                {
                    float fx = (x / (float)(W - 1)) - 0.5f;   // -0.5..0.5
                    Color c = new Color(0, 0, 0, 0);
                    foreach (var tr in tiers)
                    {
                        if (up < tr.y0 || up > tr.y1) continue;
                        if (Mathf.Abs(fx) > tr.half) continue;
                        float side = 1f - 0.45f * Mathf.Abs(fx) / tr.half;   // darker toward the sides (round)
                        float topLip = up > tr.y1 - 0.05f ? 1f : 0f;         // lit lip at the tier top
                        c = Color.Lerp(stone * side, lip, topLip * 0.8f);
                        c.a = 1f;
                    }
                    // gem set into the top tier
                    float gd = fx * fx + (up - 0.80f) * (up - 0.80f);
                    float gem = Mathf.Exp(-gd / 0.0022f);
                    if (gem > 0.02f) c = Color.Lerp(c, gemC, Mathf.Clamp01(gem));
                    px[y * W + x] = c;
                }
            }
            t.SetPixels(px);
            t.Apply(false);
            _ped = t;
            return _ped;
        }

        private static Texture2D EmblemTex()
        {
            if (_emblem != null) return _emblem;
            const int W = 240, H = 90;
            _emblem = Build(W, H, (x, y) =>
            {
                // a soft rounded translucent banner
                float fx = Mathf.Abs(x / (float)(W - 1) - 0.5f) * 2f; // 0..1
                float fy = Mathf.Abs(y / (float)(H - 1) - 0.5f) * 2f;
                float d = Mathf.Max(fx, fy * 0.9f);
                float body = Mathf.Clamp01(1f - Mathf.Pow(d, 4f)) * 0.42f;
                float rim = Mathf.Exp(-Mathf.Pow((d - 0.92f) / 0.05f, 2f)) * 0.7f;
                return Mathf.Clamp01(body + rim);
            });
            return _emblem;
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
