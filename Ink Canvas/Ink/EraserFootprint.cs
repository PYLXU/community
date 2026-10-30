using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;

namespace Ink_Canvas.Ink
{
    /// <summary>
    /// 橡皮擦「擦头足迹」的**共用**视觉：一张擦头预览图，跟随指针居中显示。
    /// <para>
    /// 主画布（<c>MainWindow.xaml</c> 的 <c>EraserOverlayCanvas</c> + <c>EraserFeedback</c>）
    /// 与卫星独立画布（内置图片查看器的画布）都用这一份实现，并且引用<b>同一个资源文件</b>
    /// <c>MainWindow_cs/MW_Eraser.xaml</c>（<c>EllipseEraserImageSource</c> /
    /// <c>RectangleEraserImageSource</c>）。因此擦头样式、宽高换算在哪儿都不可能不一致。
    /// </para>
    /// <para>
    /// 尺寸有两个入口：
    /// <list type="bullet">
    /// <item><see cref="ApplyShape(bool, double)"/> —— 主画布用（入参是设置里的擦头形状与宽度）；</item>
    /// <item><see cref="ApplyShape(StylusShape)"/> —— 卫星画布用，尺寸直接取自
    /// 真正参与擦除的 <see cref="StylusShape"/>，预览框与真实擦除范围天然对齐，
    /// 不会出现「预览比实际擦得大/小」。</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class EraserFootprint
    {
        /// <summary>擦头预览图的绘制基准尺寸（矩形 38×56）——与资源里的 ClipGeometry 一致，仅用于说明比例。</summary>
        public const double SourceWidth = 38;

        /// <summary>擦头预览图的绘制基准高度。</summary>
        public const double SourceHeight = 56;

        /// <summary>擦头预览的最小显示边长，避免极小擦头时看不见。</summary>
        public const double MinDisplaySize = 10;

        /// <summary>矩形擦头资源键（与 <c>MW_Eraser.xaml</c> 中的定义一致）。</summary>
        public const string RectangleImageKey = "RectangleEraserImageSource";

        /// <summary>圆形擦头资源键（与 <c>MW_Eraser.xaml</c> 中的定义一致）。</summary>
        public const string EllipseImageKey = "EllipseEraserImageSource";

        private readonly Image _image;
        private readonly TranslateTransform _translate;

        /// <summary>
        /// 创建擦头视觉并挂到 <paramref name="host"/>（通常是覆盖层 Canvas）。
        /// </summary>
        /// <param name="host">承载擦头预览的容器，一般是与墨迹同坐标系的覆盖层。</param>
        /// <param name="resourceLookupRoot">
        /// 用于解析擦头图片资源的元素（走 <see cref="FrameworkElement.TryFindResource"/>，
        /// 会向上查窗口与应用程序资源）。为 null 时用 <paramref name="host"/>。
        /// </param>
        public EraserFootprint(Panel host, FrameworkElement resourceLookupRoot = null)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));

            ResourceLookupRoot = resourceLookupRoot ?? host;

            _translate = new TranslateTransform();
            _image = new Image
            {
                Width = 0,
                Height = 0,
                Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0, 0),
                RenderTransform = _translate,
                IsHitTestVisible = false,
                SnapsToDevicePixels = false,
            };

            host.Children.Add(_image);
        }

        /// <summary>解析擦头图片资源用的元素。</summary>
        public FrameworkElement ResourceLookupRoot { get; }

        /// <summary>擦头预览当前是否可见。</summary>
        public bool IsVisible => _image.Visibility == Visibility.Visible;

        /// <summary>擦头预览当前宽度（DIP）。</summary>
        public double FootprintWidth => _image.Width;

        /// <summary>擦头预览当前高度（DIP）。</summary>
        public double FootprintHeight => _image.Height;

        /// <summary>
        /// 资源文件是否可达。为 false 说明窗口没有合并 <c>MW_Eraser.xaml</c>，
        /// 擦头预览会退化成「有尺寸但没图案」的空白 —— 启动时检查一次并写日志，避免静默失效。
        /// </summary>
        public bool IsResourceAvailable()
            => ResourceLookupRoot.TryFindResource(EllipseImageKey) is DrawingImage
               && ResourceLookupRoot.TryFindResource(RectangleImageKey) is DrawingImage;

        /// <summary>按「形状 + 擦头宽度」设定预览图（主画布路径，入参即设置里的擦头参数）。</summary>
        /// <param name="circle">是否为圆形擦头。</param>
        /// <param name="widthDip">擦头宽度（DIP）。</param>
        public void ApplyShape(bool circle, double widthDip)
        {
            double width = Math.Max(widthDip, MinDisplaySize);
            // 矩形擦头与源图同为 38:56，高度按同一比例放大；圆形则等高。
            double height = circle ? width : width * (SourceHeight / SourceWidth);
            ApplyCore(circle ? EllipseImageKey : RectangleImageKey, width, height);
        }

        /// <summary>
        /// 按「形状 + 宽度 + 高度」设定预览图（主画布路径）。
        /// <para>
        /// 与两参重载的区别：高度由调用方给出，用于「掌擦」等宽高比不遵循 38:56 的场景，
        /// 使预览尺寸与真实参与擦除的 <see cref="StylusShape"/> 完全一致。
        /// </para>
        /// </summary>
        /// <param name="circle">是否为圆形擦头。</param>
        /// <param name="widthDip">擦头宽度（DIP）。</param>
        /// <param name="heightDip">擦头高度（DIP）。</param>
        public void ApplyShape(bool circle, double widthDip, double heightDip)
        {
            ApplyCore(
                circle ? EllipseImageKey : RectangleImageKey,
                Math.Max(widthDip, MinDisplaySize),
                Math.Max(heightDip, MinDisplaySize));
        }

        /// <summary>
        /// 按真正用于擦除的 <paramref name="shape"/> 设定预览图（卫星画布路径）。
        /// </summary>
        /// <param name="shape">擦除实际使用的 <see cref="StylusShape"/>（来自主画布同步）。</param>
        public void ApplyShape(StylusShape shape)
        {
            if (shape == null) return;

            bool circle = shape is EllipseStylusShape;
            ApplyCore(
                circle ? EllipseImageKey : RectangleImageKey,
                Math.Max(shape.Width, MinDisplaySize),
                Math.Max(shape.Height, MinDisplaySize));
        }

        private void ApplyCore(string imageKey, double width, double height)
        {
            if (ResourceLookupRoot.TryFindResource(imageKey) is DrawingImage source)
                _image.Source = source;

            _image.Width = width;
            _image.Height = height;

            // 尺寸变了要重新测量：MoveTo 依赖 ActualWidth/ActualHeight 才能正确居中
            // （主画布原实现也是先 Measure 再定位）。
            _image.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        /// <summary>把擦头预览居中到视口坐标 <paramref name="center"/>。</summary>
        public void MoveTo(Point center)
        {
            // 显式尺寸优先：折叠或刚改尺寸时 ActualWidth/ActualHeight 可能仍是 0 或旧值，
            // 直接用会让擦头图标偏离触点（上游同类修复，此处统一到共用实现）。
            double width = _image.Width > 0 ? _image.Width : _image.ActualWidth;
            double height = _image.Height > 0 ? _image.Height : _image.ActualHeight;

            _translate.X = center.X - width / 2;
            _translate.Y = center.Y - height / 2;
        }

        /// <summary>显示擦头预览。</summary>
        public void Show() => _image.Visibility = Visibility.Visible;

        /// <summary>隐藏擦头预览。</summary>
        public void Hide() => _image.Visibility = Visibility.Collapsed;
    }
}
