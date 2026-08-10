using System;
using System.Collections.Generic;
using System.Threading;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>渲染命令类型。</summary>
    internal enum WetInkCommandKind
    {
        BeginStroke,
        UpdateStroke,
        EndStroke,
        CancelStroke,
        RetireStroke,
        UpdateTarget,
        Reset,
        Shutdown
    }

    /// <summary>投递给渲染线程的一条命令。</summary>
    internal readonly struct WetInkCommand
    {
        public WetInkCommand(
            WetInkCommandKind kind,
            long sessionId,
            WetInkRibbonGeometry geometry,
            WetInkStyleSnapshot style,
            long snapshotVersion,
            WetInkTargetSnapshot target)
        {
            Kind = kind;
            SessionId = sessionId;
            Geometry = geometry;
            Style = style;
            SnapshotVersion = snapshotVersion;
            Target = target;
        }

        public WetInkCommandKind Kind { get; }
        public long SessionId { get; }
        public WetInkRibbonGeometry Geometry { get; }
        public WetInkStyleSnapshot Style { get; }
        public long SnapshotVersion { get; }
        public WetInkTargetSnapshot Target { get; }

        public static WetInkCommand Simple(WetInkCommandKind kind, long sessionId = 0)
        {
            return new WetInkCommand(
                kind, sessionId, WetInkRibbonGeometry.Empty, default, 0, default);
        }
    }

    /// <summary>覆盖层几何/DPI 快照。</summary>
    internal readonly struct WetInkTargetSnapshot
    {
        public WetInkTargetSnapshot(
            int screenLeftPixels,
            int screenTopPixels,
            int widthPixels,
            int heightPixels,
            double dpiScaleX,
            double dpiScaleY)
        {
            ScreenLeftPixels = screenLeftPixels;
            ScreenTopPixels = screenTopPixels;
            WidthPixels = widthPixels;
            HeightPixels = heightPixels;
            DpiScaleX = dpiScaleX;
            DpiScaleY = dpiScaleY;
        }

        public int ScreenLeftPixels { get; }
        public int ScreenTopPixels { get; }
        public int WidthPixels { get; }
        public int HeightPixels { get; }
        public double DpiScaleX { get; }
        public double DpiScaleY { get; }

        public bool IsValid => WidthPixels > 0 && HeightPixels > 0;
    }

    /// <summary>
    /// UI/输入线程 → 渲染线程的命令信箱。
    ///
    /// 关键性质：同一 sessionId 的 UpdateStroke 会就地合并（只保留最新快照），
    /// 这样输入速率高于渲染速率时不会堆积；Begin/End/Cancel/Retire 保持顺序语义。
    /// </summary>
    internal sealed class WetInkCommandMailbox
    {
        /// <summary>队列容量上限，超过则丢弃最旧的 Update（保护内存）。</summary>
        private const int MaxQueuedCommands = 512;

        private readonly object _syncRoot = new object();
        private readonly List<WetInkCommand> _queue = new List<WetInkCommand>(64);

        /// <summary>sessionId → 队列内 UpdateStroke 的下标，用于就地合并。</summary>
        private readonly Dictionary<long, int> _pendingUpdateIndex = new Dictionary<long, int>();

        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private bool _shutdown;

        public WaitHandle WaitHandle => _signal;

        public void Post(in WetInkCommand command)
        {
            lock (_syncRoot)
            {
                if (_shutdown && command.Kind != WetInkCommandKind.Shutdown)
                    return;

                if (command.Kind == WetInkCommandKind.UpdateStroke &&
                    _pendingUpdateIndex.TryGetValue(command.SessionId, out var index) &&
                    index < _queue.Count &&
                    _queue[index].Kind == WetInkCommandKind.UpdateStroke &&
                    _queue[index].SessionId == command.SessionId)
                {
                    // 就地合并：只保留最新几何快照。
                    if (command.SnapshotVersion >= _queue[index].SnapshotVersion)
                        _queue[index] = command;
                    _signal.Set();
                    return;
                }

                if (_queue.Count >= MaxQueuedCommands)
                    DropOldestUpdateLocked();

                _queue.Add(command);

                if (command.Kind == WetInkCommandKind.UpdateStroke)
                    _pendingUpdateIndex[command.SessionId] = _queue.Count - 1;
                else if (command.Kind == WetInkCommandKind.Shutdown)
                    _shutdown = true;
            }

            _signal.Set();
        }

        /// <summary>取走当前全部命令（渲染线程调用）。</summary>
        public List<WetInkCommand> DrainAll()
        {
            lock (_syncRoot)
            {
                if (_queue.Count == 0)
                    return null;

                var drained = new List<WetInkCommand>(_queue);
                _queue.Clear();
                _pendingUpdateIndex.Clear();
                return drained;
            }
        }

        private void DropOldestUpdateLocked()
        {
            for (var i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Kind != WetInkCommandKind.UpdateStroke)
                    continue;

                _queue.RemoveAt(i);
                RebuildUpdateIndexLocked();
                return;
            }

            // 没有 Update 可丢时，丢最旧的一条以避免无界增长。
            if (_queue.Count > 0)
            {
                _queue.RemoveAt(0);
                RebuildUpdateIndexLocked();
            }
        }

        private void RebuildUpdateIndexLocked()
        {
            _pendingUpdateIndex.Clear();
            for (var i = 0; i < _queue.Count; i++)
            {
                if (_queue[i].Kind == WetInkCommandKind.UpdateStroke)
                    _pendingUpdateIndex[_queue[i].SessionId] = i;
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                _queue.Clear();
                _pendingUpdateIndex.Clear();
                _shutdown = true;
            }

            _signal.Set();
            _signal.Dispose();
        }
    }
}