using System;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.WorldScreen.World
{
    /// <summary>
    /// One placed portal's in-world marker: a floating world-space uGUI panel (owner name + watcher count)
    /// billboarded toward the player. Procedural + shader-safe (world-space <see cref="Canvas"/> + default
    /// uGUI <see cref="Image"/>/<see cref="Text"/> — NO <c>Shader.Find</c>, mirroring the proven WorldScreen
    /// render path). SP-1c first render increment: a visible marker per portal; walk-up activation raising the
    /// AVPro screen is a later increment.
    /// </summary>
    internal sealed class PortalProp
    {
        private const float WidthM = 1.4f;      // world width of the marker panel
        private const int PxW = 240, PxH = 150; // canvas pixel size (aspect ~ panel); scaled to metres below
        private const float FloatUpM = 1.6f;    // hover the marker above the portal spot

        private GameObject? _root;
        private Text? _label;
        private Vector3 _worldPos;

        /// <summary>Creates the marker (once) and points it at <paramref name="pos"/> with the given text.</summary>
        public void Place(Vector3 pos, string text)
        {
            EnsureCreated();
            _worldPos = pos + Vector3.up * FloatUpM;
            _root!.transform.position = _worldPos;
            if (_label != null) _label.text = text;
        }

        /// <summary>Re-orients the marker to face the player horizontally (upright billboard). Call per frame.</summary>
        public void Billboard(Camera? cam)
        {
            if (_root == null || cam == null) return;
            var dir = _root.transform.position - cam.transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return;
            _root.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        public void Destroy()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; _label = null; }
        }

        private void EnsureCreated()
        {
            if (_root != null) return;
            _root = new GameObject("StellarPortalProp");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = _root.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(PxW, PxH);
            _root.transform.localScale = Vector3.one * (WidthM / PxW); // pixels → metres

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(_root.transform, false);
            var img = panelGo.AddComponent<Image>();
            img.color = new Color(0.10f, 0.32f, 0.85f, 0.86f); // portal blue
            Stretch(img.rectTransform);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(_root.transform, false);
            _label = labelGo.AddComponent<Text>();
            _label.font = ResolveFont();
            _label.alignment = TextAnchor.MiddleCenter;
            _label.horizontalOverflow = HorizontalWrapMode.Wrap;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.color = Color.white;
            _label.fontSize = 26;
            Stretch(_label.rectTransform, inset: 10f);
        }

        private static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
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
