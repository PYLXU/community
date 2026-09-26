using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// 图片批注的持久化：按「图片全路径」把墨迹存到配置目录，重新打开同一张图时恢复。
    /// <para>
    /// <b>坐标约定</b>：落盘的笔迹点一律在<b>图片像素坐标系</b>里，不带任何视图变换。
    /// 保存时用当前视图矩阵的逆把视口坐标换算回去，载入时再用当前视图矩阵正变换回来 ——
    /// 这样批注永远锚在图片的同一处内容上，与保存当时的缩放/旋转/窗口大小无关。
    /// </para>
    /// <para>
    /// 不把批注写到图片旁边（不污染用户的文件夹），而是集中放在
    /// <c>&lt;程序目录&gt;/Configs/ImageViewerInk/</c> 下，文件名 = 路径哈希 + 原始文件名（便于人工辨认）。
    /// </para>
    /// </summary>
    public static class ImageViewerAnnotationStore
    {
        private static readonly string StoreDirectory =
            Path.Combine(App.RootPath ?? AppDomain.CurrentDomain.BaseDirectory, "Configs", "ImageViewerInk");

        /// <summary>按图片路径算出批注文件全路径。</summary>
        public static string GetAnnotationFilePath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;

            try
            {
                string fullPath = Path.GetFullPath(imagePath);
                // 用规范化后的全路径做哈希，保证「同一文件的不同写法」落到同一个批注文件。
                string key = fullPath.ToLowerInvariant();
                string hash;
                using (var sha1 = SHA1.Create())
                {
                    byte[] digest = sha1.ComputeHash(Encoding.UTF8.GetBytes(key));
                    hash = BitConverter.ToString(digest).Replace("-", string.Empty).Substring(0, 16);
                }

                string safeName = SanitizeFileName(Path.GetFileName(fullPath));
                return Path.Combine(StoreDirectory, hash + "_" + safeName + ".isf");
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"计算批注文件路径失败（{imagePath}）: {ex.Message}", LogHelper.LogType.Warning);
                return null;
            }
        }

        /// <summary>
        /// 把视口坐标的笔迹换算回图片坐标并落盘。
        /// </summary>
        /// <param name="imagePath">图片路径。</param>
        /// <param name="strokes">画布上的笔迹（视口坐标）。</param>
        /// <param name="viewMatrix">视口 = viewMatrix × 图片 的当前视图矩阵。</param>
        /// <param name="removeWhenEmpty">笔迹为空时是否删除已有存档。</param>
        public static void Save(string imagePath, StrokeCollection strokes, Matrix viewMatrix, bool removeWhenEmpty = true)
        {
            string path = GetAnnotationFilePath(imagePath);
            if (path == null) return;

            try
            {
                if (strokes == null || strokes.Count == 0)
                {
                    if (removeWhenEmpty && File.Exists(path)) File.Delete(path);
                    return;
                }

                // 复制一份再换算，绝不能动到画布上正在显示的笔迹。
                var imageSpace = strokes.Clone();
                if (viewMatrix.HasInverse)
                {
                    var inverse = viewMatrix;
                    inverse.Invert();
                    foreach (Stroke stroke in imageSpace) stroke.Transform(inverse, false);
                }

                Directory.CreateDirectory(StoreDirectory);
                using (var stream = File.Create(path))
                {
                    imageSpace.Save(stream);
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"保存图片批注失败（{imagePath}）: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 读取某张图片的批注，并把点集从图片坐标变换到当前视图坐标。
        /// </summary>
        /// <param name="imagePath">图片路径。</param>
        /// <param name="viewMatrix">当前视图矩阵（图片 → 视口）。</param>
        /// <returns>可直接加入画布的笔迹集合；没有存档时返回空集合。</returns>
        public static StrokeCollection Load(string imagePath, Matrix viewMatrix)
        {
            var result = new StrokeCollection();
            string path = GetAnnotationFilePath(imagePath);
            if (path == null || !File.Exists(path)) return result;

            try
            {
                StrokeCollection stored;
                using (var stream = File.OpenRead(path))
                {
                    stored = new StrokeCollection(stream);
                }

                foreach (Stroke stroke in stored)
                {
                    var clone = stroke.Clone();
                    clone.Transform(viewMatrix, false);
                    result.Add(clone);
                }

                LogHelper.WriteLogToFile(
                    $"已载入图片批注（{result.Count} 笔）: {imagePath}", LogHelper.LogType.Event);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"载入图片批注失败（{imagePath}）: {ex.Message}", LogHelper.LogType.Warning);
                result.Clear();
            }

            return result;
        }

        /// <summary>删除某张图片的批注存档。</summary>
        public static void Delete(string imagePath)
        {
            try
            {
                string path = GetAnnotationFilePath(imagePath);
                if (path != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"删除图片批注失败（{imagePath}）: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "image";

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            }

            // 文件名过长会让「哈希_文件名.isf」超出 MAX_PATH，截断即可（哈希已保证唯一性）。
            var safe = sb.ToString();
            return safe.Length > 48 ? safe.Substring(safe.Length - 48) : safe;
        }
    }
}
