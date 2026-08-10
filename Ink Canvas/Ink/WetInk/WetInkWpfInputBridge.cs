using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// WPF 触笔/触摸输入桥：把 WPF 的 Stylus/Touch 事件转成引擎采样。
    ///
    /// 为什么用 WPF 事件而不是 WM_POINTER：四边红外触摸框的输入被 WPF 的
    /// WISPTS（实时触笔栈）先行消费，WM_POINTER 常常到不了 HWND 钩子——
    /// 这正是旧系统需要 WpfPointerInputSource 兜底的原因。WPF 触笔事件在
    /// 所有设备（含红外框）上都可靠派发，因此作为引擎的输入源（与希沃
    /// StylusPlugIn 同源）。WM_POINTER 可作未来的笔迹历史增强，但本类为唯一源。
    /// </summary>
    internal sealed class WetInkWpfInputBridge : IDisposable
    {
        /// <summary>StylusDevice.Id 的命名空间位，避免笔/触摸 id 冲突。</summary>
        private const uint StylusPointerNamespace = 0x40000000;
        private const uint TouchPointerNamespace = 0x80000000;

        public WetInkWpfInputBridge(
            UIElement source,
            WetInkPointerHandler handler)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        private readonly UIElement _source;
        private readonly WetInkPointerHandler _handler;
        private readonly Dictionary<uint, uint> _frameIds = new Dictionary<uint, uint>();
        private bool _wired;

        /// <summary>笔 id（含命名空间位）。</summary>
        private static uint PenPointerId(StylusDevice device) =>
            StylusPointerNamespace | ((uint)(device?.Id ?? 0) & 0x3FFFFFFF);

        /// <summary>触摸 id（含命名空间位）。</summary>
        private static uint TouchPointerId(TouchDevice device) =>
            TouchPointerNamespace | ((uint)(device?.Id ?? 0) & 0x3FFFFFFF);

        public void Wire()
        {
            if (_wired)
                return;
            _wired = true;

            // AddHandler with handledEventsToo=true：InkPresenter 会把 Stylus/Touch 事件
            // 标记为 Handled 让内置 InkCanvas 自己画——桥仍需接收以驱动引擎新墨迹路径。
            // InkPresenter 在冒泡阶段会标记 e.Handled=true。handledEventsToo=true 理论应能收到，
            // 但实际在某些 WPF 版本/手写笔栈下 routed event 在 Handled=true 之后不再派发
            // 给同阶段订阅。需要同时订阅 Preview 阶段（隧道）才能稳定拿到。
            _source.AddHandler(Stylus.PreviewStylusDownEvent, new RoutedEventHandler(OnStylusDownPreview), handledEventsToo: true);
            _source.AddHandler(Stylus.PreviewStylusMoveEvent, new RoutedEventHandler(OnStylusMovePreview), handledEventsToo: true);
            _source.AddHandler(Stylus.PreviewStylusUpEvent, new RoutedEventHandler(OnStylusUpPreview), handledEventsToo: true);
            // Touch 的 Preview 事件键注册在 UIElement.Touch*Event 上而非 Touch.*Event。
            _source.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnTouchDownPreview), handledEventsToo: true);
            _source.AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(OnTouchMovePreview), handledEventsToo: true);
            _source.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnTouchUpPreview), handledEventsToo: true);
            _source.AddHandler(Stylus.StylusSystemGestureEvent, new RoutedEventHandler(OnStylusSystemGestureRouted));
            _source.AddHandler(UIElement.TouchDownEvent, new EventHandler<TouchEventArgs>(OnTouchDown), handledEventsToo: true);
            _source.AddHandler(UIElement.TouchMoveEvent, new EventHandler<TouchEventArgs>(OnTouchMove), handledEventsToo: true);
            _source.AddHandler(UIElement.TouchUpEvent, new EventHandler<TouchEventArgs>(OnTouchUp), handledEventsToo: true);
        }

        public void Unwire()
        {
            if (!_wired)
                return;
            _wired = false;

            _source.RemoveHandler(Stylus.PreviewStylusDownEvent, new RoutedEventHandler(OnStylusDownPreview));
            _source.RemoveHandler(Stylus.PreviewStylusMoveEvent, new RoutedEventHandler(OnStylusMovePreview));
            _source.RemoveHandler(Stylus.PreviewStylusUpEvent, new RoutedEventHandler(OnStylusUpPreview));
            _source.RemoveHandler(Stylus.PreviewStylusDownEvent, new RoutedEventHandler(OnStylusDownPreview));
            _source.RemoveHandler(Stylus.PreviewStylusMoveEvent, new RoutedEventHandler(OnStylusMovePreview));
            _source.RemoveHandler(Stylus.PreviewStylusUpEvent, new RoutedEventHandler(OnStylusUpPreview));
            _source.RemoveHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnTouchDownPreview));
            _source.RemoveHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(OnTouchMovePreview));
            _source.RemoveHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(OnTouchUpPreview));
            _source.RemoveHandler(Stylus.StylusSystemGestureEvent, new RoutedEventHandler(OnStylusSystemGestureRouted));
            _source.RemoveHandler(UIElement.TouchDownEvent, new EventHandler<TouchEventArgs>(OnTouchDown));
            _source.RemoveHandler(UIElement.TouchMoveEvent, new EventHandler<TouchEventArgs>(OnTouchMove));
            _source.RemoveHandler(UIElement.TouchUpEvent, new EventHandler<TouchEventArgs>(OnTouchUp));
        }

        private void OnStylusDownPreview(object sender, RoutedEventArgs e)
        {
            Ink_Canvas.Helpers.LogHelper.WriteLogToFile(
                $"[WetInk] PREVIEW StylusDown handled={(e as StylusDownEventArgs)?.Handled}");
            if (e is StylusDownEventArgs sde) OnStylusDown(sender, sde);
        }

        private void OnStylusMovePreview(object sender, RoutedEventArgs e)
        {
            if (e is StylusEventArgs se) OnStylusMove(sender, se);
        }

        private void OnStylusUpPreview(object sender, RoutedEventArgs e)
        {
            if (e is StylusEventArgs se) OnStylusUp(sender, se);
        }

        private void OnTouchDownPreview(object sender, TouchEventArgs e)
        {
            Ink_Canvas.Helpers.LogHelper.WriteLogToFile(
                $"[WetInk] PREVIEW TouchDown handled={e.Handled}");
            OnTouchDown(sender, e);
        }

        private void OnTouchMovePreview(object sender, TouchEventArgs e)
        {
            OnTouchMove(sender, e);
        }

        private void OnTouchUpPreview(object sender, TouchEventArgs e)
        {
            OnTouchUp(sender, e);
        }

        private void OnStylusDownRouted(object sender, RoutedEventArgs e)
        {
            if (!(e is StylusDownEventArgs sde)) return;
            Ink_Canvas.Helpers.LogHelper.WriteLogToFile(
                $"[WetInk] StylusDown id={PenPointerId(sde.StylusDevice)} touch={IsTouchDevice(sde.StylusDevice)}");
            OnStylusDown(sender, sde);
        }

        private void OnStylusMoveRouted(object sender, RoutedEventArgs e)
        {
            if (e is StylusEventArgs se) OnStylusMove(sender, se);
        }

        private void OnStylusUpRouted(object sender, RoutedEventArgs e)
        {
            if (e is StylusEventArgs se) OnStylusUp(sender, se);
        }

        private void OnStylusSystemGestureRouted(object sender, RoutedEventArgs e)
        {
            if (e is StylusSystemGestureEventArgs sge) OnStylusSystemGesture(sender, sge);
        }

        // ------------------------------------------------------------------
        // stylus (pen / touch-through-stylus)
        // ------------------------------------------------------------------

        private void OnStylusDown(object sender, StylusDownEventArgs e)
        {
            // 关键：WISPTS 把触摸也派为 StylusDown(TabletDeviceType.Touch)，
            // 与 TouchDown 重复。不去重就会一份触摸开两个会话（笔/触摸命名空间不同），
            // 画一条线被画成两笔重叠 → 「无法书写连续线段」。
            if (IsTouchDevice(e.StylusDevice)) return;
#if DEBUG
            System.Diagnostics.Debug.WriteLine(
                $"[WetInk] StylusDown id={PenPointerId(e.StylusDevice)} touch={IsTouchDevice(e.StylusDevice)}");
#endif
            DispatchStylus(PenPointerId(e.StylusDevice), WetInkPointerPhase.Down,
                e.StylusDevice, e.GetStylusPoints(_source), e);
        }

        private void OnStylusMove(object sender, StylusEventArgs e)
        {
            if (IsTouchDevice(e.StylusDevice)) return;
            DispatchStylus(PenPointerId(e.StylusDevice), WetInkPointerPhase.Update,
                e.StylusDevice, e.GetStylusPoints(_source), e);
        }

        private void OnStylusUp(object sender, StylusEventArgs e)
        {
            // 兼容不同 WPF 版本：既有 StylusUpEventArgs 也有基类 StylusEventArgs。
            if (IsTouchDevice(e.StylusDevice)) return;
            DispatchStylus(PenPointerId(e.StylusDevice), WetInkPointerPhase.Up,
                e.StylusDevice, e.GetStylusPoints(_source), e);
        }

        private void OnStylusSystemGesture(object sender, StylusSystemGestureEventArgs e)
        {
            if (e.SystemGesture == SystemGesture.Tap)
            {
                Dispatch(PenPointerId(e.StylusDevice), WetInkPointerPhase.CaptureLost,
                    Array.Empty<WetInkSample>());
            }
        }

        private void DispatchStylus(
            uint pointerId, WetInkPointerPhase phase,
            StylusDevice device, StylusPointCollection points, object sourceEvent)
        {
            var kind = IsTouchDevice(device) ? WetInkInputKind.Touch : WetInkInputKind.Pen;
            var sample = ToStylusSample(pointerId, kind, device, points, phase);
            Dispatch(pointerId, phase, new[] { sample });
        }

        /// <summary>判断触笔设备是否实为触摸（StylusDevice.TabletDevice.Type == Touch）。</summary>
        private static bool IsTouchDevice(StylusDevice device)
        {
            try
            {
                return device?.TabletDevice?.Type == TabletDeviceType.Touch;
            }
            catch
            {
                return false;
            }
        }

        private WetInkSample ToStylusSample(
            uint pointerId, WetInkInputKind kind, StylusDevice device,
            StylusPointCollection points, WetInkPointerPhase phase)
        {
            // 取最新点；无点时用 GetPosition 兜底。
            StylusPoint point;
            if (points != null && points.Count > 0)
                point = points[points.Count - 1];
            else
            {
                var pos = device?.GetPosition(_source) ?? new Point(0, 0);
                point = new StylusPoint(pos.X, pos.Y, 0.5f);
            }

            var pressure = point.PressureFactor;
            var hasPressure = pressure > 0;

            double contactWidth = 0, contactHeight = 0;
            // 接触区尺寸仅触摸路径需要（红外框手掌判别）。
            // 笔走 stylus 的接触区按 0 即可（笔尖宽度由 drawingAttributes 决定）。
            // 触摸的 WidthFactor/HeightFactor 是 WPF 触笔点的扩展属性——触控板有，
            // 红外框也偶有，但 StylusPoint 上没有现成字段；下面在 touch 路径单独取。

            return new WetInkSample(
                pointerId,
                kind,
                point.X,
                point.Y,
                pressure,
                hasPressure,
                WetInkTimestampConverter.NowMicroseconds(),
                NextFrameId(pointerId),
                phase == WetInkPointerPhase.Up ? WetInkSampleFlags.None : WetInkSampleFlags.InContact,
                contactWidth,
                contactHeight);
        }

        // ------------------------------------------------------------------
        // touch
        // ------------------------------------------------------------------

        private void OnTouchDown(object sender, TouchEventArgs e)
        {
            Ink_Canvas.Helpers.LogHelper.WriteLogToFile(
                $"[WetInk] TouchDown id={TouchPointerId(e.TouchDevice)}");
            DispatchTouch(TouchPointerId(e.TouchDevice), WetInkPointerPhase.Down, e.GetTouchPoint(_source));
        }

        private void OnTouchMove(object sender, TouchEventArgs e) =>
            DispatchTouch(TouchPointerId(e.TouchDevice), WetInkPointerPhase.Update, e.GetTouchPoint(_source));

        private void OnTouchUp(object sender, TouchEventArgs e) =>
            DispatchTouch(TouchPointerId(e.TouchDevice), WetInkPointerPhase.Up, e.GetTouchPoint(_source));

        private void DispatchTouch(uint pointerId, WetInkPointerPhase phase, TouchPoint touchPoint)
        {
            var bounds = touchPoint.Bounds;
            var sample = new WetInkSample(
                pointerId,
                WetInkInputKind.Touch,
                touchPoint.Position.X,
                touchPoint.Position.Y,
                0.5f,
                hasPressure: false,
                WetInkTimestampConverter.NowMicroseconds(),
                NextFrameId(pointerId),
                phase == WetInkPointerPhase.Up ? WetInkSampleFlags.None : WetInkSampleFlags.InContact,
                bounds.Width,
                bounds.Height);

            Dispatch(pointerId, phase, new[] { sample });
        }

        private void Dispatch(uint pointerId, WetInkPointerPhase phase, WetInkSample[] samples)
        {
            if (samples.Length == 0)
            {
                // CaptureLost 等无采样消息：仍要通知引擎结束/取消该 pointer。
                var empty = new WetInkPointerBatch(
                    pointerId, WetInkInputKind.Unknown, Array.Empty<WetInkSample>(),
                    false, false, false, false);
                _handler(phase, empty);
                return;
            }

            var kind = samples[0].InputKind;
            var full = new WetInkPointerBatch(
                pointerId, kind, samples, false, false, false, false);
            _handler(phase, full);
        }

        private uint NextFrameId(uint pointerId)
        {
            _frameIds.TryGetValue(pointerId, out var current);
            current++;
            _frameIds[pointerId] = current;
            return current;
        }

        public void Dispose()
        {
            Unwire();
            _frameIds.Clear();
        }
    }
}