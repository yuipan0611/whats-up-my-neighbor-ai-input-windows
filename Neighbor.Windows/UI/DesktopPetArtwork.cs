using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using Neighbor.Core;

namespace Neighbor.Windows.UI
{
    /// <summary>Renders the bundled original SVG paths; no runtime downloads or SVG dependency.</summary>
    public sealed class DesktopPetArtwork : IDisposable
    {
        private sealed class Shape : IDisposable
        {
            public GraphicsPath Path;
            public string Fill, Stroke;
            public float Width;
            public void Dispose() { Path.Dispose(); }
        }
        private readonly Dictionary<string, List<Shape>> images = new Dictionary<string, List<Shape>>();
        public DesktopPetArtwork()
        {
            try
            {
                foreach (string name in new[] { "sit", "walk-body", "walk-front-face", "walk-foot-0", "walk-foot-1", "walk-foot-2", "walk-foot-3" })
                {
                    var list = new List<Shape>(); images.Add(name, list);
                    using (var stream = typeof(DesktopPetArtwork).Assembly.GetManifestResourceStream("Neighbor.pet." + name + ".svg"))
                    using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    {
                        while (reader.Read()) if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "path")
                            list.Add(new Shape { Path = Parse(reader.GetAttribute("d")), Fill = reader.GetAttribute("fill"), Stroke = reader.GetAttribute("stroke"), Width = Number(reader.GetAttribute("stroke-width") ?? "1") });
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        private static float Number(string value) { return float.Parse(value, CultureInfo.InvariantCulture); }
        // Deliberately only supports the absolute path commands in our bundled artwork.
        private static GraphicsPath Parse(string data)
        {
            var tokens = Regex.Matches(data, @"[A-Za-z]|[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?");
            var path = new GraphicsPath();
            int i = 0; char command = ' '; PointF current = default(PointF), start = current;
            Func<float> read = () => Number(tokens[i++].Value);
            try
            {
                while (i < tokens.Count)
                {
                    if (char.IsLetter(tokens[i].Value[0])) command = tokens[i++].Value[0];
                    switch (command)
                    {
                        case 'M': current = new PointF(read(), read()); start = current; path.StartFigure(); command = 'L'; break;
                        case 'L': { var p = new PointF(read(), read()); path.AddLine(current, p); current = p; break; }
                        case 'H': { var p = new PointF(read(), current.Y); path.AddLine(current, p); current = p; break; }
                        case 'V': { var p = new PointF(current.X, read()); path.AddLine(current, p); current = p; break; }
                        case 'C': { var a = new PointF(read(), read()); var b = new PointF(read(), read()); var p = new PointF(read(), read()); path.AddBezier(current, a, b, p); current = p; break; }
                        case 'Z': case 'z': path.CloseFigure(); current = start; command = ' '; break;
                        default: throw new InvalidOperationException("Unsupported bundled cat path command: " + command);
                    }
                }
                return path;
            }
            catch { path.Dispose(); throw; }
        }
        private void Image(Graphics g, string name)
        {
            foreach (var shape in images[name])
            {
                if (shape.Fill != null && shape.Fill != "none")
                    using (var brush = new SolidBrush(ColorTranslator.FromHtml(shape.Fill))) g.FillPath(brush, shape.Path);
                if (shape.Stroke != null)
                    using (var pen = new Pen(ColorTranslator.FromHtml(shape.Stroke), shape.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) g.DrawPath(pen, shape.Path);
            }
        }
        public void Draw(Graphics g, int size, bool walking, bool facingLeft, double distance)
        {
            var original = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size / 400f, size / 400f);
                if (!walking) { Image(g, "sit"); return; }
                float bob = (float)(-Math.Abs(Math.Sin(distance / (86 * .28 / .6) * 2 * Math.PI)) * 3);
                var silhouette = g.Save();
                if (!facingLeft) { g.TranslateTransform(400, 0); g.ScaleTransform(-1, 1); }
                float[] rootsX = { 112, 159, 270, 309 }, rootsY = { 244, 270, 270, 247 };
                float[] necksX = { 103, 161, 271, 321 }, necksY = { 303, 322, 322, 313 };
                float[] rootWidths = { 58, 54, 56, 54 }, neckWidths = { 50, 48, 48, 44 };
                for (int i = 0; i < 4; i++)
                {
                    DesktopPetMotion.PawOffset(distance, i % 2, out float x, out float y);
                    float rx = rootsX[i], ry = rootsY[i] + bob, nx = necksX[i] + x, ny = necksY[i] + y;
                    float rw = rootWidths[i] / 2, nw = neckWidths[i] / 2, h = Math.Max(10, ny - ry);
                    using (var join = new GraphicsPath())
                    using (var fur = new SolidBrush(Color.FromArgb(16, 17, 20)))
                    {
                        join.AddBezier(rx - rw, ry, rx - rw, ry + h * .4f, nx - nw, ny - h * .3f, nx - nw, ny);
                        join.AddLine(nx - nw, ny, nx + nw, ny);
                        join.AddBezier(nx + nw, ny, nx + nw, ny - h * .3f, rx + rw, ry + h * .4f, rx + rw, ry);
                        join.CloseFigure(); g.FillPath(fur, join);
                    }
                    var paw = g.Save(); g.TranslateTransform(x, y); Image(g, "walk-foot-" + i); g.Restore(paw);
                }
                g.TranslateTransform(0, bob); Image(g, "walk-body"); g.Restore(silhouette);
                // Keep X on the left and the check on the right when changing direction.
                g.TranslateTransform(facingLeft ? 0 : 40, bob); Image(g, "walk-front-face");
            }
            finally { g.Restore(original); }
        }
        public void Dispose() { foreach (var list in images.Values) foreach (var shape in list) shape.Dispose(); images.Clear(); }
    }
}
