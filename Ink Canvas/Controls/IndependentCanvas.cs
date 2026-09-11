using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Media;

namespace Ink_Canvas.Controls
{
    /// <summary>
    /// 独立画布：一块与主画布解耦的墨迹区域。
    /// - 复用 WPF InkCanvas / Stroke 节点结构，不重复造轮子。
    /// - 作为独立控件存在，不受主画布墨迹移动（stroke.Transform）影响，保持在原位。
    /// - 支持边框/填充样式；可显示竖直/水平滚动条，滚动信息由调用处传入。
    /// </summary>
    public sealed class IndependentCanvas : UserControl
    {
        private readonly Grid _rootGrid;
        private readonly Border _contentHost;
        private readonly InkCanvas _inkCanvas;
        private readonly ScrollBar _verticalScrollBar;
        private readonly ScrollBar _horizontalScrollBar;

        private double _scrollX;
        private double _scrollY;

        public IndependentCanvas()
        {
            // 让内容网格填满控件（否则内容按自身尺寸对齐，滚动条/墨迹区域会塌缩）。
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;

            _inkCanvas = new InkCanvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = true
            };

            _contentHost = new Border
            {
                Child = _inkCanvas,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                ClipToBounds = true
            };

            _verticalScrollBar = CreateScrollBar(Orientation.Vertical);
            _horizontalScrollBar = CreateScrollBar(Orientation.Horizontal);

            _rootGrid = new Grid();
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });

            Grid.SetRow(_contentHost, 0);
            Grid.SetColumn(_contentHost, 0);
            Grid.SetRow(_verticalScrollBar, 0);
            Grid.SetColumn(_verticalScrollBar, 1);
            Grid.SetRow(_horizontalScrollBar, 1);
            Grid.SetColumn(_horizontalScrollBar, 0);

            _rootGrid.Children.Add(_contentHost);
            _rootGrid.Children.Add(_verticalScrollBar);
            _rootGrid.Children.Add(_horizontalScrollBar);

            // 默认不显示滚动条，等调用处开启。
            _verticalScrollBar.Visibility = Visibility.Collapsed;
            _horizontalScrollBar.Visibility = Visibility.Collapsed;

            Content = _rootGrid;
        }

        private static ScrollBar CreateScrollBar(Orientation orientation)
        {
            return new ScrollBar
            {
                Orientation = orientation,
                Minimum = 0,
                Maximum = 0,
                Value = 0,
                ViewportSize = 0,
                IsTabStop = false
            };
        }

        /// <summary>当前画布内的墨迹集合（复用 Stroke 节点结构）。</summary>
        public StrokeCollection Strokes => _inkCanvas.Strokes;

        /// <summary>内部 InkCanvas（供上层需要读取/清空墨迹时使用）。</summary>
        public InkCanvas InkSurface => _inkCanvas;

        /// <summary>填充（背景）样式。</summary>
        public Brush Fill
        {
            get => _contentHost.Background;
            set => _contentHost.Background = value;
        }

        /// <summary>边框画刷。</summary>
        public Brush BorderStroke
        {
            get => _contentHost.BorderBrush;
            set => _contentHost.BorderBrush = value;
        }

        /// <summary>边框粗细。</summary>
        public Thickness BorderStrokeThickness
        {
            get => _contentHost.BorderThickness;
            set => _contentHost.BorderThickness = value;
        }

        /// <summary>是否显示竖直滚动条（设置项）。</summary>
        public bool ShowVerticalScrollBar
        {
            get => _verticalScrollBar.Visibility == Visibility.Visible;
            set
            {
                _verticalScrollBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                _rootGrid.ColumnDefinitions[1].Width = value ? GridLength.Auto : new GridLength(0);
            }
        }

        /// <summary>是否显示水平滚动条（设置项）。</summary>
        public bool ShowHorizontalScrollBar
        {
            get => _horizontalScrollBar.Visibility == Visibility.Visible;
            set
            {
                _horizontalScrollBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                _rootGrid.RowDefinitions[1].Height = value ? GridLength.Auto : new GridLength(0);
            }
        }

        /// <summary>
        /// 由调用处传入滚动信息，驱动墨迹平移与滚动条滚动。
        /// </summary>
        /// <param name="viewport">可视区大小（即本画布大小，DIP）。</param>
        /// <param name="extent">内容总大小（DIP）。</param>
        /// <param name="offset">当前滚动偏移（向右/向下为正）。</param>
        public void SetScrollInfo(Size viewport, Size extent, Vector offset)
        {
            double deltaX = offset.X - _scrollX;
            double deltaY = offset.Y - _scrollY;
            if (deltaX != 0 || deltaY != 0)
            {
                // 内容向下滚 deltaY，墨迹随之向上平移 deltaY，保持贴合内容。
                var matrix = new Matrix(1, 0, 0, 1, -deltaX, -deltaY);
                foreach (Stroke stroke in _inkCanvas.Strokes)
                    stroke.Transform(matrix, false);
            }
            _scrollX = offset.X;
            _scrollY = offset.Y;

            ApplyToScrollBar(_verticalScrollBar, offset.Y, viewport.Height, extent.Height);
            ApplyToScrollBar(_horizontalScrollBar, offset.X, viewport.Width, extent.Width);
        }

        private static void ApplyToScrollBar(ScrollBar bar, double value, double viewportSize, double extentSize)
        {
            double max = extentSize - viewportSize;
            if (double.IsNaN(max) || double.IsInfinity(max)) max = 0;
            max = Math.Max(0, max);

            bar.ViewportSize = Math.Max(0, viewportSize);
            bar.Maximum = max;
            bar.Value = Math.Min(Math.Max(0, value), max);
        }
    }
}