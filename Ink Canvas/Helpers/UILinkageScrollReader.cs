using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// UI 联动 - 滚动读取与控制：
    /// - Word：通过 Win32 枚举子窗口找到文本区窗口（类名 _WwG），用 GetScrollInfo 读取滚动条状态。
    ///   注意竖直轴走 UIA ScrollPattern（可靠），<b>横向轴必须以 _WwG 的水平滚动条为准</b>
    ///   ——ScrollPattern 的横向读数不可靠，见 <see cref="TryGetWordHorizontalScroll"/>。
    /// - SumatraPDF：文档渲染区为画布子窗口（类名 SUMATRA_PDF_CANVAS），同样用 GetScrollInfo 读取；
    ///   程序性滚动通过 WM_VSCROLL/WM_HSCROLL 的 LINE/PAGE 相对步进 + 读数反馈闭环实现绝对定位
    ///   （SumatraPDF 的滚动处理不支持 SB_THUMBPOSITION，且 overlay 滚动条模式下预置 nPos 无效，
    ///   因此采用「读数 → 步进 → 再读数」的闭环逼近目标位置，对标准/overlay/隐藏滚动条模式均适用）。
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
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

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

        // WM_VSCROLL / WM_HSCROLL 的滚动命令（两消息共用同一取值）
        private const int SB_LINEUP = 0;
        private const int SB_LINEDOWN = 1;
        private const int SB_PAGEUP = 2;
        private const int SB_PAGEDOWN = 3;
        private const int SB_TOP = 6;
        private const int SB_BOTTOM = 7;
        private const int SB_RIGHT = 7;

        private const uint WM_VSCROLL = 0x0115;
        private const uint WM_HSCROLL = 0x0114;

        // SendMessageTimeout 的 fuFlags：目标挂起时直接放弃，避免阻塞调用线程。
        private const uint SMTO_ABORTIFHUNG = 0x0008;
        private const uint ScrollMessageTimeoutMs = 200;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

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

            /// <summary>内容单位总数（nMax − nMin + 1）。</summary>
            public int Total => Max - Min + 1;

            /// <summary>可滚动上限（内容总数 − 视口单位数），即 nPos 的有效最大值。</summary>
            public int MaxPos => Math.Max(0, Total - (int)Page);
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

        /// <summary>
        /// 读取 Word 文本区（<c>_WwG</c>）的<b>水平</b>滚动条状态。
        /// <para>
        /// Word 的 UIA <c>ScrollPattern</c> 横向读数不可靠：横向常报
        /// <c>NoScroll</c>（-1）或 0、<c>HorizontalViewSize</c> 报 100，即使文档明显宽于视口。
        /// 沿用它推算横向偏移会塌成 0 —— 表现为独立画布横向滚动条弹回首位、墨迹不随横向滚动。
        /// 因此横向位置与范围必须取自滚动条本身：<c>nPos</c> 为位置、<c>nMax+1</c> 为内容总单位数、
        /// <c>nPage</c> 为视口占用的单位数（三者同一口径，换算 DIP 时用视口宽 / nPage 即可，
        /// 与单位本身是像素、缇还是列无关）。
        /// </para>
        /// 返回 false 表示窗口不可读，或没有有效范围（内容未超出视口 / overlay 滚动条未暴露范围）。
        /// </summary>
        public static bool TryGetWordHorizontalScroll(IntPtr textAreaHwnd, out RawScrollInfo info)
        {
            info = default;
            if (textAreaHwnd == IntPtr.Zero) return false;
            if (!TryGetScroll(textAreaHwnd, SB_HORZ, out var raw)) return false;

            // nPage == 0 或范围退化：视作「不可横向滚动」，交由页面实测帧表达（页面居中留白）。
            if (raw.Page == 0 || raw.Max <= raw.Min) return false;

            info = raw;
            return true;
        }

        #region SumatraPDF

        /// <summary>SumatraPDF 文档渲染画布子窗口类名。</summary>
        private const string SumatraCanvasClassName = "SUMATRA_PDF_CANVAS";

        /// <summary>
        /// 在 SumatraPDF 主窗口（SUMATRA_PDF_FRAME）下查找文档渲染画布（SUMATRA_PDF_CANVAS）句柄，
        /// 取面积最大的可见者。找不到返回 Zero。
        /// </summary>
        public static IntPtr FindSumatraCanvas(IntPtr sumatraMainHwnd)
        {
            if (sumatraMainHwnd == IntPtr.Zero) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            long foundArea = 0;

            EnumChildProc callback = (hwnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;

                    var sb = new StringBuilder(256);
                    GetClassName(hwnd, sb, sb.Capacity);
                    if (sb.ToString() != SumatraCanvasClassName) return true;

                    if (GetWindowRect(hwnd, out var r))
                    {
                        long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                        if (found == IntPtr.Zero || area > foundArea)
                        {
                            found = hwnd;
                            foundArea = area;
                        }
                    }
                    else if (found == IntPtr.Zero)
                    {
                        found = hwnd;
                    }
                }
                catch
                {
                    // 忽略单个窗口的异常，继续枚举。
                }
                return true;
            };

            try
            {
                EnumChildWindows(sumatraMainHwnd, callback, IntPtr.Zero);
            }
            finally
            {
                GC.KeepAlive(callback);
            }

            return found;
        }

        /// <summary>
        /// 读取 SumatraPDF 画布滚动状态（换算为与 Word ScrollPattern 同语义的百分比）：
        /// vp/hp 为滚动位置百分比，vs/hs 为视口占内容总尺寸的百分比，pageUnitsV 为
        /// SumatraPDF 写入的竖直视口单位数（nPage 原始值，供缩放度量使用）。
        /// <para>
        /// SumatraPDF 内部对「视口占用的滚动条单位数」（nPage）与内容总单位数（nMax+1）
        /// 使用的缩放口径可能不一致（随文档/显示模式呈 1×/2× 等系数差），按 nMax−nPage+1
        /// 估算的可滚动上限会偏大。传入校准得到的真实视口单位数（<paramref name="effUnitsV"/>/
        /// <paramref name="effUnitsH"/>，见 <see cref="TryCalibrateSumatraViewportUnits"/>）可修正；
        /// 传 0 时退回 nPage 估计。
        /// </para>
        /// </summary>
        public static bool TryGetSumatraScrollPercents(
            IntPtr canvasHwnd, double effUnitsV, double effUnitsH,
            out double vp, out double hp, out double vs, out double hs, out double pageUnitsV)
        {
            vp = hp = vs = hs = pageUnitsV = double.NaN;
            if (canvasHwnd == IntPtr.Zero) return false;

            if (!TryGetScrollState(canvasHwnd, SB_VERT, out int vMax, out int vTotal, out int vPos, out int vPage))
                return false;
            if (!TryGetScrollState(canvasHwnd, SB_HORZ, out int hMax, out int hTotal, out int hPos, out int hPage))
                return false;

            pageUnitsV = vPage;
            double effV = effUnitsV > 0 ? effUnitsV : vPage;
            double effH = effUnitsH > 0 ? effUnitsH : hPage;
            int vMaxPos = Math.Max(0, vTotal - (int)Math.Round(effV));
            int hMaxPos = Math.Max(0, hTotal - (int)Math.Round(effH));

            vp = vMaxPos > 0 ? ClampPercent(vPos * 100.0 / vMaxPos) : 0.0;
            hp = hMaxPos > 0 ? ClampPercent(hPos * 100.0 / hMaxPos) : 0.0;
            vs = vTotal > 0 && effV > 0 ? ClampPercent(effV * 100.0 / vTotal) : 0.0;
            hs = hTotal > 0 && effH > 0 ? ClampPercent(effH * 100.0 / hTotal) : 0.0;
            return true;
        }

        /// <summary>
        /// 把 SumatraPDF 画布滚动到指定百分比（null 表示该轴不动）。
        /// <paramref name="effUnitsV"/>/<paramref name="effUnitsH"/> 为校准得到的真实视口单位数
        /// （0 = 退回 nPage 估计）。内部为读数反馈闭环，同步阻塞至到位，耗时操作请在后台线程调用。
        /// </summary>
        public static bool TrySetSumatraScrollPercent(
            IntPtr canvasHwnd, double? vp, double? hp, double effUnitsV = 0, double effUnitsH = 0)
        {
            if (canvasHwnd == IntPtr.Zero) return false;

            bool ok = true;
            if (vp.HasValue) ok &= ScrollToPercent(canvasHwnd, SB_VERT, true, vp.Value, effUnitsV);
            if (hp.HasValue) ok &= ScrollToPercent(canvasHwnd, SB_HORZ, false, hp.Value, effUnitsH);
            return ok;
        }

        /// <summary>
        /// 校准 SumatraPDF 滚动条单位与视口像素的对应关系（返回「单位/像素」比）。
        /// <para>
        /// 做法：记住当前位置 → 竖直/水平分别发 BOTTOM/RIGHT 到端点 → 读取实际停靠位置
        /// （= 内容单位总数 − 真实视口单位数）→ 步进回原位。由此得到精确的「真实视口单位数 /
        /// 视口像素数」比，修正 nPage 的口径偏差；窗口尺寸变化时该比值不变，缩放变化时按比值
        /// 递推，均无需重新校准。存在一次端点往返（约几十毫秒），请在绑定/换文档等画布尚未
        /// 展示时调用。
        /// </para>
        /// </summary>
        public static bool TryCalibrateSumatraViewportUnits(
            IntPtr canvasHwnd, double viewWidthPx, double viewHeightPx,
            out double unitsPerPxV, out double unitsPerPxH)
        {
            unitsPerPxV = unitsPerPxH = 0;
            if (canvasHwnd == IntPtr.Zero || viewWidthPx <= 0 || viewHeightPx <= 0) return false;

            if (!TryGetScrollState(canvasHwnd, SB_VERT, out int vMax, out _, out int vPos, out int vPage))
                return false;
            bool hasH = TryGetScrollState(canvasHwnd, SB_HORZ, out int hMax, out _, out int hPos, out int hPage);

            bool vScrollable = vPage > 0 && vMax - vPage + 1 > 0;
            bool hScrollable = hasH && hPage > 0 && hMax - hPage + 1 > 0;
            if (!vScrollable && !hScrollable)
            {
                // 内容未超出视口：偏移恒为 0，单位比按 nPage 估计即可（仅缩放度量使用）。
                unitsPerPxV = vPage / viewHeightPx;
                unitsPerPxH = hasH ? hPage / viewWidthPx : 0;
                return true;
            }

            if (vScrollable && !SendScrollCommand(canvasHwnd, true, SB_BOTTOM)) return false;
            if (hScrollable && !SendScrollCommand(canvasHwnd, false, SB_RIGHT)) return false;

            // 等待停靠位置稳定（渲染线程可能异步回写实际视口位置）。
            int vBottom = vScrollable ? ReadSettledPos(canvasHwnd, SB_VERT) : 0;
            int hRight = hScrollable ? ReadSettledPos(canvasHwnd, SB_HORZ) : 0;

            // 步进回原位（端点快速路径通常一两步内完成）。
            bool restored = true;
            if (vScrollable) restored &= ScrollToPos(canvasHwnd, SB_VERT, true, vPos);
            if (hScrollable && hasH) restored &= ScrollToPos(canvasHwnd, SB_HORZ, false, hPos);
            if (!restored) return false;

            unitsPerPxV = vScrollable ? (double)(vMax + 1 - vBottom) / viewHeightPx : vPage / viewHeightPx;
            unitsPerPxH = hScrollable ? (double)(hMax + 1 - hRight) / viewWidthPx : (hasH ? hPage / viewWidthPx : 0);
            return unitsPerPxV > 0 || unitsPerPxH > 0;
        }

        /// <summary>读取单轴位置并等待其稳定（两次读数一致或超时为止）。</summary>
        private static int ReadSettledPos(IntPtr hwnd, int nBar)
        {
            const int SettleMs = 30;
            const int MaxWaits = 5;

            if (!TryGetScrollState(hwnd, nBar, out _, out _, out int pos, out _)) return -1;
            for (int i = 0; i < MaxWaits; i++)
            {
                System.Threading.Thread.Sleep(SettleMs);
                if (!TryGetScrollState(hwnd, nBar, out _, out _, out int next, out _)) return pos;
                if (next == pos) return pos;
                pos = next;
            }
            return pos;
        }

        private static double ClampPercent(double value)
            => Math.Min(100.0, Math.Max(0.0, value));

        /// <summary>
        /// 读取单轴滚动状态：max 为 nMax（内容单位总数 − 1），total 为内容单位总数（nMax + 1），
        /// pos 为当前位置，page 为 SumatraPDF 写入的视口单位数（口径可能与真实视口有差，
        /// 校准前仅作估计用）。
        /// </summary>
        private static bool TryGetScrollState(IntPtr hwnd, int nBar, out int max, out int total, out int pos, out int page)
        {
            max = total = pos = page = 0;
            if (hwnd == IntPtr.Zero) return false;

            var sc = new SCROLLINFO
            {
                cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
                fMask = SIF_RANGE | SIF_PAGE | SIF_POS
            };
            if (!GetScrollInfo(hwnd, nBar, ref sc)) return false;

            page = (int)sc.nPage;
            pos = sc.nPos;
            total = sc.nMax + 1;
            max = sc.nMax;
            return true;
        }

        private static bool ScrollToPercent(IntPtr hwnd, int nBar, bool vertical, double percent, double effUnits = 0)
        {
            if (!TryGetScrollState(hwnd, nBar, out int max, out int total, out int pos, out int page))
                return false;

            if (page <= 0) return true; // 无可滚动范围。

            // 真实可滚动上限：优先用校准值（内容单位总数 − 真实视口单位数）。
            int maxPos = effUnits > 0
                ? Math.Max(0, total - (int)Math.Round(effUnits))
                : Math.Max(0, max - page + 1);
            if (maxPos <= 0) return true; // 内容未超出视口，无需滚动（视为已到位）。

            int target = (int)Math.Round(ClampPercent(percent) / 100.0 * maxPos);
            return ScrollToPos(hwnd, nBar, vertical, target, maxPos);
        }

        /// <summary>
        /// 通过「相对步进 + 读数反馈」逼近目标滚动位置。
        /// <para>
        /// SumatraPDF（3.6+ 默认 smart/overlay 滚动条）的滚动处理从 overlay 控件读取当前位置，
        /// 外部 SetScrollInfo 预置不参与；程序性滚动只能发 LINE/PAGE/TOP/BOTTOM 相对消息：
        /// - 距目标超过半页用整页步进（移动量恰为 nPage，每步距离至少减半）；
        /// - 否则用行步进（移动量为行高，默认 16、随 DPI/设置变化，按实测修正）；
        /// - 端点用 TOP/BOTTOM 绝对定位一步到位。
        /// 行高网格不可细分，收敛容差为半行（±8 单位左右，约数像素，与 Word 路径
        /// ScrollPattern 0.4% 容差相当）。
        /// </para>
        /// </summary>
        private static bool ScrollToPos(IntPtr hwnd, int nBar, bool vertical, int target, int maxPosTrue = 0)
        {
            // 上限覆盖最坏情况：行高较小时（如单页适配模式下行高为 1）需更多次行步进。
            const int MaxSteps = 192;
            const int SettleMs = 25;
            int line = 16; // 行高（SumatraPDF 默认 scrollLineAmount=16，随 DPI/设置变化），按实测修正。

            for (int i = 0; i < MaxSteps; i++)
            {
                if (!TryGetScrollState(hwnd, nBar, out int max, out _, out int pos, out int page))
                    return false;

                int claimedMaxPos = Math.Max(0, max - page + 1);
                int maxPos = maxPosTrue > 0 ? Math.Min(maxPosTrue, claimedMaxPos) : claimedMaxPos;
                target = Math.Min(Math.Max(0, target), maxPos);
                int diff = pos - target; // >0 需向上滚，<0 需向下滚
                if (Math.Abs(diff) <= Math.Max(1, line / 2)) return true;

                bool scrollUp = diff > 0;

                // 端点快速路径：TOP/BOTTOM 为绝对定位，一步精确到位。
                if ((target <= 0 && scrollUp) || (target >= maxPos && !scrollUp))
                {
                    if (!SendScrollCommand(hwnd, vertical, target <= 0 ? SB_TOP : SB_BOTTOM)) return false;
                    continue;
                }

                // 步进选择：距目标超过半页用整页步进（距离减半以上），否则行步进逼近。
                int command;
                if (page > 0 && Math.Abs(diff) > page / 2)
                    command = scrollUp ? SB_PAGEUP : SB_PAGEDOWN;
                else
                    command = scrollUp ? SB_LINEUP : SB_LINEDOWN;

                if (!SendScrollCommand(hwnd, vertical, command)) return false;
                if (!TryGetScrollState(hwnd, nBar, out _, out _, out int after, out _)) return false;

                if (after == pos)
                {
                    // 无位移：可能渲染线程尚未回写实际视口位置，稍候重读一次。
                    System.Threading.Thread.Sleep(SettleMs);
                    if (!TryGetScrollState(hwnd, nBar, out _, out _, out after, out _)) return false;
                    if (after == pos) return Math.Abs(pos - target) <= Math.Max(1, line); // 无法继续滚动
                }
                else if (command == SB_LINEUP || command == SB_LINEDOWN)
                {
                    int measured = Math.Abs(after - pos);
                    if (measured > 0 && measured <= page) line = measured;
                }
            }

            // 步数用尽：接受半行残差（网格限制下的最优可达）。
            return TryGetScrollState(hwnd, nBar, out _, out _, out int finalPos, out _) &&
                   Math.Abs(finalPos - target) <= Math.Max(1, line / 2);
        }

        /// <summary>向 SumatraPDF 画布发送一条滚动消息（同步等待处理完成，便于随后读数）。</summary>
        private static bool SendScrollCommand(IntPtr hwnd, bool vertical, int command)
        {
            try
            {
                // 同步等待 SumatraPDF 处理完滚动（其内部会 ScrollYTo 并同步滚动条状态），
                // 挂起时放弃以避免阻塞调用线程。
                return SendMessageTimeout(
                    hwnd, vertical ? WM_VSCROLL : WM_HSCROLL, (IntPtr)command, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, ScrollMessageTimeoutMs, out _) != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}