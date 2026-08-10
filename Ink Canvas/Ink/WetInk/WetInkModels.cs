using System;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>输入设备种类。</summary>
    internal enum WetInkInputKind
    {
        Unknown,
        Mouse,
        Pen,
        Touch
    }

    /// <summary>笔尖形状。</summary>
    internal enum WetInkTipShape
    {
        Ellipse,
        Rectangle
    }

    /// <summary>湿墨渲染模式。</summary>
    internal enum WetInkRenderMode
    {
        /// <summary>普通笔 / 荧光笔：不透明或半透明实色带状。</summary>
        Standard,

        /// <summary>激光笔：加法混合辉光。</summary>
        Laser
    }

    /// <summary>单个采样点标志位。</summary>
    [Flags]
    internal enum WetInkSampleFlags
    {
        None = 0,

        /// <summary>该点处于接触状态（按下 / 触摸中）。</summary>
        InContact = 1 << 0,

        /// <summary>该点由预测器外插产生，不参与干墨提交。</summary>
        Predicted = 1 << 1,

        /// <summary>笔杆按钮（副按钮）按下。</summary>
        BarrelButton = 1 << 2,

        /// <summary>指针被系统取消（如被手势接管）。</summary>
        Canceled = 1 << 3
    }

    /// <summary>
    /// 一个原始输入采样。坐标为窗口客户区 DIP；接触尺寸为物理像素（由分类器转 DIP）。
    /// 结构体按值传递，避免每帧堆分配。
    /// </summary>
    internal readonly struct WetInkSample
    {
        public WetInkSample(
            uint pointerId,
            WetInkInputKind inputKind,
            double x,
            double y,
            float pressure,
            bool hasPressure,
            long timestampMicroseconds,
            uint frameId,
            WetInkSampleFlags flags,
            double contactWidthPixels,
            double contactHeightPixels)
        {
            PointerId = pointerId;
            InputKind = inputKind;
            X = x;
            Y = y;
            Pressure = pressure;
            HasPressure = hasPressure;
            TimestampMicroseconds = timestampMicroseconds;
            FrameId = frameId;
            Flags = flags;
            ContactWidthPixels = contactWidthPixels;
            ContactHeightPixels = contactHeightPixels;
        }

        public uint PointerId { get; }
        public WetInkInputKind InputKind { get; }
        public double X { get; }
        public double Y { get; }

        /// <summary>归一化压感 0..1。没有真压感时由分类器/处理器补齐。</summary>
        public float Pressure { get; }
        public bool HasPressure { get; }

        /// <summary>统一到微秒的时间戳，用于速度/预测计算。</summary>
        public long TimestampMicroseconds { get; }

        /// <summary>同一输入帧内的序号，用于历史去重排序。</summary>
        public uint FrameId { get; }

        public WetInkSampleFlags Flags { get; }

        /// <summary>接触区宽（物理像素），红外触摸框据此判手指/手掌。</summary>
        public double ContactWidthPixels { get; }

        /// <summary>接触区高（物理像素）。</summary>
        public double ContactHeightPixels { get; }

        public bool IsInContact => (Flags & WetInkSampleFlags.InContact) != 0;
        public bool IsPredicted => (Flags & WetInkSampleFlags.Predicted) != 0;

        public WetInkSample WithPosition(double x, double y)
        {
            return new WetInkSample(
                PointerId, InputKind, x, y, Pressure, HasPressure,
                TimestampMicroseconds, FrameId, Flags,
                ContactWidthPixels, ContactHeightPixels);
        }

        public WetInkSample WithPressure(float pressure)
        {
            return new WetInkSample(
                PointerId, InputKind, X, Y, pressure, true,
                TimestampMicroseconds, FrameId, Flags,
                ContactWidthPixels, ContactHeightPixels);
        }

        public WetInkSample WithFlags(WetInkSampleFlags flags)
        {
            return new WetInkSample(
                PointerId, InputKind, X, Y, Pressure, HasPressure,
                TimestampMicroseconds, FrameId, flags,
                ContactWidthPixels, ContactHeightPixels);
        }
    }

    /// <summary>
    /// 落笔瞬间冻结的笔样式。一笔中途改设置不影响已经在写的笔画。
    /// </summary>
    internal readonly struct WetInkStyleSnapshot
    {
        public WetInkStyleSnapshot(
            uint colorArgb,
            double widthDip,
            double heightDip,
            WetInkTipShape tipShape,
            WetInkRenderMode renderMode,
            bool ignorePressure,
            bool isHighlighter)
        {
            ColorArgb = colorArgb;
            WidthDip = widthDip;
            HeightDip = heightDip;
            TipShape = tipShape;
            RenderMode = renderMode;
            IgnorePressure = ignorePressure;
            IsHighlighter = isHighlighter;
        }

        public uint ColorArgb { get; }
        public double WidthDip { get; }
        public double HeightDip { get; }
        public WetInkTipShape TipShape { get; }
        public WetInkRenderMode RenderMode { get; }
        public bool IgnorePressure { get; }
        public bool IsHighlighter { get; }
    }

    /// <summary>
    /// 抬笔时交给 UI 线程构建 WPF Stroke 的载荷。
    /// </summary>
    internal sealed class WetInkCommitPayload
    {
        public WetInkCommitPayload(
            long sessionId,
            WetInkInputKind inputKind,
            WetInkStyleSnapshot style,
            WetInkSample[] samples)
        {
            SessionId = sessionId;
            InputKind = inputKind;
            Style = style;
            Samples = samples ?? Array.Empty<WetInkSample>();
        }

        public long SessionId { get; }
        public WetInkInputKind InputKind { get; }
        public WetInkStyleSnapshot Style { get; }

        /// <summary>只含真实点（预测点已剔除），时间升序。</summary>
        public WetInkSample[] Samples { get; }
    }
}
