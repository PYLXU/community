using System;
using System.Collections.Generic;
using System.Windows;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 命中的 UI 区域分类。结构式判定：命中落在 inkCanvas 之内才是画布表面，
    /// 其它一律视为 UI 镀铬（配合 WindowFromPoint 排除外来顶层窗口）。
    /// </summary>
    internal enum WetInkHitZone
    {
        Outside,
        UiChrome,
        CanvasSurface
    }

    /// <summary>路由决策：某次落笔应该交给谁。</summary>
    internal enum WetInkRoute
    {
        /// <summary>引擎画墨。</summary>
        Ink,

        /// <summary>按点擦除（手掌擦除也走这里）。</summary>
        PointErase,

        /// <summary>按笔划擦除。</summary>
        StrokeErase,

        /// <summary>选择。</summary>
        Select,

        /// <summary>图形绘制（由现有图形模式处理）。</summary>
        Shape,

        /// <summary>漫游（板面移动）。</summary>
        BoardRoam,

        /// <summary>页面冻结，禁止任何修改。</summary>
        BlockedFrozen,

        /// <summary>UI 元素/交互，交还 WPF 处理，引擎不拦截。</summary>
        DeferToWpf
    }

    /// <summary>路由上下文：由 MainWindow 在每笔落笔时提供。</summary>
    internal readonly struct WetInkRouteContext
    {
        public WetInkRouteContext(
            bool multiTouchWriting,
            bool twoFingerGestureAllowed,
            int activeTouchCount)
        {
            MultiTouchWriting = multiTouchWriting;
            TwoFingerGestureAllowed = twoFingerGestureAllowed;
            ActiveTouchCount = activeTouchCount;
        }

        /// <summary>当前是否处于多指书写模式（引擎原生支持多指，永远 true 由逻辑工具决定）。</summary>
        public bool MultiTouchWriting { get; }

        /// <summary>是否允许双指手势接管（缩放/平移）。</summary>
        public bool TwoFingerGestureAllowed { get; }

        /// <summary>当前在途的触摸会话数。</summary>
        public int ActiveTouchCount { get; }
    }

    /// <summary>单次路由决策。</summary>
    internal readonly struct WetInkRouteDecision
    {
        public WetInkRouteDecision(
            WetInkRoute route,
            bool engineOwnsStroke,
            bool deferToWpf,
            double palmEraserWidthDip = 0,
            bool suppressPointEmission = false)
        {
            Route = route;
            EngineOwnsStroke = engineOwnsStroke;
            DeferToWpf = deferToWpf;
            PalmEraserWidthDip = palmEraserWidthDip;
            SuppressPointEmission = suppressPointEmission;
        }

        public WetInkRoute Route { get; }

        /// <summary>引擎是否接管本笔（开始/更新/结束由引擎处理）。</summary>
        public bool EngineOwnsStroke { get; }

        /// <summary>是否交还 WPF（UI 点击等）。</summary>
        public bool DeferToWpf { get; }

        /// <summary>手掌擦除时的擦除宽度（DIP）。</summary>
        public double PalmEraserWidthDip { get; }

        /// <summary>抑制点发射（笔杆按钮按下时）。</summary>
        public bool SuppressPointEmission { get; }
    }

    /// <summary>
    /// 路由与四边红外触摸分类的宿主抽象。由 MainWindow 实现，把
    /// 逻辑工具、冻结状态、命中测试、触摸阈值提供给引擎。
    /// </summary>
    internal interface IWetInkRouteHost
    {
        /// <summary>返回命中区域（窗口客户区 DIP 坐标）。</summary>
        WetInkHitZone HitTest(double xDip, double yDip);

        /// <summary>构建落笔路由上下文。</summary>
        WetInkRouteContext BuildRouteContext();

        /// <summary>解析当前逻辑工具（笔/橡皮/选择/图形/漫游/光标）。</summary>
        WetInkRoute ResolveLogicalTool();

        /// <summary>当前页面是否冻结（禁止修改）。</summary>
        bool IsPageFrozen { get; }

        /// <summary>四边红外触摸框（设置里 IsQuadIR）。</summary>
        bool IsQuadIrTouch { get; }

        /// <summary>特殊屏（大屏红外），触摸倍率。</summary>
        bool IsSpecialScreen { get; }

        /// <summary>特殊屏触摸倍率。</summary>
        double TouchMultiplier { get; }

        /// <summary>手掌擦除功能开关。</summary>
        bool IsPalmEraserEnabled { get; }

        /// <summary>NibMode 触控阈值宽度（DIP）。</summary>
        double NibModeBoundsWidth { get; }

        /// <summary>FingerMode 触控阈值宽度（DIP）。</summary>
        double FingerModeBoundsWidth { get; }

        /// <summary>NibMode 阈值倍率。</summary>
        double NibModeThresholdFactor { get; }

        /// <summary>FingerMode 阈值倍率。</summary>
        double FingerModeThresholdFactor { get; }

        /// <summary>手掌擦除敏感度倍率（3.0 / 2.5 / 2.0）。</summary>
        double PalmEraserSensitivityMultiplier { get; }

        /// <summary>手掌擦除尺寸系数。</summary>
        double PalmEraserSizeFactor { get; }
    }

    /// <summary>
    /// 四边红外触摸分类器：根据接触区尺寸把触摸点分成
    /// 「手指（写墨）」「手掌（擦除）」「双指手势」。
    /// 公式与旧系统 BuildPalmRoutePolicy + TryGetPalmEraserWidth 完全一致，
    /// 只是作为独立组件，供路由与错误恢复复用。
    /// </summary>
    internal sealed class WetInkTouchClassifier
    {
        public const int PalmEraserSensitivityLow = 0;
        public const int PalmEraserSensitivityMedium = 1;
        public const int PalmEraserSensitivityHigh = 2;

        public WetInkTouchClassifier(IWetInkRouteHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        private readonly IWetInkRouteHost _host;

        /// <summary>
        /// 计算接触宽度（DIP）。四边红外触摸框用几何平均 √(w·h)，
        /// 其余用宽度。
        /// </summary>
        public double GetContactWidthDip(WetInkSample sample, double dpiX, double dpiY)
        {
            if (sample.ContactWidthPixels <= 0)
                return 0;

            var widthDip = sample.ContactWidthPixels / dpiX;
            if (!_host.IsQuadIrTouch)
                return widthDip;

            var heightDip = sample.ContactHeightPixels > 0
                ? sample.ContactHeightPixels / dpiY
                : widthDip;
            return Math.Sqrt(Math.Max(0, widthDip * heightDip));
        }

        /// <summary>
        /// 判定触摸是否构成手掌（擦除）而非手指（书写）。
        /// 与旧 TryGetPalmEraserWidth 等价：boundWidth &gt; BoundsWidth 且 &gt; 阈值。
        /// </summary>
        public bool TryGetPalmEraserWidth(
            double contactWidthDip,
            bool useFingerMode,
            out double eraserWidthDip)
        {
            eraserWidthDip = 0;

            if (!_host.IsPalmEraserEnabled)
                return false;
            if (_host.IsSpecialScreen && _host.TouchMultiplier == 0)
                return false;

            var boundsWidth = useFingerMode
                ? _host.FingerModeBoundsWidth
                : _host.NibModeBoundsWidth;
            var thresholdFactor = useFingerMode
                ? _host.FingerModeThresholdFactor
                : _host.NibModeThresholdFactor;

            var threshold = boundsWidth
                            * thresholdFactor
                            * _host.PalmEraserSensitivityMultiplier;
            if (contactWidthDip <= boundsWidth || contactWidthDip <= threshold)
                return false;

            eraserWidthDip = contactWidthDip
                             * _host.PalmEraserSizeFactor
                             * (_host.IsSpecialScreen ? _host.TouchMultiplier : 1);
            return true;
        }

        /// <summary>双指手势阈值：已有 ≥2 个在途触摸会话时，新触摸允许手势接管。</summary>
        public static bool ShouldAllowTwoFingerGesture(WetInkRouteContext context)
        {
            return context.TwoFingerGestureAllowed && context.ActiveTouchCount >= 2;
        }
    }
}
