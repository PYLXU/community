using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>控制器对外回调：把事件送回 UI 线程。</summary>
    internal interface IWetInkControllerSink
    {
        /// <summary>会话开始（已创建会话并投递首帧几何），UI 线程应显示湿墨覆盖层。</summary>
        void OnStrokeStarted(long sessionId);

        /// <summary>会话结束（已构建载荷），UI 线程负责提交到 inkCanvas.Strokes。</summary>
        void OnStrokeCompleted(WetInkCommitPayload payload);

        /// <summary>会话取消（手势接管/冻结），不提交。</summary>
        void OnStrokeCanceled(long sessionId);

        /// <summary>会话已完成湿墨退休（干墨已合成），可清理会话。</summary>
        void OnSessionRetired(long sessionId);

        /// <summary>
        /// 橡皮擦移动：擦除 inkCanvas 干墨。phase 为 Down/Update/Up。
        /// 坐标是窗口客户区 DIP；widthDip 为擦除宽度。
        /// </summary>
        void OnEraserPoint(long sessionId, WetInkPointerPhase phase, double xDip, double yDip, double eraserWidthDip);

        /// <summary>需要路由决策时查询宿主。</summary>
        WetInkRouteDecision QueryDownRoute(
            WetInkPointerBatch batch,
            double contactWidthDip,
            bool useFingerMode);

        /// <summary>路由上下文（活动触摸数等）。</summary>
        WetInkRouteContext QueryRouteContext();
    }

    /// <summary>
    /// 湿墨控制器：WM_POINTER/WPF 输入批 → 分类 → 路由 → 会话 → 渲染命令。
    /// 运行在 UI 线程（输入回调同步调用），渲染通过 WetInkWindowHost 信箱解耦。
    /// </summary>
    internal sealed class WetInkController : IDisposable
    {
        /// <summary>会话「手指微抬」续笔窗口：此窗口内的重新落笔视为同一笔。</summary>
        private const long TouchLiftToleranceMicroseconds = 60_000;

        public WetInkController(
            WetInkSessionManager sessions,
            WetInkCommandMailbox mailbox,
            WetInkTouchClassifier classifier,
            IWetInkControllerSink sink)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        private readonly WetInkSessionManager _sessions;
        private readonly WetInkCommandMailbox _mailbox;
        private readonly WetInkTouchClassifier _classifier;
        private readonly IWetInkControllerSink _sink;

        /// <summary>每个会话的处理管道（平滑/压感）。</summary>
        private readonly Dictionary<long, WetInkSampleProcessor> _processors =
            new Dictionary<long, WetInkSampleProcessor>();

        private double _dpiX = 1.0;
        private double _dpiY = 1.0;

        /// <summary>当前笔样式快照（MainWindow 在工具/样式变化时更新，落笔时冻结）。</summary>
        private WetInkStyleSnapshot _currentStyle;

        private bool _disposed;

        /// <summary>DPI 更新（由 MainWindow 在窗口 DPI 变化时调用）。</summary>
        public void SetDpi(double dpiX, double dpiY)
        {
            _dpiX = dpiX > 0 ? dpiX : 1.0;
            _dpiY = dpiY > 0 ? dpiY : 1.0;
        }

        /// <summary>更新当前笔样式（颜色/宽度/笔尖/渲染模式）。</summary>
        public void SetCurrentStyle(in WetInkStyleSnapshot style)
        {
            _currentStyle = style;
        }

        /// <summary>处理一个输入批。phase 决定 Down/Update/Up/CaptureLost。
        /// 返回 true 表示引擎真正接管了该消息（输入源应标记 handled，阻止 WPF 再处理）；
        /// false 表示引擎不处理（命中 UI 镀铬 / 非写墨工具 / 交还 WPF）。</summary>
        public bool OnPointerInput(WetInkPointerPhase phase, WetInkPointerBatch batch)
        {
            if (_disposed || batch == null || batch.SamplesNewestFirst.Count == 0)
                return false;

            switch (phase)
            {
                case WetInkPointerPhase.Down:
                    return OnDown(batch);
                case WetInkPointerPhase.Update:
                    return OnUpdate(batch);
                case WetInkPointerPhase.Up:
                    return OnUp(batch);
                case WetInkPointerPhase.CaptureLost:
                    return OnCaptureLost(batch);
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------
        // down
        // ------------------------------------------------------------------

        private bool OnDown(WetInkPointerBatch batch)
        {
            var pointerId = batch.PointerId;
            var sample = batch.SamplesNewestFirst[0];

            // 会话续笔：同一设备在容错窗口内重新落笔，继续上一笔。
            if (TryResumeTouchedSession(pointerId, sample))
                return true;

            // 已有活动会话：先取消（陈旧会话保护）。
            if (_sessions.TryGetByPointer(pointerId, out var stale))
            {
                CancelSession(stale);
                _sessions.DetachPointer(pointerId);
            }

            var contactWidthDip = _classifier.GetContactWidthDip(sample, _dpiX, _dpiY);
            var useFingerMode = batch.InputKind == WetInkInputKind.Touch;
            var decision = _sink.QueryDownRoute(batch, contactWidthDip, useFingerMode);

            if (!decision.EngineOwnsStroke)
                return false;

            if (decision.Route == WetInkRoute.Ink)
            {
                var session = _sessions.Begin(
                    pointerId, batch.InputKind, _currentStyle, WetInkRoute.Ink, 0);

                _processors[session.SessionId] = CreateProcessor();
                var processed = _processors[session.SessionId].Process(batch.SamplesNewestFirst);

                session.AppendReal(processed);
                PostGeometry(session);

                try { _sink.OnStrokeStarted(session.SessionId); }
                catch (Exception ex) { Debug.WriteLine($"[WetInk] stroke-started notify: {ex.Message}"); }
                return true;
            }

            if (decision.Route.IsErase())
            {
                var session = _sessions.Begin(
                    pointerId, batch.InputKind, _currentStyle,
                    decision.Route, decision.PalmEraserWidthDip);

                var s = batch.SamplesNewestFirst[0];
                _sink.OnEraserPoint(
                    session.SessionId, WetInkPointerPhase.Down, s.X, s.Y,
                    session.PalmEraserWidthDip);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 「手指微抬」容错：红外触摸框手指轻微抬起会产生瞬时 Up→Down。
        /// 若离上次抬笔在容错窗口内，把它续到上一笔而不是新起一笔。
        /// </summary>
        private bool TryResumeTouchedSession(uint pointerId, in WetInkSample sample)
        {
            if (!_sessions.TryGetByPointer(pointerId, out var session))
                return false;
            if (session.State != WetInkSessionState.Active)
                return false;

            var gap = sample.TimestampMicroseconds - session.LastRealTimestampMicroseconds;
            if (gap < 0 || gap > TouchLiftToleranceMicroseconds)
                return false;

            // 续笔：只追加，不重启。
            var processor = GetOrCreateProcessor(session);
            var processed = processor.Process(new[] { sample });
            session.AppendReal(processed);
            PostGeometry(session);
            return true;
        }

        // ------------------------------------------------------------------
        // update / up / capture lost
        // ------------------------------------------------------------------

        private bool OnUpdate(WetInkPointerBatch batch)
        {
            if (!_sessions.TryGetByPointer(batch.PointerId, out var session))
                return false;
            if (session.State != WetInkSessionState.Active)
                return true; // 引擎仍持有该 pointer（Ending 等），消息不能放回 WPF。

            // 擦除类工具：不下发几何，只按本次样本擦除干墨。
            if (session.Route.IsErase())
            {
                var s = batch.SamplesNewestFirst[0];
                _sink.OnEraserPoint(session.SessionId, WetInkPointerPhase.Update,
                    s.X, s.Y, session.PalmEraserWidthDip);
                return true;
            }

            var processor = GetOrCreateProcessor(session);
            var processed = processor.Process(batch.SamplesNewestFirst);
            if (processed.Count == 0)
                return true; // 引擎持有该 pointer，即使本帧无有效点也标记 handled。

            session.AppendReal(processed);

            // 停顿拉直：书写停顿一定时间后把笔画拉直成线。
            if (session.RealSampleCount >= 4)
                MaybeStraighten(session);

            PostGeometry(session);
            return true;
        }

        private bool OnUp(WetInkPointerBatch batch)
        {
            if (!_sessions.TryGetByPointer(batch.PointerId, out var session))
                return false;

            if (session.Route.IsErase())
            {
                var s = batch.SamplesNewestFirst[0];
                _sink.OnEraserPoint(session.SessionId, WetInkPointerPhase.Up,
                    s.X, s.Y, session.PalmEraserWidthDip);
                _sessions.DetachPointer(batch.PointerId);
                _processors.Remove(session.SessionId);
                _lastUpdateMicroseconds.Remove(session.SessionId);
                _sessions.Remove(session.SessionId);
                return true;
            }

            if (session.State == WetInkSessionState.Active)
                session.BeginEnding();

            // 预测已随 BeginEnding 丢弃；这里把最终几何发给渲染线程。
            PostGeometry(session);

            // 构建干墨载荷 → 提交。pointerId 先解绑，允许同一设备立刻开新笔。
            var payload = session.BuildCommitPayload();
            _sessions.DetachPointer(batch.PointerId);

            // 太短的笔画（轻点/误触）丢弃。
            if (payload.Samples.Length < 2)
            {
                CancelSession(session);
                return true;
            }

            _sink.OnStrokeCompleted(payload);

            // 湿墨要等干墨合成后才 retire（防烘干闪变），由 sink 经 MainWindow
            // 的 WPF 帧 fence 之后回调 OnWpfFrameRendered。
            PostEndStroke(session);
            return true;
        }

        private bool OnCaptureLost(WetInkPointerBatch batch)
        {
            if (!_sessions.TryGetByPointer(batch.PointerId, out var session))
                return false;

            // 捕获丢失（手势接管/冻结）：取消整笔，不提交。
            CancelSession(session);
            _sessions.DetachPointer(batch.PointerId);
            return true;
        }

        // ------------------------------------------------------------------
        // retirement
        // ------------------------------------------------------------------

        /// <summary>WPF 干墨合成帧已过（MainWindow 的帧 fence 调用），撤湿墨。</summary>
        public void OnWpfFrameRendered(long sessionId)
        {
            if (!_sessions.TryGetBySession(sessionId, out var session))
                return;

            session.MarkWpfFrameRendered();

            _mailbox.Post(WetInkCommand.Simple(WetInkCommandKind.RetireStroke, sessionId));
            _sessions.Remove(sessionId);
            _processors.Remove(sessionId);
            _lastUpdateMicroseconds.Remove(sessionId);

            _sink.OnSessionRetired(sessionId);
        }

        private void CancelSession(WetInkSession session)
        {
            if (session.State == WetInkSessionState.Active ||
                session.State == WetInkSessionState.Ending)
            {
                session.Cancel();
            }

            _mailbox.Post(WetInkCommand.Simple(WetInkCommandKind.CancelStroke, session.SessionId));
            _sessions.Remove(session.SessionId);
            _processors.Remove(session.SessionId);
            _lastUpdateMicroseconds.Remove(session.SessionId);

            try { _sink.OnStrokeCanceled(session.SessionId); }
            catch (Exception ex) { Debug.WriteLine($"[WetInk] cancel notify: {ex.Message}"); }
        }

        /// <summary>取消全部在途会话（视频旋转/换页/清理）。</summary>
        public void CancelAllSessions(string reason)
        {
            var active = _sessions.SnapshotAllSessions();
            for (var i = 0; i < active.Count; i++)
            {
                if (active[i].State == WetInkSessionState.Active ||
                    active[i].State == WetInkSessionState.Ending)
                {
                    CancelSession(active[i]);
                }
            }
        }

        // ------------------------------------------------------------------
        // straighten
        // ------------------------------------------------------------------

        private void MaybeStraighten(WetInkSession session)
        {
            if (!_straightenEnabled)
                return;

            var last = session.LastRealTimestampMicroseconds;
            var previous = _lastUpdateMicroseconds.TryGetValue(session.SessionId, out var prev)
                ? prev
                : last;
            _lastUpdateMicroseconds[session.SessionId] = last;

            if (last - previous < _straightenDelayMicroseconds)
                return;

            if (session.StraightenToLine())
                PostGeometry(session);
        }

        private bool _straightenEnabled;
        private long _straightenDelayMicroseconds;
        private readonly Dictionary<long, long> _lastUpdateMicroseconds =
            new Dictionary<long, long>();

        /// <summary>配置停顿拉直（由 MainWindow 从设置注入）。</summary>
        public void ConfigureStraighten(bool enabled, int delayMilliseconds)
        {
            _straightenEnabled = enabled;
            _straightenDelayMicroseconds = (long)(Math.Max(50, delayMilliseconds) * 1000L);
            if (!enabled)
                _lastUpdateMicroseconds.Clear();
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private WetInkSampleProcessor CreateProcessor()
        {
            return new WetInkSampleProcessor(
                enableSmoothing: true,
                simulatePressureFromSpeed: false);
        }

        private WetInkSampleProcessor GetOrCreateProcessor(WetInkSession session)
        {
            if (!_processors.TryGetValue(session.SessionId, out var processor))
            {
                processor = CreateProcessor();
                _processors[session.SessionId] = processor;
            }
            return processor;
        }

        /// <summary>把会话当前几何发给渲染线程。只投 Update，不合并 Begin/End。</summary>
        private void PostGeometry(WetInkSession session)
        {
            var geometry = WetInkGeometryBuilder.Build(
                session.RealSamples, null, session.Style);

            var command = new WetInkCommand(
                session.State == WetInkSessionState.Active
                    ? WetInkCommandKind.UpdateStroke
                    : WetInkCommandKind.EndStroke,
                session.SessionId,
                geometry,
                session.Style,
                session.SnapshotVersion,
                default);

            _mailbox.Post(command);
        }

        private void PostEndStroke(WetInkSession session)
        {
            var geometry = WetInkGeometryBuilder.Build(
                session.RealSamples, null, session.Style);

            _mailbox.Post(new WetInkCommand(
                WetInkCommandKind.EndStroke,
                session.SessionId,
                geometry,
                session.Style,
                session.SnapshotVersion,
                default));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { CancelAllSessions("dispose"); }
            catch (Exception ex) { Debug.WriteLine($"[WetInk] dispose cancel: {ex.Message}"); }

            _processors.Clear();
        }
    }
}