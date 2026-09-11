using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// UI 联动：通过 UIAutomation 定位目标应用（首个支持 Word）的页面视口。
    /// 仅做定位与信息读取，不直接操纵目标应用；滚动监听与墨迹同步由上层负责。
    /// </summary>
    public static class UILinkageHelper
    {
        /// <summary>Word 进程名（不含 .exe）。</summary>
        private const string WordProcessName = "WINWORD";

        /// <summary>
        /// 查找 Word 主窗口元素（按进程名匹配顶级窗口）。找不到返回 null。
        /// </summary>
        public static AutomationElement FindWordWindow()
        {
            try
            {
                var procs = Process.GetProcessesByName(WordProcessName);
                if (procs.Length == 0) return null;

                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

                foreach (AutomationElement window in windows)
                {
                    int pid;
                    try { pid = window.Current.ProcessId; }
                    catch (ElementNotAvailableException) { continue; }

                    foreach (var proc in procs)
                    {
                        if (proc.Id == pid) return window;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                // 枚举过程中窗口被销毁，视为未找到。
            }

            return null;
        }

        /// <summary>
        /// 在窗口下查找页面视口。Word 的编辑区通常是 ControlType.Document。
        /// 具体特征（视口类型/自动化 ID）需随实机调教补充。
        /// </summary>
        public static AutomationElement FindWordViewport(AutomationElement window)
        {
            if (window == null) return null;
            try
            {
                return window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        /// <summary>读取元素对应原生窗口句柄，失败返回 Zero。</summary>
        public static IntPtr GetNativeWindowHandle(AutomationElement element)
        {
            if (element == null) return IntPtr.Zero;
            try
            {
                int handle = element.Current.NativeWindowHandle;
                return handle == 0 ? IntPtr.Zero : new IntPtr(handle);
            }
            catch (ElementNotAvailableException)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>读取元素屏幕物理像素矩形，失败返回 null。</summary>
        public static Rect? GetScreenRect(AutomationElement element)
        {
            if (element == null) return null;
            try
            {
                var r = element.Current.BoundingRectangle;
                if (r.IsEmpty) return null;
                return new Rect(r.Left, r.Top, r.Width, r.Height);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        /// <summary>
        /// 读取滚动偏移（内容坐标，向右/向下为正）与内容总大小。单位：物理像素。
        /// 优先走 ScrollPattern；Word 文档视口通常不暴露 ScrollPattern，需后续用
        /// Win32 GetScrollInfo / 消息钩子等方案配合实机调教，故此处仅尽力而为。
        /// </summary>
        public static bool TryGetScroll(AutomationElement viewport, out Vector offset, out Size extent)
        {
            offset = new Vector(0, 0);
            extent = new Size(0, 0);
            if (viewport == null) return false;

            try
            {
                var rect = viewport.Current.BoundingRectangle;
                var viewportSize = new Size(rect.Width, rect.Height);

                if (viewport.TryGetCurrentPattern(ScrollPattern.Pattern, out var patternObj)
                    && patternObj is ScrollPattern scroll)
                {
                    // 视口大小占内容的比例 => 内容总大小 = 视口 / (ViewSize / 100)。
                    extent = new Size(
                        scroll.Current.HorizontalViewSize > 0 ? viewportSize.Width * 100.0 / scroll.Current.HorizontalViewSize : viewportSize.Width,
                        scroll.Current.VerticalViewSize > 0 ? viewportSize.Height * 100.0 / scroll.Current.VerticalViewSize : viewportSize.Height);

                    offset = new Vector(
                        scroll.Current.HorizontalScrollPercent / 100.0 * extent.Width,
                        scroll.Current.VerticalScrollPercent / 100.0 * extent.Height);
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
                // 元素在读取过程中被销毁。
            }

            return false;
        }
    }
}