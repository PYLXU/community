using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// chrome 排除矩形收集（**屏幕 DIP** 坐标）。
    ///
    /// 实测教训：
    /// 1. 浮动栏/工具栏是动态构建的（FloatingToolbar/BoardToolbar），必须遍历视觉树收集。
    /// 2. 必须**排除全屏容器**（Main_Grid / GridBackgroundCoverHolder 等），否则整个画布被
    ///    排除，InkPresenter 收不到输入 → 写不了字。
    /// 3. PointToScreen / TranslatePoint 均返回 DIP，**不能再乘 dpiScale**（否则坐标放大漂移）。
    ///    覆盖窗口 NCHITTEST 的 lParam 是物理像素，除 dpiScale 后即为屏幕 DIP。
    /// </summary>
    internal sealed class WetInkRouter
    {
        /// <summary>矩形面积超过窗口面积此比例即视为容器，不排除。</summary>
        private const double MaxChromeAreaRatio = 0.5;

        /// <summary>
        /// 遍历主窗口视觉树收集 chrome 排除矩形（屏幕 DIP）。
        /// skipSubtreeRoot = 画布容器（其子树全部跳过）。
        /// </summary>
        public List<Rect> BuildAllChromeRects(
            Window mainWindow, Point screenOriginDip, DependencyObject skipSubtreeRoot)
        {
            var rects = new List<Rect>();
            if (mainWindow == null) return rects;

            var windowW = mainWindow.ActualWidth;
            var windowH = mainWindow.ActualHeight;
            var maxArea = windowW * windowH * MaxChromeAreaRatio;

            CollectChrome(mainWindow, mainWindow, screenOriginDip, skipSubtreeRoot,
                windowW, windowH, maxArea, rects);
            return rects;
        }

        private void CollectChrome(
            DependencyObject parent, Window mainWindow, Point screenOriginDip,
            DependencyObject skipRoot, double windowW, double windowH, double maxArea, List<Rect> rects)
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
                        // TranslatePoint 返回 DIP，直接加窗口屏幕原点即屏幕 DIP。
                        var local = fe.TranslatePoint(new Point(0, 0), mainWindow);
                        var x = screenOriginDip.X + local.X;
                        var y = screenOriginDip.Y + local.Y;
                        var w = fe.ActualWidth;
                        var h = fe.ActualHeight;

                        // 只收「真 chrome」：必须在窗口范围内，且面积不能接近整窗（那是容器）。
                        var inWindow = x >= screenOriginDip.X - 1 && y >= screenOriginDip.Y - 1
                            && x < screenOriginDip.X + windowW && y < screenOriginDip.Y + windowH;
                        var isContainer = w * h > maxArea;

                        if (w > 0 && h > 0 && inWindow && !isContainer)
                            rects.Add(new Rect(x, y, w, h));
                    }
                    catch
                    {
                        // 元素尚未参与布局，跳过。
                    }
                }

                CollectChrome(child, mainWindow, screenOriginDip, skipRoot,
                    windowW, windowH, maxArea, rects);
            }
        }
    }
}
