using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.WorldScreen.World
{
    /// <summary>
    /// One placed portal's in-world beacon: a flat glowing RING on the ground + a vertical light BEAM rising
    /// from it (billboarded toward the player) + a small owner-name tag. Procedural + shader-safe (world-space
    /// <see cref="Canvas"/> + <see cref="RawImage"/> with generated <see cref="Texture2D"/>s — NO
    /// <c>Shader.Find</c>, mirroring the proven WorldScreen render path and the procedural-texture approach in
    /// <c>docs/rendering-images-in-game.md</c>). Walk-up activation (raising the AVPro screen) is a later increment.
    /// </summary>
    internal sealed class PortalProp
    {
        private const float RingDiameterM = 2.6f;   // ground ring diameter
        private const float BeamWidthM = 0.55f;     // beam width
        private const float BeamHeightM = 5.0f;     // beam height
        private static readonly Color PortalTint = new Color(0.30f, 0.80f, 1.0f, 1f); // cyan

        private GameObject? _root;   // at the ground position
        private GameObject? _facing; // holds the beam + label, billboarded toward the player
        private Text? _label;

        /// <summary>Creates the beacon (once) at ground <paramref name="pos"/> with the owner-name text.</summary>
        public void Place(Vector3 pos, string text)
        {
            EnsureCreated();
            _root!.transform.position = pos + Vector3.up * 0.05f; // just above the ground (avoid z-fight)
            if (_label != null) _label.text = text;
        }

        /// <summary>Faces the beam + name tag toward the player horizontally (the ground ring stays flat).
        /// Call per frame.</summary>
        public void Billboard(Camera? cam)
        {
            if (_facing == null || cam == null) return;
            var dir = _facing.transform.position - cam.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return;
            _facing.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        public void Destroy()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; _facing = null; _label = null; }
        }

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("StellarPortalBeacon");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            // 1) Ground ring — a flat world-space canvas lying in the X-Z plane (visible face up).
            var ringGo = new GameObject("Ring");
            ringGo.transform.SetParent(_root.transform, false);
            var ringCanvas = ringGo.AddComponent<Canvas>();
            ringCanvas.renderMode = RenderMode.WorldSpace;
            const int RingPx = 256;
            ringGo.GetComponent<RectTransform>().sizeDelta = new Vector2(RingPx, RingPx);
            ringGo.transform.localScale = Vector3.one * (RingDiameterM / RingPx);
            ringGo.transform.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward); // lie flat, face up
            var ring = ringGo.AddComponent<RawImage>();
            ring.texture = RingTex();

            // 2) Beam + label — a separate child that gets billboarded toward the player.
            _facing = new GameObject("Facing");
            _facing.transform.SetParent(_root.transform, false);

            var beamGo = new GameObject("Beam");
            beamGo.transform.SetParent(_facing.transform, false);
            var beamCanvas = beamGo.AddComponent<Canvas>();
            beamCanvas.renderMode = RenderMode.WorldSpace;
            const int BeamPxW = 32, BeamPxH = 288;
            var beamRt = beamGo.GetComponent<RectTransform>();
            beamRt.sizeDelta = new Vector2(BeamPxW, BeamPxH);
            beamGo.transform.localScale = new Vector3(BeamWidthM / BeamPxW, BeamHeightM / BeamPxH, 1f);
            beamGo.transform.localPosition = new Vector3(0f, BeamHeightM / 2f, 0f); // base at the ground
            var beam = beamGo.AddComponent<RawImage>();
            beam.texture = BeamTex();

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(_facing.transform, false);
            var labelCanvas = labelGo.AddComponent<Canvas>();
            labelCanvas.renderMode = RenderMode.WorldSpace;
            const int LblPxW = 220, LblPxH = 56;
            labelGo.GetComponent<RectTransform>().sizeDelta = new Vector2(LblPxW, LblPxH);
            const float LabelWidthM = 1.8f;
            labelGo.transform.localScale = Vector3.one * (LabelWidthM / LblPxW);
            labelGo.transform.localPosition = new Vector3(0f, BeamHeightM + 0.35f, 0f); // just above the beam
            _label = labelGo.AddComponent<Text>();
            _label.font = ResolveFont();
            _label.alignment = TextAnchor.MiddleCenter;
            _label.horizontalOverflow = HorizontalWrapMode.Overflow;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.color = Color.white;
            _label.fontSize = 30;
        }

        // ---- procedural textures (built once, shared) ----

        private static Texture2D? _ringTex;
        private static Texture2D RingTex()
        {
            if (_ringTex != null) return _ringTex;
            const int N = 128;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[N * N];
            float c = (N - 1) / 2f, R = (N / 2f) - 2f;
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = x - c, dy = y - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / R;     // 0 centre … 1 edge
                float ringBand = Mathf.Exp(-Mathf.Pow((d - 0.86f) / 0.09f, 2f)); // bright ring near the rim
                float fill = d < 0.86f ? 0.16f : 0f;             // faint inner glow
                float a = d > 1f ? 0f : Mathf.Clamp01(ringBand + fill);
                px[y * N + x] = new Color(PortalTint.r, PortalTint.g, PortalTint.b, a);
            }
            t.SetPixels(px);
            t.Apply(false);
            _ringTex = t;
            return t;
        }

        private static Texture2D? _beamTex;
        private static Texture2D BeamTex()
        {
            if (_beamTex != null) return _beamTex;
            const int W = 16, H = 128;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                float up = y / (float)(H - 1);                    // 0 base … 1 top
                float vertical = Mathf.Pow(1f - up, 1.4f);        // bright at the base, fading up
                for (int x = 0; x < W; x++)
                {
                    float ex = Mathf.Abs((x / (float)(W - 1)) - 0.5f) * 2f; // 0 centre … 1 edge
                    float edge = Mathf.Clamp01(1f - ex * ex);      // soft-edged beam
                    float a = Mathf.Clamp01(vertical * edge * 0.9f);
                    px[y * W + x] = new Color(PortalTint.r, PortalTint.g, PortalTint.b, a);
                }
            }
            t.SetPixels(px);
            t.Apply(false);
            _beamTex = t;
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
