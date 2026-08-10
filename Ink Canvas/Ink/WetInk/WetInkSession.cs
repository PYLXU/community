using System;
using System.Collections.Generic;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>会话状态机。</summary>
    internal enum WetInkSessionState
    {
        /// <summary>正在书写。</summary>
        Active,

        /// <summary>已抬笔，等待构建干墨。</summary>
        Ending,

        /// <summary>干墨已加入 InkCanvas，等待 WPF 合成完成后才撤湿墨。</summary>
        DryCommittedAwaitingWpfFrame,

        /// <summary>正在撤湿墨视觉。</summary>
        RetiringWetVisual,

        /// <summary>完成。</summary>
        Completed,

        /// <summary>取消（手势接管 / 捕获丢失 / 冻结）。</summary>
        Canceled
    }

    /// <summary>该会话是否走擦除路径（不下发几何，只通知 sink 按点擦除干墨）。</summary>
    internal static class WetInkRouteExtensions
    {
        public static bool IsErase(this WetInkRoute route) =>
            route == WetInkRoute.PointErase || route == WetInkRoute.StrokeErase;
    }

    /// <summary>
    /// 单笔湿墨会话。累积真实采样 + 预测尾，维护状态机。
    /// 非线程安全：由 WetInkController 在锁内调用。
    /// </summary>
    internal sealed class WetInkSession
    {
        /// <summary>红外触摸「手指微抬」容错窗口：这段时间内的 Down 视为同一笔续写。</summary>
        public const long TouchLiftToleranceMicroseconds = 60_000; // 60ms

        public WetInkSession(
            long sessionId,
            uint pointerId,
            WetInkInputKind inputKind,
            WetInkStyleSnapshot style,
            WetInkRoute route,
            double palmEraserWidthDip)
        {
            SessionId = sessionId;
            PointerId = pointerId;
            InputKind = inputKind;
            Style = style;
            Route = route;
            PalmEraserWidthDip = palmEraserWidthDip;
            State = WetInkSessionState.Active;
        }

        private readonly List<WetInkSample> _realSamples = new List<WetInkSample>(256);
        private WetInkSample[] _predictedSamples = Array.Empty<WetInkSample>();

        public long SessionId { get; }
        public uint PointerId { get; }
        public WetInkInputKind InputKind { get; }
        public WetInkStyleSnapshot Style { get; }
        public WetInkRoute Route { get; }
        public double PalmEraserWidthDip { get; }

        public WetInkSessionState State { get; private set; }

        /// <summary>渲染快照版本号，每次几何变化 +1，用于渲染线程合并。</summary>
        public long SnapshotVersion { get; private set; }

        /// <summary>最后一个真实采样的时间戳，用于「手指微抬」容错。</summary>
        public long LastRealTimestampMicroseconds { get; private set; }

        public int RealSampleCount => _realSamples.Count;

        public IReadOnlyList<WetInkSample> RealSamples => _realSamples;
        public WetInkSample[] PredictedSamples => _predictedSamples;

        /// <summary>追加真实采样（已由处理器平滑/去重）。</summary>
        public void AppendReal(IReadOnlyList<WetInkSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return;

            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                _realSamples.Add(s);
                if (s.TimestampMicroseconds > LastRealTimestampMicroseconds)
                    LastRealTimestampMicroseconds = s.TimestampMicroseconds;
            }

            SnapshotVersion++;
        }

        /// <summary>
        /// 原子替换预测尾。必须与 AppendReal 在同一次调用内完成，
        /// 否则会出现 1 帧「预测已撤、真实未到」的空档（旧系统实证）。
        /// </summary>
        public void ReplacePrediction(WetInkSample[] predicted)
        {
            _predictedSamples = predicted ?? Array.Empty<WetInkSample>();
            SnapshotVersion++;
        }

        /// <summary>抬笔：丢弃预测尾，转 Ending。</summary>
        public void BeginEnding()
        {
            if (State != WetInkSessionState.Active)
                return;

            _predictedSamples = Array.Empty<WetInkSample>();
            State = WetInkSessionState.Ending;
            SnapshotVersion++;
        }

        /// <summary>干墨已提交，等待 WPF 合成帧。</summary>
        public void MarkDryCommitted()
        {
            if (State == WetInkSessionState.Ending)
                State = WetInkSessionState.DryCommittedAwaitingWpfFrame;
        }

        /// <summary>WPF 合成帧已过，可以开始撤湿墨。</summary>
        public void MarkWpfFrameRendered()
        {
            if (State == WetInkSessionState.DryCommittedAwaitingWpfFrame)
                State = WetInkSessionState.RetiringWetVisual;
        }

        /// <summary>湿墨视觉已撤，会话结束。</summary>
        public void MarkRetired()
        {
            if (State == WetInkSessionState.RetiringWetVisual)
                State = WetInkSessionState.Completed;
        }

        public void Cancel()
        {
            _predictedSamples = Array.Empty<WetInkSample>();
            State = WetInkSessionState.Canceled;
            SnapshotVersion++;
        }

        /// <summary>把当前会话拉直成一条直线（停顿拉直）。</summary>
        public bool StraightenToLine()
        {
            if (State != WetInkSessionState.Active || _realSamples.Count < 2)
                return false;

            var first = _realSamples[0];
            var last = _realSamples[_realSamples.Count - 1];
            _realSamples.Clear();
            _realSamples.Add(first);
            _realSamples.Add(last);
            _predictedSamples = Array.Empty<WetInkSample>();
            SnapshotVersion++;
            return true;
        }

        /// <summary>构建干墨提交载荷（只含真实点）。</summary>
        public WetInkCommitPayload BuildCommitPayload()
        {
            return new WetInkCommitPayload(
                SessionId,
                InputKind,
                Style,
                _realSamples.ToArray());
        }
    }

    /// <summary>
    /// 会话管理器。双字典：pointerId → 活动会话，sessionId → 全部在途会话。
    /// 抬笔后 pointerId 立即解绑，使同一根手指可以马上开始下一笔，
    /// 而 retirement 回调仍能按 sessionId 找到旧会话（旧系统实证的竞态修法）。
    /// </summary>
    internal sealed class WetInkSessionManager
    {
        private readonly Dictionary<uint, WetInkSession> _byPointer =
            new Dictionary<uint, WetInkSession>();
        private readonly Dictionary<long, WetInkSession> _bySession =
            new Dictionary<long, WetInkSession>();
        private long _nextSessionId = 1;

        public int ActiveCount => _byPointer.Count;

        public int ActiveTouchCount
        {
            get
            {
                var n = 0;
                foreach (var kv in _byPointer)
                {
                    if (kv.Value.InputKind == WetInkInputKind.Touch)
                        n++;
                }
                return n;
            }
        }

        public WetInkSession Begin(
            uint pointerId,
            WetInkInputKind inputKind,
            WetInkStyleSnapshot style,
            WetInkRoute route,
            double palmEraserWidthDip)
        {
            // 同一 pointerId 已有活动会话：先取消，避免会话泄漏。
            if (_byPointer.TryGetValue(pointerId, out var stale))
            {
                stale.Cancel();
                _byPointer.Remove(pointerId);
            }

            var session = new WetInkSession(
                _nextSessionId++, pointerId, inputKind, style, route, palmEraserWidthDip);
            _byPointer[pointerId] = session;
            _bySession[session.SessionId] = session;
            return session;
        }

        public bool TryGetByPointer(uint pointerId, out WetInkSession session)
        {
            return _byPointer.TryGetValue(pointerId, out session);
        }

        public bool TryGetBySession(long sessionId, out WetInkSession session)
        {
            return _bySession.TryGetValue(sessionId, out session);
        }

        /// <summary>抬笔：从 pointer 字典解绑，但保留在 session 字典等待 retirement。</summary>
        public void DetachPointer(uint pointerId)
        {
            _byPointer.Remove(pointerId);
        }

        /// <summary>会话彻底结束，从 session 字典移除。</summary>
        public void Remove(long sessionId)
        {
            _bySession.Remove(sessionId);
        }

        public List<WetInkSession> SnapshotActiveSessions()
        {
            return new List<WetInkSession>(_byPointer.Values);
        }

        public List<WetInkSession> SnapshotAllSessions()
        {
            return new List<WetInkSession>(_bySession.Values);
        }

        public void Clear()
        {
            _byPointer.Clear();
            _bySession.Clear();
        }
    }
}
