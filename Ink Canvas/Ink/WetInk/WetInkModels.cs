using System;
using Windows.UI.Input.Inking;
using Windows = global::Windows;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 逻辑墨迹工具。引擎按逻辑工具决定是否接管输入，不依赖 InkCanvas 物理 EditingMode。
    /// </summary>
    internal enum WetInkLogicalTool
    {
        Cursor,
        Pen,
        PointEraser,
        StrokeEraser,
        Select,
        Shape,
        BoardRoam
    }

    /// <summary>指针接触类型（手掌擦除判定结果）。</summary>
    internal enum WetInkContactKind
    {
        None,
        Pen,
        Mouse,
        Finger,
        Palm
    }

    /// <summary>
    /// 墨迹样式快照。与 ICC 笔设置同步，同时映射到 WinRT InkDrawingAttributes 与
    /// WPF System.Windows.Ink.Stroke 的 DrawingAttributes（湿墨渲染与干墨存储必须一致）。
    /// </summary>
    internal readonly struct WetInkStyleSnapshot
    {
        public WetInkStyleSnapshot(
            global::Windows.UI.Color color,
            double width,
            double height,
            bool fitToCurve,
            bool ignorePressure,
            bool drawAsHighlighter,
            PenTipShape penTip)
        {
            Color = color;
            Width = width;
            Height = height;
            FitToCurve = fitToCurve;
            IgnorePressure = ignorePressure;
            DrawAsHighlighter = drawAsHighlighter;
            PenTip = penTip;
        }

        public global::Windows.UI.Color Color { get; }
        public double Width { get; }
        public double Height { get; }
        public bool FitToCurve { get; }
        public bool IgnorePressure { get; }
        public bool DrawAsHighlighter { get; }
        public PenTipShape PenTip { get; }
    }

    /// <summary>
    /// 单个指针的接触信息。来自覆盖窗口的 WM_POINTER 消息（只读、仅用于手掌分类），
    /// 不参与墨迹采集——采集与渲染完全交给 InkPresenter。
    /// </summary>
    internal readonly struct WetInkContactInfo
    {
        public WetInkContactInfo(
            uint pointerId,
            WetInkContactKind kind,
            double xDip,
            double yDip,
            double contactWidthDip,
            double contactHeightDip)
        {
            PointerId = pointerId;
            Kind = kind;
            XDip = xDip;
            YDip = yDip;
            ContactWidthDip = contactWidthDip;
            ContactHeightDip = contactHeightDip;
        }

        public uint PointerId { get; }
        public WetInkContactKind Kind { get; }
        public double XDip { get; }
        public double YDip { get; }
        public double ContactWidthDip { get; }
        public double ContactHeightDip { get; }
    }

    /// <summary>
    /// 手掌判定策略。构建自 MainWindow.Settings（现有 Nib/FingerMode 阈值），
    /// 与旧 BuildPalmRoutePolicy 公式一致，设置不变。
    /// </summary>
    internal readonly struct WetInkPalmPolicy
    {
        public WetInkPalmPolicy(
            bool enabled,
            bool isQuadIr,
            bool isSpecialScreen,
            double boundsWidthDip,
            double thresholdFactor,
            double sensitivityMultiplier,
            double eraserSizeFactor,
            double touchMultiplier)
        {
            Enabled = enabled;
            IsQuadIr = isQuadIr;
            IsSpecialScreen = isSpecialScreen;
            BoundsWidthDip = boundsWidthDip;
            ThresholdFactor = thresholdFactor;
            SensitivityMultiplier = sensitivityMultiplier;
            EraserSizeFactor = eraserSizeFactor;
            TouchMultiplier = touchMultiplier;
        }

        public bool Enabled { get; }
        public bool IsQuadIr { get; }
        public bool IsSpecialScreen { get; }
        public double BoundsWidthDip { get; }
        public double ThresholdFactor { get; }
        public double SensitivityMultiplier { get; }
        public double EraserSizeFactor { get; }
        public double TouchMultiplier { get; }
    }

    /// <summary>命中区域（结构式判定，非白名单）。</summary>
    internal enum WetInkHitZone
    {
        Outside,
        CanvasSurface,
        UiChrome
    }

    /// <summary>干墨提交候选：一条已完成的 InkPresenter 笔画。</summary>
    internal readonly struct WetInkDryCandidate
    {
        public WetInkDryCandidate(global::Windows.UI.Input.Inking.InkStroke inkStroke, WetInkStyleSnapshot style)
        {
            InkStroke = inkStroke;
            Style = style;
        }

        public global::Windows.UI.Input.Inking.InkStroke InkStroke { get; }
        public WetInkStyleSnapshot Style { get; }
    }
}
