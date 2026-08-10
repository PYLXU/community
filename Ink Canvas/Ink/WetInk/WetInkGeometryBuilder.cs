using System;
using System.Collections.Generic;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>渲染顶点：位置（DIP）+ 预乘颜色。</summary>
    internal readonly struct WetInkVertex
    {
        public WetInkVertex(float x, float y, uint colorArgb)
        {
            X = x;
            Y = y;
            ColorArgb = colorArgb;
        }

        public float X { get; }
        public float Y { get; }
        public uint ColorArgb { get; }
    }

    /// <summary>
    /// 一条湿墨带的三角形几何。使用 triangle list，
    /// 每段两个三角，端点加圆头帽扇形。
    /// </summary>
    internal sealed class WetInkRibbonGeometry
    {
        public WetInkRibbonGeometry(WetInkVertex[] vertices, int vertexCount)
        {
            Vertices = vertices ?? Array.Empty<WetInkVertex>();
            VertexCount = vertexCount;
        }

        public WetInkVertex[] Vertices { get; }
        public int VertexCount { get; }

        public bool IsEmpty => VertexCount < 3;

        public static readonly WetInkRibbonGeometry Empty =
            new WetInkRibbonGeometry(Array.Empty<WetInkVertex>(), 0);
    }

    /// <summary>
    /// 把采样点串转成可变宽度的三角形带。
    /// 宽度由「样式基宽 × 压感」决定；圆头帽用扇形三角逼近。
    /// </summary>
    internal static class WetInkGeometryBuilder
    {
        /// <summary>圆头帽扇形分段数。</summary>
        private const int CapSegments = 8;

        /// <summary>最小半宽（DIP），防止压感为 0 时退化成零面积。</summary>
        private const double MinHalfWidth = 0.35;

        /// <summary>
        /// 构建整条带的几何。samples 为时间升序（真实点 + 预测尾）。
        /// </summary>
        public static WetInkRibbonGeometry Build(
            IReadOnlyList<WetInkSample> realSamples,
            WetInkSample[] predictedSamples,
            in WetInkStyleSnapshot style)
        {
            var total = (realSamples?.Count ?? 0) + (predictedSamples?.Length ?? 0);
            if (total < 1)
                return WetInkRibbonGeometry.Empty;

            // 合并真实 + 预测成单串。
            var points = new List<WetInkSample>(total);
            if (realSamples != null)
                points.AddRange(realSamples);
            if (predictedSamples != null && predictedSamples.Length > 0)
                points.AddRange(predictedSamples);

            if (points.Count == 1)
                return BuildDot(points[0], style);

            // 上限估算：每段 6 顶点 + 两端帽各 CapSegments*3。
            var maxVertices = (points.Count - 1) * 6 + CapSegments * 6 + 12;
            var vertices = new WetInkVertex[maxVertices];
            var count = 0;

            var color = style.ColorArgb;

            for (var i = 0; i < points.Count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];

                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= double.Epsilon)
                    continue;

                // 单位法线。
                var nx = -dy / len;
                var ny = dx / len;

                var ha = HalfWidth(a, style);
                var hb = HalfWidth(b, style);

                var a0x = (float)(a.X + nx * ha);
                var a0y = (float)(a.Y + ny * ha);
                var a1x = (float)(a.X - nx * ha);
                var a1y = (float)(a.Y - ny * ha);
                var b0x = (float)(b.X + nx * hb);
                var b0y = (float)(b.Y + ny * hb);
                var b1x = (float)(b.X - nx * hb);
                var b1y = (float)(b.Y - ny * hb);

                // 两个三角形组成四边形。
                vertices[count++] = new WetInkVertex(a0x, a0y, color);
                vertices[count++] = new WetInkVertex(b0x, b0y, color);
                vertices[count++] = new WetInkVertex(a1x, a1y, color);

                vertices[count++] = new WetInkVertex(a1x, a1y, color);
                vertices[count++] = new WetInkVertex(b0x, b0y, color);
                vertices[count++] = new WetInkVertex(b1x, b1y, color);

                // 关节处补一个圆头帽，避免转折出现缺口。
                if (i > 0)
                    count = AppendCap(vertices, count, a, ha, color);
            }

            // 两端圆头帽。
            count = AppendCap(vertices, count, points[0], HalfWidth(points[0], style), color);
            count = AppendCap(vertices, count, points[points.Count - 1],
                HalfWidth(points[points.Count - 1], style), color);

            return new WetInkRibbonGeometry(vertices, count);
        }

        /// <summary>单点（轻点）时画一个圆点。</summary>
        private static WetInkRibbonGeometry BuildDot(
            in WetInkSample sample, in WetInkStyleSnapshot style)
        {
            var vertices = new WetInkVertex[CapSegments * 3];
            var count = AppendCap(vertices, 0, sample, HalfWidth(sample, style), style.ColorArgb);
            return new WetInkRibbonGeometry(vertices, count);
        }

        /// <summary>在指定点追加一个圆头帽（扇形三角）。</summary>
        private static int AppendCap(
            WetInkVertex[] vertices, int count, in WetInkSample center, double halfWidth, uint color)
        {
            if (count + CapSegments * 3 > vertices.Length)
                return count;

            var cx = (float)center.X;
            var cy = (float)center.Y;
            var step = Math.PI * 2 / CapSegments;

            for (var i = 0; i < CapSegments; i++)
            {
                var a0 = i * step;
                var a1 = (i + 1) * step;

                vertices[count++] = new WetInkVertex(cx, cy, color);
                vertices[count++] = new WetInkVertex(
                    (float)(cx + Math.Cos(a0) * halfWidth),
                    (float)(cy + Math.Sin(a0) * halfWidth),
                    color);
                vertices[count++] = new WetInkVertex(
                    (float)(cx + Math.Cos(a1) * halfWidth),
                    (float)(cy + Math.Sin(a1) * halfWidth),
                    color);
            }

            return count;
        }

        /// <summary>由样式基宽与压感算半宽。</summary>
        private static double HalfWidth(in WetInkSample sample, in WetInkStyleSnapshot style)
        {
            var baseWidth = style.WidthDip;
            if (style.IgnorePressure)
                return Math.Max(MinHalfWidth, baseWidth * 0.5);

            // WPF 的 PressureFactor 语义：0.5 为常压，故 ×2 归一。
            var factor = Math.Max(0.1, Math.Min(2.0, sample.Pressure * 2.0));
            return Math.Max(MinHalfWidth, baseWidth * 0.5 * factor);
        }
    }
}