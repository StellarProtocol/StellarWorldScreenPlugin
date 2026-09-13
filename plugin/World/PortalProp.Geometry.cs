#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.WorldScreen.World
{
    /// <summary>
    /// The genuinely 3D parts of the beacon: a stone PEDESTAL built from stacked drums (each drum a ring of real
    /// vertical side faces plus a real top cap you see from above) and a flared light CONE built as a truncated
    /// cone of outward-tilted panels. Fixed geometry — it does NOT billboard, so from the game's top-down camera
    /// it reads with true depth (stepped tops) instead of a flat cut-out. Every face is a world-space
    /// <see cref="RawImage"/> on the <c>UI/Default</c> shader (present in the sandbox AND the game; <c>Cull Off</c>,
    /// so each face reads from both sides) — no <c>Shader.Find</c>, no magenta.
    /// </summary>
    internal sealed partial class PortalProp
    {
        private static readonly Color StoneDark = new Color(0.16f, 0.28f, 0.36f);
        private static readonly Color StoneMid  = new Color(0.30f, 0.52f, 0.63f);
        private static readonly Color StoneLit  = new Color(0.55f, 0.82f, 0.95f);

        // Three stacked drums, widest at the bottom — a real 3D stepped pedestal.
        private void BuildPedestal3D(Transform parent)
        {
            _pedestalGo = new GameObject("Pedestal");
            _pedestalGo.transform.SetParent(parent, false);
            var side = StoneSideTex();
            var cap = StoneCapTex();
            //        radius  yBottom  height
            BuildDrum(_pedestalGo.transform, 0.58f, 0.00f, 0.30f, side, cap);
            BuildDrum(_pedestalGo.transform, 0.44f, 0.30f, 0.28f, side, cap);
            BuildDrum(_pedestalGo.transform, 0.32f, 0.58f, 0.24f, side, cap); // top at PedTopYM (0.82)
        }

        // One drum: TierSides vertical side faces around a circle + a horizontal top cap disc.
        private void BuildDrum(Transform parent, float radius, float yBottom, float height, Texture2D sideTex, Texture2D capTex)
        {
            float panelW = 2f * radius * Mathf.Sin(Mathf.PI / TierSides) * 1.03f; // chord + a hair of overlap
            for (int i = 0; i < TierSides; i++)
            {
                float ang = (i + 0.5f) * (2f * Mathf.PI / TierSides);
                var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                var face = WorldCanvas("Side", parent, 48, 48, 1f);
                face.transform.localPosition = dir * radius + Vector3.up * (yBottom + height / 2f);
                face.transform.localRotation = Quaternion.LookRotation(dir, Vector3.up);
                face.transform.localScale = new Vector3(panelW / 48f, height / 48f, 1f);
                face.AddComponent<RawImage>().texture = sideTex; // texture carries the stone colour (opaque)
            }
            // Top cap disc, lying flat, just above the drum's top so it reads as the tier's face from above.
            var top = WorldCanvas("Cap", parent, 64, 64, 1f);
            top.transform.localPosition = Vector3.up * (yBottom + height + 0.004f);
            top.transform.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            top.transform.localScale = new Vector3((2f * radius) / 64f, (2f * radius) / 64f, 1f);
            top.AddComponent<RawImage>().texture = capTex;
        }

        // The flared 3D light cone: ConeSides outward-tilted panels forming a truncated cone from the pedestal
        // top (radius ConeBotRM) out to radius ConeTopRM at height ConeHM. Parented to a group Tick spins about Y.
        private void BuildCone3D(Transform parent)
        {
            _coneGroup = new GameObject("Cone");
            _coneGroup.transform.SetParent(parent, false);
            var tex = ConePanelTex();
            float yBase = PedTopYM, yTop = PedTopYM + ConeHM;
            float midR = (ConeBotRM + ConeTopRM) / 2f;
            float panelW = 2f * midR * Mathf.Sin(Mathf.PI / ConeSides) * 1.25f; // generous overlap → continuous curtain
            for (int i = 0; i < ConeSides; i++)
            {
                float ang = i * (2f * Mathf.PI / ConeSides);
                var dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                var pB = dir * ConeBotRM + Vector3.up * yBase; // panel foot (at the pedestal)
                var pT = dir * ConeTopRM + Vector3.up * yTop;  // panel head (flared out, up high)
                var slant = (pT - pB).normalized;
                var tangent = Vector3.Cross(Vector3.up, dir);
                var normal = Vector3.Cross(slant, tangent).normalized; // panel faces outward along the cone wall
                var panel = WorldCanvas("ConePanel", _coneGroup.transform, 48, 96, 1f);
                panel.transform.localPosition = (pB + pT) / 2f;
                panel.transform.localRotation = Quaternion.LookRotation(normal, slant);
                panel.transform.localScale = new Vector3(panelW / 48f, (pT - pB).magnitude / 96f, 1f);
                var img = panel.AddComponent<RawImage>();
                img.texture = tex;
                img.color = Alpha(Tint, 0.42f);
                _conePanels.Add(img);
            }
        }

        // ---- geometry textures (built once, shared) ----

        private static Texture2D? _stoneSide, _stoneCap, _conePanel, _gem;

        // Opaque stone side face: darker at the foot, lighter up, a lit lip at the very top, gently bevelled at
        // the vertical edges so the faceting of the drum reads.
        private static Texture2D StoneSideTex()
        {
            if (_stoneSide != null) return _stoneSide;
            _stoneSide = BuildColor(48, 48, (x, y, W, H) =>
            {
                float up = y / (float)(H - 1);
                float fx = Mathf.Abs(x / (float)(W - 1) - 0.5f) * 2f;   // 0 centre … 1 vertical edge
                var c = Color.Lerp(StoneDark, StoneMid, up);
                if (up > 0.82f) c = Color.Lerp(c, StoneLit, (up - 0.82f) / 0.18f * 0.9f); // lit top lip
                float bevel = 1f - 0.20f * fx;                          // slightly darker toward the side edges
                c *= bevel; c.a = 1f;
                return c;
            });
            return _stoneSide;
        }

        // Opaque lit-stone top cap disc (transparent outside the circle) — the face seen from directly above.
        private static Texture2D StoneCapTex()
        {
            if (_stoneCap != null) return _stoneCap;
            _stoneCap = BuildColor(64, 64, (x, y, W, H) =>
            {
                float cx = (W - 1) / 2f, cy = (H - 1) / 2f, R = W / 2f - 2f;
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / R;
                if (d > 1f) return new Color(0, 0, 0, 0);
                var c = Color.Lerp(StoneLit, StoneMid, d * 0.7f);        // brighter centre, darker rim
                if (d > 0.90f) c = Color.Lerp(c, StoneLit, (d - 0.90f) / 0.10f * 0.6f); // rim highlight
                c.a = 1f;
                return c;
            });
            return _stoneCap;
        }

        // Translucent cone-wall gradient: glowy at the foot (by the pedestal), fading up; soft vertical seams.
        private static Texture2D ConePanelTex()
        {
            if (_conePanel != null) return _conePanel;
            _conePanel = Build(48, 96, (x, y) =>
            {
                float up = y / 95f;                                     // 0 foot … 1 head
                float fx = Mathf.Abs(x / 47f - 0.5f) * 2f;
                float vert = Mathf.Lerp(0.75f, 0.22f, up);              // bright at the pedestal, fainter aloft
                float edge = Mathf.Clamp01(1f - Mathf.Pow(fx, 3f));    // soften side seams into a continuous cone
                return Mathf.Clamp01(edge * vert);
            });
            return _conePanel;
        }

        // Bright cyan-white gem orb.
        private static Texture2D GemTex()
        {
            if (_gem != null) return _gem;
            _gem = Build(40, 40, (x, y) =>
            {
                float c = 19.5f, R = 20f;
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / R;
                float core = Mathf.Exp(-Mathf.Pow(d / 0.42f, 2f));
                return Mathf.Clamp01(core);
            }, new Color(0.80f, 0.98f, 1.0f, 1f));
            return _gem;
        }

        // Per-pixel opaque/masked texture builder (colour varies per pixel, unlike Build which tints one colour).
        private static Texture2D BuildColor(int w, int h, Func<int, int, int, int, Color> pixel)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = pixel(x, y, w, h);
            t.SetPixels(px);
            t.Apply(false);
            return t;
        }
    }
}
