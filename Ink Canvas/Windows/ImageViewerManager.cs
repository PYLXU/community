using Ink_Canvas.Helpers;
using Ink_Canvas.Windows.SettingsViews.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace Ink_Canvas.Windows
{
    /// <summary>
    /// 内置图片查看器的实例管理器（多实例）。
    /// <para>
    /// 每个 <see cref="ImageViewerWindow"/> 都是一个独立顶层窗口，因此任务栏会为每个窗口
    /// 显示实时缩略图预览，并能分别点击唤起到前台 —— 这正是「最小化后在任务栏按图片分别唤起」
    /// 的实现方式，不需要额外的 ITaskbarList3 代码。
    /// </para>
    /// <para>
    /// 是否复用窗口由设置 <c>ImageViewerSettings.MultiInstance</c> 决定；同一张图片已经打开时
    /// 无论哪种模式都只会把它的窗口唤到前台（不会产生重复窗口）。
    /// </para>
    /// </summary>
    public static class ImageViewerManager
    {
        private static readonly List<ImageViewerWindow> Windows = new List<ImageViewerWindow>();
        private static readonly object Gate = new object();

        /// <summary>当前打开的查看器窗口（只读快照）。</summary>
        public static IReadOnlyList<ImageViewerWindow> OpenWindows
        {
            get
            {
                lock (Gate) return Windows.ToList();
            }
        }

        /// <summary>
        /// 当前所有查看器画布（每张图片一块）。
        /// <para>
        /// 供宿主把主工具栏的批注状态同步过去 —— 与 UI 联动批注层走同一份
        /// <c>IndependentCanvasStateSync</c> 实现，因此「主工具栏批注时可以在图片上书写」
        /// 的行为与 UI 联动完全一致。
        /// </para>
        /// </summary>
        public static IReadOnlyList<Ink_Canvas.Controls.IndependentCanvas> OpenCanvases
        {
            get
            {
                var result = new List<Ink_Canvas.Controls.IndependentCanvas>();
                foreach (var window in OpenWindows)
                {
                    var canvas = window.Canvas;
                    if (canvas != null) result.Add(canvas);
                }

                return result;
            }
        }

        /// <summary>
        /// 通知所有查看器窗口「主工具栏当前是否处于批注状态」，用于刷新底栏「墨迹书写」按钮的高亮。
        /// 幂等：状态未变化时窗口自身会直接返回。
        /// </summary>
        /// <param name="annotating">主工具栏是否处于批注（可书写）状态。</param>
        public static void NotifyAnnotationStateChanged(bool annotating)
        {
            foreach (var window in OpenWindows)
            {
                try
                {
                    window.UpdateInkWritingState(annotating);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
        }

        /// <summary>
        /// 打开一张图片：已有同图窗口则唤到前台；多实例模式下新建窗口，否则复用最近一个窗口换图。
        /// 可在任意线程调用（内部会切回 UI 线程）。
        /// </summary>
        /// <param name="filePath">图片路径。</param>
        public static void OpenImage(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => OpenImage(filePath)));
                return;
            }

            string fullPath;
            try
            {
                fullPath = File.Exists(filePath) ? Path.GetFullPath(filePath) : filePath;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片查看器规范化路径失败（{filePath}）: {ex.Message}", LogHelper.LogType.Warning);
                fullPath = filePath;
            }

            try
            {
                var settings = SettingsManager.Settings?.ImageViewer;

                // 同一张图已经开着：直接唤起它的窗口，不重复开窗。
                var existing = FindWindowShowing(fullPath);
                if (existing != null)
                {
                    existing.ActivateFromTaskbar();
                    return;
                }

                ImageViewerWindow window = null;
                if (!(settings?.MultiInstance ?? true))
                {
                    // 单实例：复用最近打开的那个窗口换图（该窗口会先保存旧图的批注）。
                    lock (Gate) window = Windows.LastOrDefault(w => w.IsLoaded);
                }

                bool created = false;
                if (window == null)
                {
                    window = new ImageViewerWindow
                    {
                        ShowInTaskbar = settings?.ShowInTaskbar ?? true,
                    };
                    created = true;

                    // 先登记再显示：这样窗口即便在显示阶段就被关掉，Closed 也能正常注销。
                    Register(window);
                    window.Show();
                }

                window.LoadImage(fullPath);
                window.ActivateFromTaskbar();

                LogHelper.WriteLogToFile(
                    $"图片查看器{(created ? "新建窗口" : "复用窗口")}打开: {fullPath}", LogHelper.LogType.Event);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"图片查看器打开失败（{filePath}）: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        /// <summary>把改动后的窗口外观设置应用到所有已打开的查看器窗口。</summary>
        public static void ApplyAppearanceToOpenWindows()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ApplyAppearanceToOpenWindows));
                return;
            }

            var settings = SettingsManager.Settings?.ImageViewer;
            foreach (var window in OpenWindows)
            {
                try
                {
                    window.ApplyBackgroundColor();
                    if (settings != null) window.ShowInTaskbar = settings.ShowInTaskbar;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
        }

        /// <summary>关闭全部查看器窗口（退出程序前调用，保证批注被保存）。</summary>
        public static void CloseAll()
        {
            foreach (var window in OpenWindows)
            {
                try
                {
                    window.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }

            lock (Gate) Windows.Clear();
        }

        /// <summary>窗口关闭时注销自身。</summary>
        internal static void Unregister(ImageViewerWindow window)
        {
            if (window == null) return;
            lock (Gate) Windows.Remove(window);

            // 最近落笔记录指向已关闭的窗口时必须清掉，否则全局 Ctrl+Z 会去操作一块死画布。
            if (ReferenceEquals(_activeCanvas, window.Canvas)) ClearCanvasActivity();
        }

        // ---------------- 墨迹活动归属（供宿主分派全局撤销/重做） ----------------

        private static Ink_Canvas.Controls.IndependentCanvas _activeCanvas;
        private static DateTime _activeCanvasUtc = DateTime.MinValue;

        /// <summary>
        /// 记录「用户在查看器画布上落笔」。
        /// <para>
        /// 与 UI 联动画布完全同义：全局 Ctrl+Z / Ctrl+Y 是系统级热键（RegisterHotKey），
        /// 查看器窗口根本收不到，必须由宿主按「最近落笔画布」把撤销分派过来，
        /// 否则在图片上写错后按 Ctrl+Z 撤销掉的会是白板内容。
        /// </para>
        /// </summary>
        internal static void NoteCanvasActivity(Ink_Canvas.Controls.IndependentCanvas canvas)
        {
            if (canvas == null) return;
            _activeCanvas = canvas;
            _activeCanvasUtc = DateTime.UtcNow;
        }

        /// <summary>最近落笔的查看器画布及其时刻；调用方自行按新鲜期判定。</summary>
        internal static (Ink_Canvas.Controls.IndependentCanvas Canvas, DateTime Utc) ActiveCanvasSnapshot
            => (_activeCanvas, _activeCanvasUtc);

        private static void ClearCanvasActivity()
        {
            _activeCanvas = null;
            _activeCanvasUtc = DateTime.MinValue;
        }

        private static void Register(ImageViewerWindow window)
        {
            lock (Gate)
            {
                if (!Windows.Contains(window)) Windows.Add(window);
            }
        }

        private static ImageViewerWindow FindWindowShowing(string fullPath)
        {
            foreach (var window in OpenWindows)
            {
                if (string.IsNullOrEmpty(window.ImagePath)) continue;
                if (string.Equals(window.ImagePath, fullPath, StringComparison.OrdinalIgnoreCase)) return window;
            }

            return null;
        }
    }
}
