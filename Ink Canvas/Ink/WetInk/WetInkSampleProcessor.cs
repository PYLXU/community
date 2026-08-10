using System;
using System.Collections.Generic;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 采样处理：历史去重排序、红外脏数据过滤、One-Euro 平滑、压感解析。
    /// 每个会话一个实例，非线程安全（由 controller 在锁内调用）。
    /// </summary>
    internal sealed class WetInkSampleProcessor
    {
        /// <summary>最小移动距离（DIP）：小于此距离的点视为抖动噪声，丢弃。</summary>
        private const double MinMoveDistanceDip = 0.35;

        /// <summary>邻近点合并阈值（DIP）：红外框一根手指偶报两点时的重关联半径。</summary>
        public const double NearbyPointMergeRadiusDip = 21.0;

        /// <summary>触摸无真压感时的默认压感。</summary>
        private const float DefaultTouchPressure = 0.5f;

        /// <summary>速度→压感映射：速度越快压感越低（模拟笔锋）。</summary>
        private const double SpeedPressureFalloff = 1.8;

        public WetInkSampleProcessor(bool enableSmoothing, bool simulatePressureFromSpeed)
        {
            _enableSmoothing = enableSmoothing;
            _simulatePressureFromSpeed = simulatePressureFromSpeed;
        }

        private readonly bool _enableSmoothing;
        private readonly bool _simulatePressureFromSpeed;

        private OneEuroFilter _filterX = new OneEuroFilter(minCutoff: 1.0, beta: 0.007);
        private OneEuroFilter _filterY = new OneEuroFilter(minCutoff: 1.0, beta: 0.007);

        private bool _hasLast;
        private double _lastX;
        private double _lastY;
        private long _lastTimestamp;

        /// <summary>
        /// 把一批 newest-first 的原始历史采样，处理成 oldest-first 的干净采样。
        /// 步骤：反转排序 → 去重 → 抖动过滤 → 平滑 → 压感补齐。
        /// </summary>
        public List<WetInkSample> Process(IReadOnlyList<WetInkSample> newestFirst)
        {
            var result = new List<WetInkSample>(newestFirst?.Count ?? 0);
            if (newestFirst == null || newestFirst.Count == 0)
                return result;

            // 1) newest-first → oldest-first，并按 (timestamp, frameId) 去重。
            var ordered = NormalizeHistory(newestFirst);

            for (var i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];

                // 2) 抖动/重复点过滤（红外框噪声）。
                if (_hasLast)
                {
                    var dx = s.X - _lastX;
                    var dy = s.Y - _lastY;
                    if ((dx * dx + dy * dy) < (MinMoveDistanceDip * MinMoveDistanceDip))
                        continue;
                }

                // 3) One-Euro 平滑（红外框坐标抖动明显，笔一般也受益）。
                var x = s.X;
                var y = s.Y;
                if (_enableSmoothing)
                {
                    var tSeconds = s.TimestampMicroseconds / 1_000_000.0;
                    x = _filterX.Filter(x, tSeconds);
                    y = _filterY.Filter(y, tSeconds);
                }

                // 4) 压感解析。
                var pressure = s.Pressure;
                var hasPressure = s.HasPressure;
                if (!hasPressure)
                {
                    pressure = _simulatePressureFromSpeed && _hasLast
                        ? SimulatePressure(x, y, s.TimestampMicroseconds)
                        : DefaultTouchPressure;
                }

                var processed = s.WithPosition(x, y).WithPressure(pressure);
                result.Add(processed);

                _lastX = x;
                _lastY = y;
                _lastTimestamp = s.TimestampMicroseconds;
                _hasLast = true;
            }

            return result;
        }

        /// <summary>
        /// 历史归一化：反转成时间升序，并按 (timestamp, frameId) 去重。
        /// WM_POINTER 历史是 newest-first，且相邻帧可能重叠。
        /// </summary>
        private static List<WetInkSample> NormalizeHistory(IReadOnlyList<WetInkSample> newestFirst)
        {
            var ordered = new List<WetInkSample>(newestFirst.Count);
            for (var i = newestFirst.Count - 1; i >= 0; i--)
                ordered.Add(newestFirst[i]);

            ordered.Sort(static (a, b) =>
            {
                var c = a.TimestampMicroseconds.CompareTo(b.TimestampMicroseconds);
                return c != 0 ? c : a.FrameId.CompareTo(b.FrameId);
            });

            // 去重：相同 (timestamp, frameId) 只保留一个。
            var deduped = new List<WetInkSample>(ordered.Count);
            long lastTs = long.MinValue;
            uint lastFrame = uint.MaxValue;
            for (var i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];
                if (s.TimestampMicroseconds == lastTs && s.FrameId == lastFrame)
                    continue;
                deduped.Add(s);
                lastTs = s.TimestampMicroseconds;
                lastFrame = s.FrameId;
            }

            return deduped;
        }

        private float SimulatePressure(double x, double y, long timestampMicroseconds)
        {
            var dt = (timestampMicroseconds - _lastTimestamp) / 1_000_000.0;
            if (dt <= 0)
                return DefaultTouchPressure;

            var dx = x - _lastX;
            var dy = y - _lastY;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var speed = distance / dt; // DIP/s

            // 归一化到 ~1000 DIP/s 量级，再指数衰减。
            var normalized = speed / 1000.0;
            var pressure = Math.Exp(-SpeedPressureFalloff * normalized);
            return (float)Math.Max(0.15, Math.Min(1.0, pressure));
        }

        public void Reset()
        {
            _hasLast = false;
            _filterX = new OneEuroFilter(minCutoff: 1.0, beta: 0.007);
            _filterY = new OneEuroFilter(minCutoff: 1.0, beta: 0.007);
        }

        /// <summary>
        /// One-Euro 低通滤波器：低速强平滑、高速低延迟，适合手写。
        /// 参考 Casiez et al. 2012。
        /// </summary>
        private struct OneEuroFilter
        {
            public OneEuroFilter(double minCutoff, double beta)
            {
                _minCutoff = minCutoff;
                _beta = beta;
                _dCutoff = 1.0;
                _hasPrev = false;
                _prevValue = 0;
                _prevDerivative = 0;
                _prevTime = 0;
            }

            private readonly double _minCutoff;
            private readonly double _beta;
            private readonly double _dCutoff;
            private bool _hasPrev;
            private double _prevValue;
            private double _prevDerivative;
            private double _prevTime;

            public double Filter(double value, double timeSeconds)
            {
                if (!_hasPrev)
                {
                    _hasPrev = true;
                    _prevValue = value;
                    _prevDerivative = 0;
                    _prevTime = timeSeconds;
                    return value;
                }

                var dt = timeSeconds - _prevTime;
                if (dt <= 0) dt = 1.0 / 120.0;

                var derivative = (value - _prevValue) / dt;
                var dAlpha = Alpha(_dCutoff, dt);
                var smoothedDerivative = dAlpha * derivative + (1 - dAlpha) * _prevDerivative;

                var cutoff = _minCutoff + _beta * Math.Abs(smoothedDerivative);
                var alpha = Alpha(cutoff, dt);
                var filtered = alpha * value + (1 - alpha) * _prevValue;

                _prevValue = filtered;
                _prevDerivative = smoothedDerivative;
                _prevTime = timeSeconds;
                return filtered;
            }

            private static double Alpha(double cutoff, double dt)
            {
                var tau = 1.0 / (2 * Math.PI * cutoff);
                return 1.0 / (1.0 + tau / dt);
            }
        }
    }
}
