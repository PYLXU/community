using Ink_Canvas.Controls;
using Ink_Canvas.Helpers;
using Ink_Canvas.Windows.SettingsViews.Helpers;
using System;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ink_Canvas
{
    /// <summary>
    /// UI 联动：批注模式下，通过 UIAutomation 定位 Word 页面视口，在其上叠加一块
    /// <see cref="IndependentCanvas"/> 并持续跟踪（位置/宽高 + 滚动）。首个支持 Word。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>独立画布在主画布上方的 Z 序（低于橡皮擦反馈层 1000）。</summary>
        private const int UILinkageCanvasZIndex = 500;

        private static readonly TimeSpan UILinkagePollInterval = TimeSpan.FromMilliseconds(150);

        private IndependentCanvas _uiLinkageCanvas;
        private DispatcherTimer _uiLinkageTimer;
        private IntPtr _uiLinkageWordTextArea;

        private static bool UILinkageIsEnabled
        {
            get
            {
                var s = SettingsManager.Settings?.UILinkage;
                return s != null && s.IsEnabled && s.WordEnabled;
            }
        }

        private static bool UILinkageIsDebugEnabled
        {
            get
            {
                var s = SettingsManager.Settings?.UILinkage;
                return s != null && s.IsDebugEnabled;
            }
        }

        /// <summary>最近一次书写起笔采集点（画布 DIP 坐标）；未书写过为 NaN。</summary>
        private Point _uiLinkageSamplePointDip = new Point(double.NaN, double.NaN);
        /// <summary>采集点经 UIAutomation 命中的组件描述。</summary>
        private string _uiLinkageSampleHit = "—";

        /// <summary>启动轮询定时器（Window_Loaded 时调用一次）。</summary>
        private void EnsureUILinkageTimer()
        {
            if (_uiLinkageTimer != null) return;

            _uiLinkageTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = UILinkagePollInterval
            };
            _uiLinkageTimer.Tick += (_, _) => UpdateUILinkage();
            _uiLinkageTimer.Start();
        }

        private void UpdateUILinkage()
        {
            UpdateUILinkageDebugHud();

            bool shouldTrack = UILinkageIsEnabled && IsAnnotationModeActive();
            if (!shouldTrack)
            {
                RemoveUILinkageCanvas();
                return;
            }

            if (_uiLinkageCanvas == null) EnsureUILinkageCanvas();
            if (_uiLinkageCanvas == null) return;

            if (!TryGetWordViewportDip(out var rect))
            {
                RemoveUILinkageCanvas();
                return;
            }

            PositionUILinkageCanvas(rect);
            SyncUILinkageScroll(rect.Size);
        }

        private void EnsureUILinkageCanvas()
        {
            if (_uiLinkageCanvas != null || InkCanvasGridForInkReplay == null) return;

            _uiLinkageCanvas = new IndependentCanvas
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Visible
            };
            Panel.SetZIndex(_uiLinkageCanvas, UILinkageCanvasZIndex);

            // 同步主画布笔触与工具，保证书写观感一致。
            _uiLinkageCanvas.InkSurface.EditingMode = InkCanvasEditingMode.Ink;
            if (inkCanvas?.DefaultDrawingAttributes != null)
                _uiLinkageCanvas.InkSurface.DefaultDrawingAttributes = inkCanvas.DefaultDrawingAttributes.Clone();

            // 滚动条显示设置项（暂用默认值，待接入设置页）。
            _uiLinkageCanvas.ShowVerticalScrollBar = true;
            _uiLinkageCanvas.ShowHorizontalScrollBar = true;

            InkCanvasGridForInkReplay.Children.Add(_uiLinkageCanvas);
        }

        private void RemoveUILinkageCanvas()
        {
            _uiLinkageWordTextArea = IntPtr.Zero;
            if (_uiLinkageCanvas == null) return;

            if (InkCanvasGridForInkReplay != null && InkCanvasGridForInkReplay.Children.Contains(_uiLinkageCanvas))
                InkCanvasGridForInkReplay.Children.Remove(_uiLinkageCanvas);

            _uiLinkageCanvas = null;
        }

        private bool TryGetWordViewportDip(out Rect dipRect)
        {
            dipRect = default;

            var window = UILinkageHelper.FindWordWindow();
            var nativeHandle = UILinkageHelper.GetNativeWindowHandle(window);
            if (nativeHandle == IntPtr.Zero) return false;

            var viewport = UILinkageHelper.FindWordViewport(window);
            var screenRect = UILinkageHelper.GetScreenRect(viewport);
            if (screenRect == null) return false;

            var rect = screenRect.Value;
            if (rect.Width <= 0 || rect.Height <= 0) return false;

            // 缓存 Word 文本区句柄，供滚动读取（失败时下一轮继续尝试）。
            if (_uiLinkageWordTextArea == IntPtr.Zero)
                _uiLinkageWordTextArea = UILinkageScrollReader.FindWordTextArea(nativeHandle);

            // 屏幕物理像素 → 窗口 DIP 坐标。
            var dpi = VisualTreeHelper.GetDpi(this);
            var topLeft = PointFromScreen(new Point(rect.Left, rect.Top));
            dipRect = new Rect(
                topLeft.X,
                topLeft.Y,
                rect.Width / dpi.DpiScaleX,
                rect.Height / dpi.DpiScaleY);

            return true;
        }

        private void PositionUILinkageCanvas(Rect dipRect)
        {
            if (_uiLinkageCanvas == null) return;
            _uiLinkageCanvas.Margin = new Thickness(dipRect.Left, dipRect.Top, 0, 0);
            _uiLinkageCanvas.Width = dipRect.Width;
            _uiLinkageCanvas.Height = dipRect.Height;
        }

        private void SyncUILinkageScroll(Size viewportDpi)
        {
            if (_uiLinkageCanvas == null) return;
            if (_uiLinkageWordTextArea == IntPtr.Zero) return;

            var offset = new Vector(0, 0);
            var extent = viewportDpi;

            // 滚动条单位为“行/列”，按视口换算成 DIP 供墨迹平移。比例可后续实机调教。
            if (UILinkageScrollReader.TryGetScroll(_uiLinkageWordTextArea, UILinkageScrollReader.SB_VERT, out var v))
            {
                double scale = v.Page > 0 ? viewportDpi.Height / v.Page : 1.0;
                offset.Y = v.Pos * scale;
                extent.Height = viewportDpi.Height + (v.Max - v.Min) * scale;
            }

            if (UILinkageScrollReader.TryGetScroll(_uiLinkageWordTextArea, UILinkageScrollReader.SB_HORZ, out var h))
            {
                double scale = h.Page > 0 ? viewportDpi.Width / h.Page : 1.0;
                offset.X = h.Pos * scale;
                extent.Width = viewportDpi.Width + (h.Max - h.Min) * scale;
            }

            _uiLinkageCanvas.SetScrollInfo(viewportDpi, extent, offset);
        }

        /// <summary>
        /// 刷新画布层调试面板（HUD）的可见性与内容。
        /// 由轮询定时器与墨迹提交路径各自调用，保证信息实时更新。
        /// </summary>
        private void UpdateUILinkageDebugHud()
        {
            if (UILinkageDebugHud == null || UILinkageDebugHudText == null) return;

            if (!UILinkageIsDebugEnabled)
            {
                UILinkageDebugHud.Visibility = Visibility.Collapsed;
                return;
            }

            UILinkageDebugHud.Visibility = Visibility.Visible;

            bool wordTracked = UILinkageIsEnabled;
            bool annotating = IsAnnotationModeActive();
            bool foundViewport = TryGetWordViewportDip(out var rect);

            var sb = new StringBuilder();
            sb.AppendLine("UI 联动调试");
            sb.AppendLine($"Word 联动: {(wordTracked ? "开" : "关")}  批注: {(annotating ? "是" : "否")}");

            if (foundViewport)
                sb.AppendLine($"视口(DIP): {rect.X:F0},{rect.Y:F0}  {rect.Width:F0}x{rect.Height:F0}");
            else
                sb.AppendLine("视口(DIP): 未找到");

            if (!double.IsNaN(_uiLinkageSamplePointDip.X))
                sb.AppendLine($"采集点(DIP): {_uiLinkageSamplePointDip.X:F0},{_uiLinkageSamplePointDip.Y:F0}");
            else
                sb.AppendLine("采集点(DIP): 尚未书写");

            sb.Append($"命中组件: {_uiLinkageSampleHit}");

            UILinkageDebugHudText.Text = sb.ToString();
        }

        /// <summary>
        /// 墨迹提交后处理：起始点即采集点。取 <see cref="Stroke.StylusPoints"/> 首点，
        /// 换算为屏幕坐标后经 UIAutomation 定位命中的应用组件，并在 HUD 中展示。
        /// </summary>
        internal void HandleUILinkageDebugAfterStroke(Stroke stroke)
        {
            try
            {
                if (!UILinkageIsDebugEnabled) return;
                if (stroke == null || stroke.StylusPoints.Count == 0) return;
                if (inkCanvas == null) return;

                var sp = stroke.StylusPoints[0];
                var canvasPt = new Point(sp.X, sp.Y);
                _uiLinkageSamplePointDip = canvasPt;

                var screenPt = inkCanvas.PointToScreen(canvasPt);
                _uiLinkageSampleHit = DescribeElementAtPoint(screenPt);

                UpdateUILinkageDebugHud();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"UI联动采集点定位失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>对屏幕物理像素点做 UIAutomation 命中测试，返回组件简述。</summary>
        private static string DescribeElementAtPoint(Point screenPt)
        {
            try
            {
                var el = AutomationElement.FromPoint(screenPt);
                if (el == null) return "(未命中)";

                string type = SafeRead(() => el.Current.ControlType?.ProgrammaticName);
                string name = SafeRead(() => el.Current.Name);
                string id = SafeRead(() => el.Current.AutomationId);
                string cls = SafeRead(() => el.Current.ClassName);

                int dot = type?.LastIndexOf('.') ?? -1;
                if (dot >= 0 && dot + 1 < type.Length) type = type.Substring(dot + 1);

                return $"{type ?? "?"} | {name ?? ""} | #{id ?? ""} | {cls ?? ""}";
            }
            catch
            {
                return "(定位失败)";
            }
        }

        private static string SafeRead(Func<string> read)
        {
            try { return read(); }
            catch { return null; }
        }
    }
}