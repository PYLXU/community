using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 命中测试与 chrome 排除。结构式：覆盖窗口的 SetWindowRgn 已经按这些矩形把 chrome
    /// 从墨迹窗口里挖掉，指针在 chrome 区域自然落到主窗口 —— 无旧系统的白名单穿透问题。
    /// 这里只负责收集 chrome 元素在「主窗口客户端 DIP」坐标下的几何矩形。
    /// </summary>
    internal sealed class WetInkRouter
    {
        private readonly Func<FrameworkElement>[] _chromeElementProviders;

        public WetInkRouter(params Func<FrameworkElement>[] chromeElementProviders)
        {
            _chromeElementProviders = chromeElementProviders ?? Array.Empty<Func<FrameworkElement>>();
        }

        /// <summary>
        /// 收集 chrome 排除矩形（**屏幕 DIP** 坐标）。元素不可见/未布局则跳过。
        /// 屏幕坐标与 WM_NCHITTEST 的 lParam（屏幕像素）一致，区域裁剪时再换算客户端坐标。
        /// </summary>
        public List<Rect> BuildExclusionRects(Window owner)
        {
            var rects = new List<Rect>();
            if (owner == null) return rects;

            foreach (var provider in _chromeElementProviders)
            {
                FrameworkElement element = null;
                try { element = provider?.Invoke(); }
                catch { continue; }

                if (element == null) continue;
                if (element.Visibility != Visibility.Visible) continue;
                if (element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;

                try
                {
                    var topLeft = element.PointToScreen(new Point(0, 0));
                    var rect = new Rect(
                        topLeft.X,
                        topLeft.Y,
                        element.ActualWidth,
                        element.ActualHeight);
                    if (rect.Width > 0 && rect.Height > 0)
                        rects.Add(rect);
                }
                catch
                {
                    // 元素尚未参与布局，跳过。
                }
            }

            return rects;
        }
    }
}
