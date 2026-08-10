using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// chrome 排除矩形收集。浮动栏/工具栏是动态构建的（FloatingToolbar/BoardToolbar），
    /// 无法用固定元素列表枚举 —— 改为遍历主窗口视觉树，收集所有可见且可命中的
    /// FrameworkElement（跳过画布容器子树）。对任何动态 UI 都通用。
    /// </summary>
    internal sealed class WetInkRouter
    {
        public WetInkRouter()
        {
        }

        /// <summary>
        /// 遍历主窗口视觉树收集 chrome 排除矩形（屏幕像素）。
        /// skipSubtreeRoot = 画布容器（其子树全部跳过，画布要接收墨迹不排除）。
        /// </summary>
        public List<Rect> BuildAllChromeRects(
            Window mainWindow, double dpiScale, Point screenOriginPx, DependencyObject skipSubtreeRoot)
        {
            var rects = new List<Rect>();
            if (mainWindow == null) return rects;
            CollectChrome(mainWindow, mainWindow, dpiScale, screenOriginPx, skipSubtreeRoot, rects);
            return rects;
        }

        private void CollectChrome(
            DependencyObject parent, Window mainWindow, double dpiScale, Point screenOriginPx,
            DependencyObject skipRoot, List<Rect> rects)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (ReferenceEquals(child, skipRoot)) continue; // 跳过画布容器子树

                var fe = child as FrameworkElement;
                if (fe != null && fe.Visibility == Visibility.Visible && fe.IsHitTestVisible
                    && fe.ActualWidth > 0 && fe.ActualHeight > 0 && !string.IsNullOrEmpty(fe.Name))
                {
                    try
                    {
                        var local = fe.TranslatePoint(new Point(0, 0), mainWindow);
                        var x = screenOriginPx.X + local.X * dpiScale;
                        var y = screenOriginPx.Y + local.Y * dpiScale;
                        var w = fe.ActualWidth * dpiScale;
                        var h = fe.ActualHeight * dpiScale;
                        if (w > 0 && h > 0 && x >= 0 && y >= 0 && x < 4000 && y < 4000)
                            rects.Add(new Rect(x, y, w, h));
                    }
                    catch
                    {
                        // 元素尚未参与布局，跳过。
                    }
                }

                CollectChrome(child, mainWindow, dpiScale, screenOriginPx, skipRoot, rects);
            }
        }
    }
}
