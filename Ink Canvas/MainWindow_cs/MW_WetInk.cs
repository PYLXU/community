using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Ink_Canvas.Helpers;
using Ink_Canvas.Ink.WetInk;

namespace Ink_Canvas
{
    /// <summary>
    /// 新墨迹引擎与 MainWindow 的集成层。
    ///
    /// 职责边界：只做「接线」——设置读取 → 引擎装配 → 命中区域解析 → 干墨提交回 inkCanvas。
    /// 输入采集/分类/几何/渲染全部在 Ink/WetInk/ 内，不在此重复实现。
    ///
    /// 与旧墨迹互斥：引擎挂载期间 inkCanvas 物理 EditingMode 恒为 None，
    /// 逻辑笔型由 <see cref="_wetInkLogicalTool"/> 记录，避免 WPF 内建 DynamicRenderer 二次采集。
    /// </summary>
    public partial class MainWindow : IWetInkRouteHost, IWetInkControllerSink
    {
        private WetInkSessionManager _wetInkSessions;
        private WetInkTouchClassifier _wetInkClassifier;
        private WetInkController _wetInkController;
        private WetInkWindowHost _wetInkHost;
        private WetInkPointerInputSource _wetInkInput;
        private HwndSource _wetInkHwndSource;

        private bool _wetInkPipelineActive;
        private bool _wetInkDisabledAfterFailure;
        private InkCanvasEditingMode _wetInkLogicalTool = InkCanvasEditingMode.Ink;

        /// <summary>新墨迹引擎当前是否可用（已挂载且未因故障降级）。</summary>
        internal bool IsWetInkPipelineAvailable =>
            _wetInkPipelineActive && !_wetInkDisabledAfterFailure;

        /// <summary>用户是否选择了新墨迹引擎（与旧墨迹互斥）。</summary>
        private static bool IsWetInkEngineSelected =>
            Settings?.Canvas?.UseLegacyInkSystem == false;

        // ==================================================================
        // lifecycle
        // ==================================================================

        /// <summary>
        /// 尝试装配新墨迹引擎。设置为旧墨迹或曾故障降级时直接返回 false。
        /// 任何一步失败都完整回滚，绝不留下半初始化状态。
        /// </summary>
        internal bool TryStartWetInkPipeline()
        {
            if (_wetInkPipelineActive || _wetInkDisabledAfterFailure)
                return _wetInkPipelineActive;

            if (!IsWetInkEngineSelected)
                return false;

            try
            {
                var source = PresentationSource.FromVisual(this) as HwndSource;
                if (source == null || source.Handle == IntPtr.Zero)
                    return false;

                _wetInkHwndSource = source;

                var target = BuildWetInkTargetSnapshot();
                if (!target.IsValid)
                {
                    _wetInkHwndSource = null;
                    return false;
                }

                var (dpiX, dpiY) = GetWetInkDpiScales();

                _wetInkSessions = new WetInkSessionManager();
                _wetInkClassifier = new WetInkTouchClassifier(this);

                _wetInkHost = new WetInkWindowHost(
                    fatalErrorCallback: ex => OnWetInkFatalError(ex?.Message ?? "unknown"),
                    retiredCallback: _ => { });
                if (!_wetInkHost.Start(target))
                {
                    ShutdownWetInkPipeline();
                    return false;
                }

                _wetInkController = new WetInkController(
                    _wetInkSessions,
                    _wetInkHost.Mailbox,
                    _wetInkClassifier,
                    this,
                    predictionEnabled: true);
                _wetInkController.SetDpi(dpiX, dpiY);
                _wetInkController.SetCurrentStyle(BuildWetInkStyle());
                _wetInkController.ConfigureStraighten(
                    Settings?.Canvas?.PauseStraightenLine == true,
                    Settings?.Canvas?.PauseStraightenDelay ?? 300);

                _wetInkHwndSource = source;
                _wetInkInput = new WetInkPointerInputSource(
                    source,
                    OnWetInkPointerInput,
                    () => GetWetInkDpiScales().X,
                    () => GetWetInkDpiScales().Y);

                _wetInkPipelineActive = true;
                SyncWetInkConfiguration();
                SyncWetInkWithEditingMode(inkCanvas?.EditingMode ?? InkCanvasEditingMode.Ink);

                LogHelper.WriteLogToFile("新墨迹引擎已挂载（WM_POINTER + D3D11/DirectComposition）");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"新墨迹引擎挂载失败：{ex.Message}", LogHelper.LogType.Error);
                ShutdownWetInkPipeline();
                return false;
            }
        }

        /// <summary>拆卸引擎并释放全部资源。可重复调用。</summary>
        internal void ShutdownWetInkPipeline()
        {
            _wetInkPipelineActive = false;

            try
            {
                _wetInkInput?.Dispose();
                _wetInkInput = null;

                _wetInkController?.Dispose();
                _wetInkController = null;

                _wetInkHost?.Dispose();
                _wetInkHost = null;

                _wetInkSessions = null;
                _wetInkClassifier = null;
                _wetInkHwndSource = null;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"新墨迹引擎拆卸异常：{ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>不可恢复故障：降级回旧墨迹，恢复物理 EditingMode 为 Ink。</summary>
        private void OnWetInkFatalError(string reason)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_wetInkDisabledAfterFailure)
                    return;

                _wetInkDisabledAfterFailure = true;
                LogHelper.WriteLogToFile($"新墨迹引擎故障降级：{reason}", LogHelper.LogType.Error);

                ShutdownWetInkPipeline();
                RestoreLegacyInkAfterWetInkFailure();
            }));
        }

        private void RestoreLegacyInkAfterWetInkFailure()
        {
            try
            {
                if (inkCanvas == null)
                    return;

                if (_wetInkLogicalTool == InkCanvasEditingMode.Ink &&
                    inkCanvas.EditingMode == InkCanvasEditingMode.None)
                {
                    inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                }

                EnsureRealtimeStylusPipelineBinding();
                _wetInkLogicalTool = inkCanvas.EditingMode;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"恢复旧墨迹失败：{ex.Message}", LogHelper.LogType.Error);
            }
        }

        // ==================================================================
        // input entry
        // ==================================================================

        /// <summary>
        /// 输入源回调（UI 线程）。只拦截写墨/擦除类工具；
        /// 选择/图形/漫游/光标一律不拦，交还 WPF 正常处理。
        /// </summary>
        private bool OnWetInkPointerInput(WetInkPointerPhase phase, WetInkPointerBatch batch)
        {
            if (!IsWetInkPipelineAvailable)
                return false;

            var route = ResolveLogicalTool();
            if (route != WetInkRoute.Ink &&
                route != WetInkRoute.PointErase &&
                route != WetInkRoute.StrokeErase)
            {
                return false;
            }

            _wetInkController?.OnPointerInput(phase, batch);
            return true;
        }

        /// <summary>更新逻辑笔型与物理 EditingMode 的一致性（引擎挂载时写墨类恒为 None）。</summary>
        internal void SyncWetInkWithEditingMode(InkCanvasEditingMode mode)
        {
            if (!IsWetInkPipelineAvailable)
            {
                _wetInkLogicalTool = mode;
                return;
            }

            // 递归保护：我们把物理模式强制成 None 会再次触发本事件，
            // 该递归事件不得覆盖刚记录的逻辑笔型。
            if (_wetInkForcingPhysicalNone)
                return;

            _wetInkLogicalTool = mode;

            // 写墨/擦除类工具：物理 EditingMode 强制 None，由引擎接管。
            if (IsWetInkWritingMode(mode) &&
                inkCanvas != null &&
                inkCanvas.EditingMode != InkCanvasEditingMode.None)
            {
                _wetInkForcingPhysicalNone = true;
                try
                {
                    inkCanvas.EditingMode = InkCanvasEditingMode.None;
                }
                finally
                {
                    _wetInkForcingPhysicalNone = false;
                }
            }
            // 其它工具（Select/None）：物理 EditingMode 保持真实值，交还 WPF。
        }

        private bool _wetInkForcingPhysicalNone;

        private static bool IsWetInkWritingMode(InkCanvasEditingMode mode)
        {
            return mode == InkCanvasEditingMode.Ink ||
                   mode == InkCanvasEditingMode.EraseByPoint ||
                   mode == InkCanvasEditingMode.EraseByStroke;
        }

        /// <summary>从设置同步引擎配置（样式、停顿拉直、DPI）。</summary>
        private void SyncWetInkConfiguration()
        {
            _wetInkController?.SetCurrentStyle(BuildWetInkStyle());
            _wetInkController?.ConfigureStraighten(
                Settings?.Canvas?.PauseStraightenLine == true,
                Settings?.Canvas?.PauseStraightenDelay ?? 300);

            var (dpiX, dpiY) = GetWetInkDpiScales();
            _wetInkController?.SetDpi(dpiX, dpiY);

            var target = BuildWetInkTargetSnapshot();
            _wetInkHost?.UpdateTarget(target);
        }

        // ==================================================================
        // IWetInkControllerSink
        // ==================================================================

        void IWetInkControllerSink.OnStrokeCompleted(WetInkCommitPayload payload)
        {
            try
            {
                CommitWetInkStrokeToDryLayer(payload);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"湿墨干提交失败：{ex.Message}", LogHelper.LogType.Error);
            }
        }

        void IWetInkControllerSink.OnStrokeCanceled(long sessionId)
        {
            // 控制器已清理会话；这里无需动作。
        }

        void IWetInkControllerSink.OnSessionRetired(long sessionId)
        {
            // 会话已从管理器移除；无需动作。
        }

        WetInkRouteDecision IWetInkControllerSink.QueryDownRoute(
            WetInkPointerBatch batch,
            double contactWidthDip,
            bool useFingerMode)
        {
            if (!IsWetInkPipelineAvailable)
                return new WetInkRouteDecision(WetInkRoute.DeferToWpf, false, true);

            if (IsCurrentPageFrozen)
                return new WetInkRouteDecision(WetInkRoute.BlockedFrozen, true, false);

            var hitZone = HitTest(batch.SamplesNewestFirst[0].X, batch.SamplesNewestFirst[0].Y);
            if (hitZone != WetInkHitZone.CanvasSurface)
                return new WetInkRouteDecision(WetInkRoute.DeferToWpf, false, true);

            var tool = ResolveLogicalTool();
            switch (tool)
            {
                case WetInkRoute.PointErase:
                case WetInkRoute.StrokeErase:
                    return new WetInkRouteDecision(tool, true, false);

                case WetInkRoute.Shape:
                    return new WetInkRouteDecision(WetInkRoute.Shape, false, true);

                case WetInkRoute.Select:
                case WetInkRoute.BoardRoam:
                    return new WetInkRouteDecision(tool, false, true);

                case WetInkRoute.Ink:
                    break;

                default:
                    return new WetInkRouteDecision(WetInkRoute.DeferToWpf, false, true);
            }

            // Ink：触摸先做手掌分类。
            if (batch.InputKind == WetInkInputKind.Touch &&
                _wetInkClassifier.TryGetPalmEraserWidth(contactWidthDip, useFingerMode, out var eraserWidth))
            {
                return new WetInkRouteDecision(
                    WetInkRoute.PointErase, true, false, palmEraserWidthDip: eraserWidth);
            }

            return new WetInkRouteDecision(WetInkRoute.Ink, true, false);
        }

        WetInkRouteContext IWetInkControllerSink.QueryRouteContext()
        {
            return BuildRouteContext();
        }

        // ==================================================================
        // dry commit
        // ==================================================================

        private void CommitWetInkStrokeToDryLayer(WetInkCommitPayload payload)
        {
            if (payload == null || payload.Samples == null || payload.Samples.Length < 2)
                return;
            if (inkCanvas == null)
                return;

            var origin = GetWetInkCanvasOrigin();

            // 构建 StylusPointCollection（窗口客户区 DIP → inkCanvas 本地）。
            var points = new StylusPointCollection();
            for (var i = 0; i < payload.Samples.Length; i++)
            {
                var s = payload.Samples[i];
                if (s.IsPredicted)
                    continue;
                points.Add(new StylusPoint(
                    s.X - origin.X,
                    s.Y - origin.Y,
                    payload.Style.IgnorePressure ? 0.5f : s.Pressure));
            }

            if (points.Count < 2)
                return;

            var attributes = BuildWetInkDrawingAttributes(payload.Style);

            var stroke = new Stroke(points, attributes);

            // 激光笔标记：ProcessCommittedStroke 会据此走渐隐。
            if (payload.Style.RenderMode == WetInkRenderMode.Laser &&
                !stroke.ContainsPropertyData(InkFadeManager.LaserRenderModeGuid))
            {
                stroke.AddPropertyData(InkFadeManager.LaserRenderModeGuid, true);
            }

            // 加入干层（触发 TimeMachine 撤销历史）+ 复用既有后处理。
            inkCanvas.Strokes.Add(stroke);
            ProcessCommittedStroke(stroke);

            // 湿墨等到干墨合成完成后再撤（防烘干闪变）。
            ScheduleWetInkRetirement(payload.SessionId);
        }

        /// <summary>
        /// WPF 合成帧 fence：干墨加入 Strokes 后等 N 帧合成完成，再通知控制器撤湿墨。
        /// </summary>
        private void ScheduleWetInkRetirement(long sessionId)
        {
            var remaining = 5;
            EventHandler handler = null;
            handler = (s, e) =>
            {
                try
                {
                    if (--remaining > 0)
                        return;
                    CompositionTarget.Rendering -= handler;
                    _wetInkController?.OnWpfFrameRendered(sessionId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WetInk] retirement fence: {ex.Message}");
                }
            };
            CompositionTarget.Rendering += handler;
        }

        // ==================================================================
        // IWetInkRouteHost
        // ==================================================================

        WetInkHitZone IWetInkRouteHost.HitTest(double xDip, double yDip) => HitTest(xDip, yDip);

        private WetInkHitZone HitTest(double xDip, double yDip)
        {
            var hit = VisualTreeHelper.HitTest(this, new Point(xDip, yDip));
            if (hit == null)
                return WetInkHitZone.Outside;

            // 结构式判定：命中落在 inkCanvas 之内才是画布表面。
            var current = hit.VisualHit as DependencyObject;
            while (current != null)
            {
                if (ReferenceEquals(current, inkCanvas))
                    return WetInkHitZone.CanvasSurface;
                current = VisualTreeHelper.GetParent(current);
            }

            return WetInkHitZone.UiChrome;
        }

        WetInkRouteContext IWetInkRouteHost.BuildRouteContext() => BuildRouteContext();

        private WetInkRouteContext BuildRouteContext()
        {
            var multiTouchWriting = IsWetInkPipelineAvailable &&
                                    (_wetInkLogicalTool == InkCanvasEditingMode.Ink ||
                                     _wetInkLogicalTool == InkCanvasEditingMode.EraseByPoint);
            return new WetInkRouteContext(
                multiTouchWriting: multiTouchWriting,
                twoFingerGestureAllowed: multiTouchWriting,
                activeTouchCount: _wetInkSessions?.ActiveTouchCount ?? 0);
        }

        WetInkRoute IWetInkRouteHost.ResolveLogicalTool() => ResolveLogicalTool();

        private WetInkRoute ResolveLogicalTool()
        {
            if (drawingShapeMode != 0)
                return WetInkRoute.Shape;
            if (IsBoardRoamingMode)
                return WetInkRoute.BoardRoam;

            switch (_wetInkLogicalTool)
            {
                case InkCanvasEditingMode.EraseByPoint:
                    return WetInkRoute.PointErase;
                case InkCanvasEditingMode.EraseByStroke:
                    return WetInkRoute.StrokeErase;
                case InkCanvasEditingMode.Select:
                    return WetInkRoute.Select;
                case InkCanvasEditingMode.Ink:
                    return WetInkRoute.Ink;
                default:
                    return WetInkRoute.DeferToWpf;
            }
        }

        bool IWetInkRouteHost.IsPageFrozen => IsCurrentPageFrozen;

        bool IWetInkRouteHost.IsQuadIrTouch => Settings?.Advanced?.IsQuadIR ?? false;

        bool IWetInkRouteHost.IsSpecialScreen => Settings?.Advanced?.IsSpecialScreen ?? false;

        double IWetInkRouteHost.TouchMultiplier => Settings?.Advanced?.TouchMultiplier ?? 1;

        bool IWetInkRouteHost.IsPalmEraserEnabled => Settings?.Canvas?.EnablePalmEraser ?? false;

        double IWetInkRouteHost.NibModeBoundsWidth =>
            Settings?.Advanced?.NibModeBoundsWidthThresholdValue ?? 10;

        double IWetInkRouteHost.FingerModeBoundsWidth =>
            Settings?.Advanced?.FingerModeBoundsWidthThresholdValue ?? 30;

        double IWetInkRouteHost.NibModeThresholdFactor => 2.5;

        double IWetInkRouteHost.FingerModeThresholdFactor => 2.5;

        double IWetInkRouteHost.PalmEraserSensitivityMultiplier =>
            (Settings?.Canvas?.PalmEraserSensitivity ?? 0) switch
            {
                0 => 3.0,
                1 => 2.5,
                2 => 2.0,
                _ => 3.0
            };

        double IWetInkRouteHost.PalmEraserSizeFactor =>
            (Settings?.Advanced?.NibModeBoundsWidthEraserSize ?? 0) > 0
                ? Settings.Advanced.NibModeBoundsWidthEraserSize
                : 0.8;

        // ==================================================================
        // style / coordinate helpers
        // ==================================================================

        private WetInkStyleSnapshot BuildWetInkStyle()
        {
            var da = drawingAttributes;
            if (da == null)
                return new WetInkStyleSnapshot(
                    0xFF000000, 3, 3, WetInkTipShape.Ellipse,
                    WetInkRenderMode.Standard, false, false);

            var colorArgb = (uint)(
                (da.Color.A << 24) | (da.Color.R << 16) |
                (da.Color.G << 8) | da.Color.B);

            return new WetInkStyleSnapshot(
                colorArgb,
                da.Width,
                da.Height,
                da.StylusTip == StylusTip.Rectangle ? WetInkTipShape.Rectangle : WetInkTipShape.Ellipse,
                penType == 2 ? WetInkRenderMode.Laser : WetInkRenderMode.Standard,
                da.IgnorePressure,
                da.IsHighlighter);
        }

        private DrawingAttributes BuildWetInkDrawingAttributes(in WetInkStyleSnapshot style)
        {
            var da = new DrawingAttributes
            {
                Color = Color.FromArgb(
                    (byte)((style.ColorArgb >> 24) & 0xFF),
                    (byte)((style.ColorArgb >> 16) & 0xFF),
                    (byte)((style.ColorArgb >> 8) & 0xFF),
                    (byte)(style.ColorArgb & 0xFF)),
                Width = style.WidthDip,
                Height = style.HeightDip,
                StylusTip = style.TipShape == WetInkTipShape.Rectangle
                    ? StylusTip.Rectangle
                    : StylusTip.Ellipse,
                IgnorePressure = style.IgnorePressure,
                IsHighlighter = style.IsHighlighter
            };
            da.FitToCurve = false;
            return da;
        }

        private WetInkTargetSnapshot BuildWetInkTargetSnapshot()
        {
            var (dpiX, dpiY) = GetWetInkDpiScales();
            if (_wetInkHwndSource != null && _wetInkHwndSource.Handle != IntPtr.Zero)
            {
                GetClientRect(_wetInkHwndSource.Handle, out var rect);
                var width = rect.right - rect.left;
                var height = rect.bottom - rect.top;
                if (width <= 0 || height <= 0)
                    return default;

                var client = new WetInkPoint { x = 0, y = 0 };
                ClientToScreen(_wetInkHwndSource.Handle, ref client);

                return new WetInkTargetSnapshot(
                    client.x, client.y, width, height, dpiX, dpiY);
            }

            // 回退：用窗口逻辑尺寸 × DPI。
            return new WetInkTargetSnapshot(
                0, 0,
                (int)(ActualWidth * dpiX),
                (int)(ActualHeight * dpiY),
                dpiX, dpiY);
        }

        private (double X, double Y) GetWetInkDpiScales()
        {
            try
            {
                var source = PresentationSource.FromVisual(this);
                var m = source?.CompositionTarget?.TransformToDevice;
                if (m.HasValue)
                    return (m.Value.M11, m.Value.M22);
            }
            catch { /* fall through */ }
            return (1.0, 1.0);
        }

        private Point GetWetInkCanvasOrigin()
        {
            try
            {
                return inkCanvas.TransformToAncestor(this).Transform(new Point(0, 0));
            }
            catch
            {
                return new Point(0, 0);
            }
        }

        // ==================================================================
        // interop (client rect)
        // ==================================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct WetInkRect
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WetInkPoint
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hwnd, out WetInkRect rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hwnd, ref WetInkPoint point);
    }
}
