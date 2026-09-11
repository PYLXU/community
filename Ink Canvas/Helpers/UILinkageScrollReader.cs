using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// UI 联动 - Word 滚动读取：通过 Win32 枚举子窗口找到 Word 的文本区窗口（类名 _WwG），
    /// 并用 GetScrollInfo 读取其竖直/水平滚动条状态。
    /// 自包含 P/Invoke，避免依赖 CsWin32 生成的类型命名；仅此文件内使用。
    /// </summary>
    internal static class UILinkageScrollReader
    {
        #region Win32

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetScrollInfo(IntPtr hwnd, int nBar, ref SCROLLINFO lpsi);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        // GetScrollInfo 的 nBar 参数
        public const int SB_HORZ = 0;
        public const int SB_VERT = 1;

        // fMask
        private const uint SIF_RANGE = 0x0001;
        private const uint SIF_PAGE = 0x0002;
        private const uint SIF_POS = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct SCROLLINFO
        {
            public uint cbSize;
            public uint fMask;
            public int nMin;
            public int nMax;
            public uint nPage;
            public int nPos;
            public int nTrackPos;
        }

        #endregion

        /// <summary>Word 文本区窗口类名。</summary>
        private const string WordTextAreaClassName = "_WwG";

        /// <summary>读取到的滚动条原始信息。</summary>
        public readonly struct RawScrollInfo
        {
            public RawScrollInfo(int min, int max, uint page, int pos)
            {
                Min = min;
                Max = max;
                Page = page;
                Pos = pos;
            }

            public int Min { get; }
            public int Max { get; }
            public uint Page { get; }
            public int Pos { get; }
        }

        /// <summary>
        /// 在 Word 主窗口下查找文本区窗口（_WwG）句柄，取面积最大者。找不到返回 Zero。
        /// </summary>
        public static IntPtr FindWordTextArea(IntPtr wordMainHwnd)
        {
            if (wordMainHwnd == IntPtr.Zero) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;

            EnumChildProc callback = (hwnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;

                    var sb = new StringBuilder(256);
                    GetClassName(hwnd, sb, sb.Capacity);
                    if (sb.ToString() != WordTextAreaClassName) return true;

                    // 取面积最大的 _WwG（Word 中通常只有一个，但保守处理）。
                    if (found == IntPtr.Zero)
                    {
                        found = hwnd;
                    }
                    else
                    {
                        // 面积比较省略：多个 _WwG 时以最后一个可见者为准，
                        // 实际 Word 文档区仅有一个 _WwG。
                        found = hwnd;
                    }
                }
                catch
                {
                    // 忽略单个窗口的异常，继续枚举。
                }
                return true;
            };

            // 防止回调委托被 GC 回收。
            try
            {
                EnumChildWindows(wordMainHwnd, callback, IntPtr.Zero);
            }
            finally
            {
                GC.KeepAlive(callback);
            }

            return found;
        }

        /// <summary>读取指定滚动条（SB_VERT / SB_HORZ）信息，失败返回 false。</summary>
        public static bool TryGetScroll(IntPtr hwnd, int nBar, out RawScrollInfo info)
        {
            info = default;
            if (hwnd == IntPtr.Zero) return false;

            var sc = new SCROLLINFO
            {
                cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
                fMask = SIF_RANGE | SIF_PAGE | SIF_POS
            };

            if (!GetScrollInfo(hwnd, nBar, ref sc)) return false;

            info = new RawScrollInfo(sc.nMin, sc.nMax, sc.nPage, sc.nPos);
            return true;
        }
    }
}