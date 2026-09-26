using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// UI 联动 - 后方窗口定位：在指定屏幕物理像素点查找「批注层后方」的顶层窗口，
    /// 沿 Z 序自顶向下遍历，跳过自身进程窗口后返回首个覆盖该点的可见窗口句柄。
    /// 同时提供被遮挡区域计算：枚举 Z 序上位于目标窗口之上的窗口并求交。
    /// 判定「可见」时剔除：不可见、最小化、DWM Cloaked、全透明分层窗口（alpha=0）
    /// 以及点击穿透窗口（WS_EX_TRANSPARENT，仅用于后方窗口查找——点击会落到其下方）。
    /// 自包含 P/Invoke，供 UI 联动实时识别（前台窗口）与调试捕捉组件使用。
    /// </summary>
    internal static class UILinkageWindowFinder
    {
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLayeredWindowAttributes(
            IntPtr hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        private const uint GW_HWNDNEXT = 2;
        private const uint GW_HWNDPREV = 3;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int DWMWA_CLOAKED = 14;
        private const uint LWA_ALPHA = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static int GetWindowLong(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8 ? (int)GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

        /// <summary>DWM Cloaked（UWP 挂起、虚拟桌面隐藏等）的窗口对用户不可见。</summary>
        private static bool IsCloaked(IntPtr hwnd)
        {
            try
            {
                return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                    && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 窗口是否对用户「有效可见」。
        /// <paramref name="treatClickThroughAsInvisible"/> 为 true 时额外把 WS_EX_TRANSPARENT
        /// （点击穿透）窗口视为不可见——用于后方窗口查找（点击会落到穿透窗口下方）；
        /// 遮挡计算应传 false，因为穿透窗口仍会渲染内容。
        /// </summary>
        public static bool IsWindowEffectivelyVisible(IntPtr hwnd, bool treatClickThroughAsInvisible)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
                    return false;

                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if (treatClickThroughAsInvisible && (exStyle & WS_EX_TRANSPARENT) != 0)
                    return false;

                if ((exStyle & WS_EX_LAYERED) != 0
                    && GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags))
                {
                    if ((flags & LWA_ALPHA) != 0 && alpha == 0)
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 返回能覆盖给定屏幕点、且不属于自身进程的可见顶层窗口句柄；找不到返回 <see cref="IntPtr.Zero"/>。
        /// 不可见、最小化、Cloaked、全透明分层与点击穿透的窗口会被跳过。
        /// </summary>
        public static IntPtr FindWindowBehind(int screenX, int screenY, IntPtr selfHwnd)
        {
            try
            {
                uint selfPid = 0;
                if (selfHwnd != IntPtr.Zero)
                    GetWindowThreadProcessId(selfHwnd, out selfPid);

                var pt = new POINT { X = screenX, Y = screenY };
                IntPtr hwnd = WindowFromPoint(pt);

                int guard = 0;
                while (hwnd != IntPtr.Zero && guard++ < 128)
                {
                    uint pid = 0;
                    GetWindowThreadProcessId(hwnd, out pid);

                    if (pid != 0 && pid != selfPid
                        && IsWindowEffectivelyVisible(hwnd, treatClickThroughAsInvisible: true)
                        && IsPointInWindow(hwnd, screenX, screenY))
                        return hwnd;

                    hwnd = GetWindow(hwnd, GW_HWNDNEXT);
                }
            }
            catch
            {
                // 忽略异常，返回 Zero 表示未找到。
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 计算 <paramref name="targetRect"/>（屏幕物理像素）中被 Z 序上位于
        /// <paramref name="capturedHwnd"/> 之上的其它窗口覆盖的区域集合。
        /// 自身进程窗口（批注层）不计入遮挡；透明/不可见窗口（Cloaked、alpha=0 分层窗口）
        /// 与点击穿透窗口（WS_EX_TRANSPARENT，如录屏软件的全屏覆盖层——点击会落到下层，
        /// 也不应造成视觉性输入裁切）均不计入遮挡。
        /// </summary>
        public static List<System.Windows.Rect> GetOccludedRects(
            IntPtr capturedHwnd, IntPtr selfHwnd, System.Windows.Rect targetRect)
        {
            var result = new List<System.Windows.Rect>();
            if (capturedHwnd == IntPtr.Zero || targetRect.IsEmpty || targetRect.Width <= 0 || targetRect.Height <= 0)
                return result;

            try
            {
                uint selfPid = 0;
                if (selfHwnd != IntPtr.Zero)
                    GetWindowThreadProcessId(selfHwnd, out selfPid);

                int targetLeft = (int)targetRect.Left;
                int targetTop = (int)targetRect.Top;
                int targetRight = (int)targetRect.Right;
                int targetBottom = (int)targetRect.Bottom;

                // 自 capturedHwnd 向上（GW_HWNDPREV）遍历 Z 序，收集覆盖目标区域的其它窗口。
                IntPtr hwnd = GetWindow(capturedHwnd, GW_HWNDPREV);
                int guard = 0;
                while (hwnd != IntPtr.Zero && guard++ < 256)
                {
                    uint pid = 0;
                    GetWindowThreadProcessId(hwnd, out pid);

                    if (pid != 0 && pid != selfPid)
                    {
                        // 先用 GetWindowRect 做矩形粗筛，再做可见性判定：
                        // IsWindowEffectivelyVisible 内含 DwmGetWindowAttribute（到 DWM 的跨进程调用），
                        // 而本方法由 250ms 轮询每拍调用、Z 序上多数窗口与目标并不相交。
                        // 粗筛后只对真正相交的少数窗口做昂贵判定，结果与逐窗口判定完全一致。
                        if (GetWindowRect(hwnd, out var r)
                            && r.Right > targetLeft && r.Left < targetRight
                            && r.Bottom > targetTop && r.Top < targetBottom
                            && IsWindowEffectivelyVisible(hwnd, treatClickThroughAsInvisible: true))
                        {
                            int w = r.Right - r.Left;
                            int h = r.Bottom - r.Top;
                            if (w > 0 && h > 0)
                            {
                                int l = Math.Max(targetLeft, r.Left);
                                int t = Math.Max(targetTop, r.Top);
                                int rr = Math.Min(targetRight, r.Right);
                                int b = Math.Min(targetBottom, r.Bottom);
                                if (rr > l && b > t)
                                    result.Add(new System.Windows.Rect(l, t, rr - l, b - t));
                            }
                        }
                    }

                    hwnd = GetWindow(hwnd, GW_HWNDPREV);
                }
            }
            catch
            {
                // 忽略异常，返回空集合表示无遮挡。
            }

            return result;
        }

        private static bool IsPointInWindow(IntPtr hwnd, int x, int y)
        {
            if (!GetWindowRect(hwnd, out var r)) return false;
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return false;
            return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
        }

        /// <summary>窗口是否处于最小化状态。</summary>
        public static bool IsMinimized(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return IsIconic(hwnd); }
            catch { return false; }
        }

        /// <summary>读取窗口屏幕物理像素矩形（失败或尺寸非法返回 false）。</summary>
        public static bool TryGetWindowRect(IntPtr hwnd, out System.Windows.Rect rect)
        {
            rect = System.Windows.Rect.Empty;
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                if (!GetWindowRect(hwnd, out var r)) return false;
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return false;
                rect = new System.Windows.Rect(r.Left, r.Top, w, h);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>窗口句柄是否仍然有效（未被销毁）。</summary>
        public static bool IsWindowAlive(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return IsWindow(hwnd); }
            catch { return false; }
        }

        /// <summary>当前前台（焦点）窗口句柄；读取失败返回 <see cref="IntPtr.Zero"/>。</summary>
        public static IntPtr GetForegroundWindowHandle()
        {
            try { return GetForegroundWindow(); }
            catch { return IntPtr.Zero; }
        }

        /// <summary>读取窗口所属进程 ID；读取失败返回 0。</summary>
        public static uint GetWindowProcessId(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                return pid;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>读取鼠标当前位置（屏幕物理像素）；读取失败返回 false。</summary>
        public static bool TryGetCursorPosition(out int screenX, out int screenY)
        {
            screenX = 0;
            screenY = 0;
            try
            {
                if (!GetCursorPos(out var pt)) return false;
                screenX = pt.X;
                screenY = pt.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读取窗口类名（用于识别目标应用主窗口，排除其对话框/子窗口）；读取失败返回空串。
        /// </summary>
        public static string GetWindowClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            try
            {
                var buffer = new System.Text.StringBuilder(256);
                int length = GetClassName(hwnd, buffer, buffer.Capacity);
                return length > 0 ? buffer.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 查找覆盖给定屏幕点、且属于自身进程的「其它」窗口（排除 <paramref name="excludeHwnd"/>，
        /// 即主窗口自身）。用于鼠标抓取模式下判定点击是否落在自身弹出面板/浮动栏/设置窗口上，
        /// 保证抓取期间仍能操作本应用界面。点击穿透窗口（WS_EX_TRANSPARENT）视为不接收点击，不计入。
        /// 找不到返回 <see cref="IntPtr.Zero"/>。
        /// </summary>
        public static IntPtr FindOwnProcessWindowOverPoint(int screenX, int screenY, IntPtr excludeHwnd)
        {
            if (excludeHwnd == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                uint selfPid = GetWindowProcessId(excludeHwnd);
                if (selfPid == 0) return IntPtr.Zero;

                var pt = new POINT { X = screenX, Y = screenY };
                IntPtr hwnd = WindowFromPoint(pt);

                int guard = 0;
                while (hwnd != IntPtr.Zero && guard++ < 128)
                {
                    if (hwnd != excludeHwnd
                        && GetWindowProcessId(hwnd) == selfPid
                        && IsWindowEffectivelyVisible(hwnd, treatClickThroughAsInvisible: true)
                        && IsPointInWindow(hwnd, screenX, screenY))
                        return hwnd;

                    hwnd = GetWindow(hwnd, GW_HWNDNEXT);
                }
            }
            catch
            {
                // 忽略异常，返回 Zero 表示该点不在自身其它窗口上。
            }

            return IntPtr.Zero;
        }
    }
}
