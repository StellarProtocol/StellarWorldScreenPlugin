#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.WorldScreen.World
{
    /// <summary>
    /// One placed portal's in-world beacon: a flat glowing RING on the ground (slowly spinning + breathing) +
    /// a vertical light BEAM rising from it (billboarded toward the player, energy scrolling up) + a small
    /// owner-name tag. Procedural + shader-safe (world-space <see cref="Canvas"/> + <see cref="RawImage"/> with
    /// generated <see cref="Texture2D"/>s — NO <c>Shader.Find</c>, mirroring the proven WorldScreen render path
    /// and the procedural-texture approach in <c>docs/rendering-images-in-game.md</c>). Iterated in the UI
    /// sandbox (story <c>portal-beacon</c>) so the look is tuned without an in-game relogin.
    /// </summary>
    internal sealed class PortalProp
    {
        private const float RingDiameterM = 2.6f;   // ground ring diameter
        private const float BeamWidthM = 0.55f;     // beam width
        private const float BeamHeightM = 5.0f;     // beam height
        private static readonly Color PortalTint = new Color(0.30f, 0.80f, 1.0f, 1f); // cyan

        private GameObject? _root;    // at the ground position
        private GameObject? _ringGo;  // flat ring child (spun in-plane)
        private GameObject? _facing;  // beam + label, billboarded toward the player
        private RawImage? _ringImg;
        private RawImage? _beamImg;
        private Text? _label;
        private Quaternion _flatRot = Quaternion.identity; // the ring's lie-flat orientation (spin is added on top)

        /// <summary>Creates the beacon (once) at ground <paramref name="pos"/> with the owner-name text.</summary>
        public void Place(Vector3 pos, string text)
        {
            EnsureCreated();
            _root!.transform.position = pos + Vector3.up * 0.05f; // just above the ground (avoid z-fight)
            if (_label != null) _label.text = text;
        }

        /// <summary>Per-frame: billboard the beam/tag toward the player, spin + breathe the ring, scroll the
        /// beam's energy upward. <paramref name="time"/> is a seconds clock (e.g. <c>Time.time</c>).</summary>
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

            // Ring: spin slowly in its own plane + breathe its glow.
            if (_ringGo != null) _ringGo.transform.localRotation = _flatRot * Quaternion.Euler(0f, 0f, time * 24f);
            float ringPulse = 0.72f + 0.28f * Mathf.Sin(time * 2.2f);
            if (_ringImg != null) _ringImg.color = new Color(PortalTint.r, PortalTint.g, PortalTint.b, ringPulse);

            // Beam: breathe alpha + scroll the banded texture upward (rising energy). Needs wrap=Repeat.
            if (_beamImg != null)
            {
                float beamPulse = 0.55f + 0.20f * Mathf.Sin(time * 2.6f + 0.8f);
                _beamImg.color = new Color(PortalTint.r, PortalTint.g, PortalTint.b, beamPulse);
                _beamImg.uvRect = new Rect(0f, time * 0.5f, 1f, 1f); // V scroll → energy flows up
            }
        }

        public void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null; _ringGo = null; _facing = null; _ringImg = null; _beamImg = null; _label = null;
            }
        }

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("StellarPortalBeacon");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            // 1) Ground ring — a flat world-space canvas lying in the X-Z plane (visible face up).
            _ringGo = new GameObject("Ring");
            _ringGo.transform.SetParent(_root.transform, false);
            var ringCanvas = _ringGo.AddComponent<Canvas>();
            ringCanvas.renderMode = RenderMode.WorldSpace;
            const int RingPx = 256;
            _ringGo.GetComponent<RectTransform>().sizeDelta = new Vector2(RingPx, RingPx);
            _ringGo.transform.localScale = Vector3.one * (RingDiameterM / RingPx); // pixels → metres
            _flatRot = Quaternion.LookRotation(Vector3.up, Vector3.forward);         // lie flat, visible face up
            _ringGo.transform.localRotation = _flatRot;
            _ringImg = _ringGo.AddComponent<RawImage>();
            _ringImg.texture = RingTex();

            // 2) Beam + label — billboarded toward the player.
            _facing = new GameObject("Facing");
            _facing.transform.SetParent(_root.transform, false);

            var beamGo = new GameObject("Beam");
            beamGo.transform.SetParent(_facing.transform, false);
            var beamCanvas = beamGo.AddComponent<Canvas>();
            beamCanvas.renderMode = RenderMode.WorldSpace;
            const int BeamPxW = 32, BeamPxH = 288;
            beamGo.GetComponent<RectTransform>().sizeDelta = new Vector2(BeamPxW, BeamPxH);
            beamGo.transform.localScale = new Vector3(BeamWidthM / BeamPxW, BeamHeightM / BeamPxH, 1f);
            beamGo.transform.localPosition = new Vector3(0f, BeamHeightM / 2f, 0f); // base at the ground
            _beamImg = beamGo.AddComponent<RawImage>();
            _beamImg.texture = BeamTex();

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
                float outer = Mathf.Exp(-Mathf.Pow((d - 0.88f) / 0.06f, 2f)); // bright rim ring
                float inner = Mathf.Exp(-Mathf.Pow((d - 0.55f) / 0.10f, 2f)) * 0.5f; // fainter inner ring
                float fill = d < 0.88f ? 0.12f : 0f;             // faint disc glow
                float a = d > 1f ? 0f : Mathf.Clamp01(outer + inner + fill);
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
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat }; // scrolls
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                float up = y / (float)(H - 1);                    // 0 base … 1 top of the tile
                float band = 0.6f + 0.4f * Mathf.Sin(up * Mathf.PI * 4f); // 2 soft rising bands per tile
                for (int x = 0; x < W; x++)
                {
                    float ex = Mathf.Abs((x / (float)(W - 1)) - 0.5f) * 2f; // 0 centre … 1 edge
                    float edge = Mathf.Clamp01(1f - ex * ex);      // soft-edged beam
                    float a = Mathf.Clamp01(band * edge * 0.85f);
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
