using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// 内置图片查看器支持的图片格式清单与运行时能力探测。
    /// <para>
    /// 清单是「常见图片格式」的固化列表；某个格式在用户机器上到底能不能解码，
    /// 由 WIC 已安装的解码器（<see cref="System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders"/>）
    /// 在运行时探测。二者取交集才是「可绑定」的格式 —— 例如 WebP/HEIF/AVIF 只在装了对应
    /// 系统扩展的机器上可用，没装时设置页会明确标注「系统不支持解码」并禁止绑定，
    /// 而不是等到用户双击图片才失败。
    /// </para>
    /// </summary>
    public static class ImageViewerFormats
    {
        /// <summary>一个候选图片格式。</summary>
        public sealed class FormatInfo
        {
            /// <summary>扩展名（含点、小写），如 <c>.png</c>。</summary>
            public string Extension { get; init; }

            /// <summary>面向用户的格式名（不含扩展名）。</summary>
            public string DisplayName { get; init; }

            /// <summary>本机 WIC 是否能解码该格式。</summary>
            public bool IsDecodable { get; init; }

            /// <summary>设置页列表里显示的一行文本，如 <c>PNG（.png）</c>。</summary>
            public string MenuText => $"{DisplayName}（{Extension}）";
        }

        /// <summary>
        /// 一组同格式的扩展名别名（如 <c>.jpg</c>/<c>.jpeg</c>/<c>.jfif</c> 都属 JPEG）。
        /// 设置页按组显示一行，勾选即绑定该组的全部扩展名，避免同一个格式在列表里重复出现多行。
        /// </summary>
        public sealed class FormatGroup
        {
            /// <summary>面向用户的格式名（不含扩展名）。</summary>
            public string DisplayName { get; init; }

            /// <summary>该组包含的全部扩展名（含点、小写，第一个是「主」扩展名）。</summary>
            public IReadOnlyList<string> Extensions { get; init; }

            /// <summary>该组中是否至少有一个扩展名可在本机解码。</summary>
            public bool IsDecodable { get; init; }

            /// <summary>扩展名列表的显示文本，如 <c>.jpg / .jpeg / .jfif</c>。</summary>
            public string ExtensionsText => string.Join(" / ", Extensions);
        }

        /// <summary>
        /// 固化的常见图片格式清单（按组；顺序即设置页显示顺序）。
        /// </summary>
        private static readonly (string DisplayName, string[] Extensions)[] Catalog =
        {
            ("PNG", new[] { ".png" }),
            ("JPEG", new[] { ".jpg", ".jpeg", ".jfif", ".jpe" }),
            ("BMP", new[] { ".bmp", ".dib" }),
            ("GIF", new[] { ".gif" }),
            ("TIFF", new[] { ".tif", ".tiff" }),
            ("图标", new[] { ".ico" }),
            ("JPEG XR", new[] { ".wdp", ".jxr" }),
            ("WebP", new[] { ".webp" }),
            ("HEIF", new[] { ".heic", ".heif" }),
            ("AVIF", new[] { ".avif" }),
        };

        /// <summary>
        /// 本进程内可解码的扩展名集合（懒加载，探测失败时退化为「WPF 原生必然支持的格式」，
        /// 保证设置页不会因为探测异常而整页空白）。
        /// </summary>
        private static readonly Lazy<HashSet<string>> DecodableExtensions = new Lazy<HashSet<string>>(ProbeDecodableExtensions);

        /// <summary>WPF/PresentationCore 自带解码器必然支持的格式（探测失败时的兜底集合）。</summary>
        private static readonly string[] BuiltInFallbackExtensions =
        {
            ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif",
            ".tif", ".tiff", ".ico", ".wdp", ".jxr"
        };

        /// <summary>本机可解码的扩展名集合（只读快照）。</summary>
        public static IReadOnlyCollection<string> Decodable => DecodableExtensions.Value;

        /// <summary>常见图片格式清单，按格式分组（含本机可解码性标记）。设置页按此渲染。</summary>
        public static IReadOnlyList<FormatGroup> CommonGroups { get; } = BuildCommonGroups();

        /// <summary>常见图片格式清单（展平到每个扩展名一条，含本机可解码性标记）。</summary>
        public static IReadOnlyList<FormatInfo> CommonFormats { get; } = BuildCommonFormats();

        /// <summary>可绑定的常见图片格式（本机确实能解码的那些）。</summary>
        public static IReadOnlyList<FormatInfo> BindableFormats =>
            CommonFormats.Where(f => f.IsDecodable).ToList();

        /// <summary>本机可绑定的全部扩展名（按清单顺序）。</summary>
        public static IReadOnlyList<string> BindableExtensions =>
            CommonFormats.Where(f => f.IsDecodable).Select(f => f.Extension).ToList();

        /// <summary>已知扩展名 → 面向用户的格式名（用于写注册表时的文件类型描述）。</summary>
        public static string DisplayNameFor(string extension)
        {
            var ext = Normalize(extension);
            foreach (var group in CommonGroups)
            {
                if (group.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                    return group.DisplayName;
            }

            return ext.Length > 1 ? ext.TrimStart('.').ToUpperInvariant() : "图片";
        }

        private static IReadOnlyList<FormatGroup> BuildCommonGroups()
        {
            var decodable = DecodableExtensions.Value;
            var list = new List<FormatGroup>(Catalog.Length);
            foreach (var (displayName, extensions) in Catalog)
            {
                list.Add(new FormatGroup
                {
                    DisplayName = displayName,
                    Extensions = extensions,
                    IsDecodable = extensions.Any(e => decodable.Contains(e)),
                });
            }
            return list;
        }

        private static IReadOnlyList<FormatInfo> BuildCommonFormats()
        {
            var decodable = DecodableExtensions.Value;
            var list = new List<FormatInfo>();
            foreach (var (displayName, extensions) in Catalog)
            {
                foreach (var extension in extensions)
                {
                    list.Add(new FormatInfo
                    {
                        Extension = extension,
                        DisplayName = displayName,
                        IsDecodable = decodable.Contains(extension),
                    });
                }
            }
            return list;
        }

        private static HashSet<string> ProbeDecodableExtensions()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var codec in System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders())
                {
                    var extensions = codec.FilenameExtension;
                    if (string.IsNullOrWhiteSpace(extensions)) continue;

                    foreach (var raw in extensions.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var ext = raw.Trim();
                        if (ext.StartsWith("*", StringComparison.Ordinal)) ext = ext.Substring(1);
                        if (ext.Length == 0) continue;
                        set.Add(Normalize(ext));
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(
                    $"图片格式解码能力探测失败，回退到内置格式集合: {ex.Message}", LogHelper.LogType.Warning);
            }

            if (set.Count == 0)
            {
                foreach (var ext in BuiltInFallbackExtensions) set.Add(ext);
            }
            else
            {
                // 少数精简系统上 BMP/PNG 解码器被裁剪，但 WPF 仍能渲染；保留内置集合作为并集，
                // 避免把「本程序确实能显示」的格式误标为不可用。
                foreach (var ext in BuiltInFallbackExtensions) set.Add(ext);
            }

            return set;
        }

        /// <summary>把扩展名规范化为「含点 + 全小写」，输入可以带点也可以不带。</summary>
        public static string Normalize(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return string.Empty;
            var ext = extension.Trim().ToLowerInvariant();
            if (!ext.StartsWith(".", StringComparison.Ordinal)) ext = "." + ext;
            return ext;
        }

        /// <summary>判断某扩展名是否属于「常见图片格式」清单。</summary>
        public static bool IsKnownFormat(string extension)
        {
            var ext = Normalize(extension);
            if (ext.Length == 0) return false;
            return Catalog.Any(c => c.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>把 <c>#AARRGGBB</c> / <c>#RRGGBB</c> / 颜色名解析为颜色，失败返回回退值。</summary>
        public static System.Windows.Media.Color ParseColorOr(string value, System.Windows.Media.Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try
            {
                if (System.Windows.Media.ColorConverter.ConvertFromString(value.Trim())
                    is System.Windows.Media.Color color)
                {
                    return color;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            return fallback;
        }

        /// <summary>判断本机是否能解码该扩展名。</summary>
        public static bool IsDecodable(string extension)
        {
            var ext = Normalize(extension);
            if (ext.Length == 0) return false;
            return DecodableExtensions.Value.Contains(ext);
        }

        /// <summary>
        /// 某扩展名是否应当由内置图片查看器接管：属于常见图片格式清单、本机可解码，
        /// 且用户已在设置里把它绑定到本程序。
        /// </summary>
        public static bool IsBoundAndDecodable(string extension, Ink_Canvas.ImageViewerSettings settings)
        {
            if (settings == null || !settings.IsEnabled) return false;
            var ext = Normalize(extension);
            if (ext.Length == 0) return false;
            if (!IsDecodable(ext)) return false;
            return settings.BoundExtensions != null
                   && settings.BoundExtensions.Any(e => string.Equals(Normalize(e), ext, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 判断一个文件路径是否应当由内置图片查看器打开（存在 + 扩展名已绑定 + 本机可解码）。
        /// </summary>
        public static bool ShouldOpenInViewer(string filePath, Ink_Canvas.ImageViewerSettings settings)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            try
            {
                if (!File.Exists(filePath)) return false;
            }
            catch
            {
                return false;
            }

            return IsBoundAndDecodable(Path.GetExtension(filePath), settings);
        }

        /// <summary>
        /// 把已绑定的扩展名集合整理为「含点、小写、按清单顺序、去重」的列表，
        /// 供设置页显示与写注册表使用。
        /// </summary>
        public static List<string> NormalizeBoundList(IEnumerable<string> extensions)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (extensions != null)
            {
                foreach (var ext in extensions)
                {
                    var normalized = Normalize(ext);
                    if (normalized.Length > 1) set.Add(normalized);
                }
            }

            var ordered = new List<string>();
            foreach (var (_, groupExtensions) in Catalog)
            {
                foreach (var extension in groupExtensions)
                {
                    if (set.Remove(extension)) ordered.Add(extension);
                }
            }

            // 清单之外的扩展名（用户手动输入的）排在后面，保持字母序以便稳定显示。
            ordered.AddRange(set.OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
            return ordered;
        }

        /// <summary>给「打开图片」文件对话框用的筛选器字符串（按已绑定格式生成）。</summary>
        public static string BuildOpenFileDialogFilter(Ink_Canvas.ImageViewerSettings settings)
        {
            var bound = settings?.BoundExtensions != null
                ? NormalizeBoundList(settings.BoundExtensions)
                : new List<string>();

            if (bound.Count == 0) bound = NormalizeBoundList(BuiltInFallbackExtensions);

            var pattern = string.Join(";", bound.Select(e => "*" + e));
            var label = string.Join("、", bound.Select(e => e.TrimStart('.').ToUpperInvariant()));
            return $"{label}|{pattern}|所有文件|*.*";
        }

        /// <summary>
        /// 资源管理器风格的「自然排序」比较（<c>2.png</c> 排在 <c>10.png</c> 之前）。
        /// shlwapi 不可用时退化为不区分大小写的序数比较，保证「上一张/下一张」仍有确定顺序。
        /// </summary>
        public static int NaturalCompare(string left, string right)
        {
            try
            {
                return StrCmpLogicalW(left ?? string.Empty, right ?? string.Empty);
            }
            catch
            {
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);

        /// <summary>
        /// 同目录下、可被查看器打开的图片（自然排序，当前文件固定在首位）；
        /// 目录不可读时返回只含当前文件的列表。
        /// <para>
        /// 这里刻意<b>不</b>按「已绑定扩展名」过滤：上一张/下一张是查看器内部的浏览行为，
        /// 不是文件关联接管，浏览同目录下所有本机能解码的图片才符合看图软件的习惯。
        /// 文件关联的接管判定仍然严格按设置走（见 <see cref="ShouldOpenInViewer"/>）。
        /// </para>
        /// </summary>
        public static List<string> EnumerateSiblingImages(string filePath)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(filePath)) return result;

            try
            {
                var fullPath = Path.GetFullPath(filePath);
                result.Add(fullPath);

                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return result;

                var candidates = new List<string>();
                foreach (var candidate in Directory.EnumerateFiles(directory))
                {
                    if (string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsDecodable(Path.GetExtension(candidate))) continue;
                    candidates.Add(candidate);
                }

                candidates.Sort((a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
                result.AddRange(candidates);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile(
                    $"枚举同目录图片失败（{filePath}）: {ex.Message}", LogHelper.LogType.Warning);
            }

            return result;
        }
    }
}
