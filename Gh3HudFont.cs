using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ClonZones
{
    /// <summary>
    /// GH3 PC bitmap font in the editable form the mod ships: `<name>.png` (straight-alpha
    /// page) + `<name>.font.txt` (one glyph per line, pixel rects). Both are exported from
    /// the original `.fnt.xen` by tools/gh3_font_export.py, which documents the container.
    /// Measure/emit rules follow the native text path (sub_5F2370 measure,
    /// SText::vmethod4 emitter): pen step = width + font_spacing (trailing spacing
    /// included), space bypasses the table, height = lineheight, no kerning.
    /// </summary>
    internal sealed class Gh3HudFont
    {
        public readonly struct Glyph
        {
            public readonly float X, Y, Width, Height, YOff;   // page pixels, origin top-left
            public readonly int XOff, Extra;
            public Glyph(float x, float y, float w, float h, float yOff, int xOff, int extra)
            { X = x; Y = y; Width = w; Height = h; YOff = yOff; XOff = xOff; Extra = extra; }
        }

        public readonly string Name;
        /// <summary>Container header+0, subtracted from every glyph's vertical origin (7 / 0 / 15).</summary>
        public float YOrigin { get; private set; }
        public float DefaultPre { get; private set; }
        public float DefaultPost { get; private set; }
        public float LineHeight { get; private set; }
        public float SpaceWidth { get; private set; }
        public int PageWidth { get; private set; }
        public int PageHeight { get; private set; }
        private readonly Glyph[] _byChar = new Glyph[256];
        private readonly bool[] _mapped = new bool[256];
        private Glyph _fallback;                  // the game renders glyph 0 for unmapped chars
        private Gh3HudRegion _page;
        private int _glyphCount;

        /// <summary>g_fontBaselineOffset (0x95FDD0): unscaled pixel added to every quad top.</summary>
        public const float BaselineOffset = 1.25f;

        public int GlyphCount => _glyphCount;
        public Gh3HudRegion Page => _page;

        private Gh3HudFont(string name) { Name = name; }

        public void AssignAtlas(Gh3HudRegion page) { _page = page; }

        public bool IsMapped(char c) => c == ' ' || (c < 256 && _mapped[c]);
        public Glyph GlyphFor(char c) => c < 256 && _mapped[c] ? _byChar[c] : _fallback;

        /// <summary>Atlas UVs of a glyph rect; V1 is the top edge (Unity V is bottom-up).</summary>
        public void AtlasUv(in Glyph g, out float u0, out float v0, out float u1, out float v1)
        {
            float du = (_page.U1 - _page.U0) / PageWidth, dv = (_page.V1 - _page.V0) / PageHeight;
            u0 = _page.U0 + g.X * du; u1 = _page.U0 + (g.X + g.Width) * du;
            v1 = _page.V1 - g.Y * dv; v0 = _page.V1 - (g.Y + g.Height) * dv;
        }

        /// <summary>
        /// sub_5F2370: width = sum(advance + post - pre) including the trailing spacing;
        /// height = lineheight. Font units before element scale. `spacing` &lt; 0 = the
        /// element never set font_spacing (CTextElement default -1): font defaults apply.
        /// </summary>
        public Vector2 Measure(char[] text, int length, float spacing)
        {
            float pre = spacing < 0f ? DefaultPre : 0f;
            float post = spacing < 0f ? DefaultPost : spacing;
            float w = 0f;
            for (int i = 0; i < length; i++)
            {
                char c = text[i];
                float adv = c == ' ' ? SpaceWidth : GlyphFor(c).Width;
                w += adv + post - pre;
            }
            return new Vector2(w, LineHeight);
        }

        /// <summary>Parses `<name>.font.txt`. Throws on any malformed line so the HUD fails closed.</summary>
        public static Gh3HudFont Parse(string name, string metrics, int pageWidth, int pageHeight)
        {
            var font = new Gh3HudFont(name) { PageWidth = pageWidth, PageHeight = pageHeight, LineHeight = -1f, SpaceWidth = -1f };
            bool haveFallback = false;
            var inv = CultureInfo.InvariantCulture;
            foreach (string raw in metrics.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (t[0])
                {
                    case "page":
                        if (t.Length < 3) throw new InvalidDataException("page needs width height");
                        int w = int.Parse(t[1], inv), h = int.Parse(t[2], inv);
                        if (w != pageWidth || h != pageHeight)
                            throw new InvalidDataException($"metrics say page {w}x{h} but {name}.png is {pageWidth}x{pageHeight}");
                        break;
                    case "lineheight": font.LineHeight = float.Parse(t[1], inv); break;
                    case "spacewidth": font.SpaceWidth = float.Parse(t[1], inv); break;
                    case "yorigin": font.YOrigin = float.Parse(t[1], inv); break;
                    case "pre": font.DefaultPre = float.Parse(t[1], inv); break;
                    case "post": font.DefaultPost = float.Parse(t[1], inv); break;
                    case "glyph":
                    {
                        if (t.Length < 7) throw new InvalidDataException($"glyph line too short: {line}");
                        int code = ParseChar(t[1]);
                        var g = new Glyph(float.Parse(t[2], inv), float.Parse(t[3], inv), float.Parse(t[4], inv), float.Parse(t[5], inv),
                            float.Parse(t[6], inv), t.Length > 7 ? int.Parse(t[7], inv) : 0, t.Length > 8 ? int.Parse(t[8], inv) : 0);
                        if (g.X < 0 || g.Y < 0 || g.X + g.Width > pageWidth || g.Y + g.Height > pageHeight)
                            throw new InvalidDataException($"glyph {t[1]} rect leaves the page");
                        if (code < 256) { font._byChar[code] = g; font._mapped[code] = true; }
                        if (!haveFallback) { font._fallback = g; haveFallback = true; }
                        font._glyphCount++;
                        break;
                    }
                    default: throw new InvalidDataException($"unknown key '{t[0]}'");
                }
            }
            if (font.LineHeight < 0f || font.SpaceWidth < 0f || font._glyphCount == 0)
                throw new InvalidDataException("lineheight, spacewidth and at least one glyph are required");
            return font;
        }

        private static int ParseChar(string token)
        {
            if (token.Length == 1) return token[0];
            if (token.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
                return int.Parse(token.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            throw new InvalidDataException($"bad glyph char '{token}'");
        }
    }
}
