using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// 「图像调整」的软件实现：亮度 / 对比度 / 饱和度。
    /// <para>
    /// 为什么不用像素着色器：项目里没有可复用的 HLSL 编译链（<c>Shaders</c> 下只有放大镜效果），
    /// 而 WPF 自带的 <c>BitmapEffect</c> 早已废弃。这里用托管代码逐像素处理，
    /// 并按<b>横向分块</b>读写（每块约 1MB），因此即使 1 亿像素的图也不会一次分配几百 MB 的数组。
    /// </para>
    /// <para>
    /// 调用方应当做防抖（滑杆拖动时每 ~120ms 才算一次），因为一次全图处理与像素数成正比。
    /// </para>
    /// </summary>
    public static class ImageAdjustment
    {
        /// <summary>分块处理的单块目标字节数（约 1MB，兼顾缓存友好与内存占用）。</summary>
        private const int ChunkTargetBytes = 1 << 20;

        /// <summary>
        /// 单次调整处理的像素数上限（约 16MP，即 4096×4096）。
        /// 超过上限的超大图会先等比缩小再调整：调整是「观感」层面的操作，
        /// 而 48MP 全尺寸处理会一次吃掉几百 MB，得不偿失。
        /// </summary>
        private const long MaxAdjustPixels = 16L * 1024 * 1024;

        /// <summary>参数是否等于恒等变换（省掉一次全图处理）。</summary>
        public static bool IsIdentity(double brightness, double contrast, double saturation)
            => Math.Abs(brightness) < 0.01 && Math.Abs(contrast) < 0.01 && Math.Abs(saturation) < 0.01;

        /// <summary>
        /// 对位图应用亮度 / 对比度 / 饱和度调整。
        /// </summary>
        /// <param name="source">源位图（通常是图片的原始像素）。</param>
        /// <param name="brightness">亮度，-100 ~ 100（0 = 不变）。</param>
        /// <param name="contrast">对比度，-100 ~ 100（0 = 不变）。</param>
        /// <param name="saturation">饱和度，-100 ~ 100（-100 = 灰度，0 = 不变）。</param>
        /// <returns>调整后的位图；参数为恒等或输入为空时原样返回 <paramref name="source"/>。</returns>
        public static BitmapSource Apply(BitmapSource source, double brightness, double contrast, double saturation)
        {
            if (source == null) return null;
            if (IsIdentity(brightness, contrast, saturation)) return source;

            try
            {
                source = LimitResolution(source);

                int width = source.PixelWidth;
                int height = source.PixelHeight;
                if (width <= 0 || height <= 0) return source;

                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                converted.Freeze();

                double dpiX = source.DpiX > 0 ? source.DpiX : 96.0;
                double dpiY = source.DpiY > 0 ? source.DpiY : 96.0;

                int stride = width * 4;
                int rowsPerChunk = Math.Max(1, Math.Min(height, ChunkTargetBytes / Math.Max(1, stride)));

                // 亮度 + 对比度可以先做成 256 项查找表，逐像素只剩查表与一次乘加。
                byte[] lut = BuildValueLut(brightness, contrast);
                double saturationFactor = 1.0 + Clamp(saturation, -100, 100) / 100.0;

                var buffer = new byte[stride * rowsPerChunk];
                var target = new WriteableBitmap(width, height, dpiX, dpiY, PixelFormats.Bgra32, null);

                for (int y = 0; y < height; y += rowsPerChunk)
                {
                    int rows = Math.Min(rowsPerChunk, height - y);
                    var rect = new Int32Rect(0, y, width, rows);

                    converted.CopyPixels(rect, buffer, stride, 0);
                    ProcessChunk(buffer, rows * width, lut, saturationFactor);
                    target.WritePixels(rect, buffer, stride, 0);
                }

                target.Freeze();
                return target;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图像调整失败: {ex.Message}", LogHelper.LogType.Warning);
                return source;
            }
        }

        /// <summary>
        /// 把超过 <see cref="MaxAdjustPixels"/> 的位图等比缩小到上限以内（保留原图不变）。
        /// </summary>
        private static BitmapSource LimitResolution(BitmapSource source)
        {
            long pixels = (long)source.PixelWidth * source.PixelHeight;
            if (pixels <= MaxAdjustPixels) return source;

            double scale = Math.Sqrt((double)MaxAdjustPixels / pixels);
            try
            {
                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                scaled.Freeze();
                LogHelper.WriteLogToFile(
                    $"图片 {source.PixelWidth}×{source.PixelHeight} 超过图像调整上限，已按 {scale:P0} 缩小后处理",
                    LogHelper.LogType.Event);
                return scaled;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图像调整降采样失败: {ex.Message}", LogHelper.LogType.Warning);
                return source;
            }
        }

        /// <summary>亮度 + 对比度查找表：<c>v' = clamp((v + brightness*255 - 128) * (1 + contrast) + 128)</c>。</summary>
        private static byte[] BuildValueLut(double brightness, double contrast)
        {
            double offset = Clamp(brightness, -100, 100) / 100.0 * 255.0;
            double factor = 1.0 + Clamp(contrast, -100, 100) / 100.0;

            var lut = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                double value = (v + offset - 128.0) * factor + 128.0;
                lut[v] = (byte)Clamp(value, 0, 255);
            }
            return lut;
        }

        /// <summary>就地处理一段 Bgra32 像素：查表 + 按亮度做饱和度混合，alpha 保持不变。</summary>
        private static void ProcessChunk(byte[] buffer, int pixelCount, byte[] lut, double saturationFactor)
        {
            bool applySaturation = Math.Abs(saturationFactor - 1.0) > 0.001;

            for (int i = 0, p = 0; i < pixelCount; i++, p += 4)
            {
                byte b = lut[buffer[p]];
                byte g = lut[buffer[p + 1]];
                byte r = lut[buffer[p + 2]];

                if (applySaturation)
                {
                    // Rec.601 亮度权重：与肉眼对 RGB 的明度感知一致。
                    double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = (byte)Clamp(lum + (r - lum) * saturationFactor, 0, 255);
                    g = (byte)Clamp(lum + (g - lum) * saturationFactor, 0, 255);
                    b = (byte)Clamp(lum + (b - lum) * saturationFactor, 0, 255);
                }

                buffer[p] = b;
                buffer[p + 1] = g;
                buffer[p + 2] = r;
                // buffer[p + 3] 是 alpha，原样保留（PNG 透明区域不会变黑）。
            }
        }

        private static double Clamp(double value, double min, double max)
            => value < min ? min : value > max ? max : value;
    }
}
