#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.WorldScreen.World
{
    /// <summary>
    /// The genuinely 3D stone PEDESTAL: three stacked drums, each a ring of real vertical side faces plus a real
    /// horizontal top cap you see from above. Fixed geometry — it does NOT billboard, so from the game's top-down
    /// camera it reads with true depth (stepped tops) instead of a flat cut-out. Every face is a world-space
    /// <see cref="RawImage"/> on the <c>UI/Default</c> shader (present in the sandbox AND the game; <c>Cull Off</c>,
    /// so each face reads from both sides) — no <c>Shader.Find</c>, no magenta. (The light cone is a single soft
    /// billboarded glow, built in <see cref="PortalProp"/> itself, so the beam stays smooth from every angle.)
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

        // ---- geometry textures (built once, shared) ----

        private static Texture2D? _stoneSide, _stoneCap, _gem;

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
