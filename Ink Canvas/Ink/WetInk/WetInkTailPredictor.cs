using System;
using System.Collections.Generic;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 湿墨尾部预测：按最近速度外插若干毫秒，抵消输入→显示的固有延迟。
    /// 预测点只进湿墨渲染，绝不进干墨提交。
    ///
    /// 曲率抑制：转弯处外插会「甩出去」，所以曲率越大预测视野越短。
    /// </summary>
    internal sealed class WetInkTailPredictor
    {
        /// <summary>预测视野下限（微秒）。</summary>
        private const long MinHorizonMicroseconds = 14_000;

        /// <summary>预测视野上限（微秒）。</summary>
        private const long MaxHorizonMicroseconds = 30_000;

        /// <summary>预测点数量。</summary>
        private const int PredictedPointCount = 3;

        /// <summary>速度低于该值（DIP/s）时不预测——静止时外插只会抖。</summary>
        private const double MinSpeedDipPerSecond = 60.0;

        /// <summary>曲率大于该弧度时完全放弃预测（急转弯）。</summary>
        private const double MaxTurnRadians = 0.9;

        public WetInkTailPredictor(bool enabled)
        {
            _enabled = enabled;
        }

        private readonly bool _enabled;

        /// <summary>
        /// 依据会话最近的真实点生成预测尾。返回空数组表示本帧不预测。
        /// </summary>
        public WetInkSample[] Predict(IReadOnlyList<WetInkSample> realSamples)
        {
            if (!_enabled || realSamples == null || realSamples.Count < 3)
                return Array.Empty<WetInkSample>();

            var n = realSamples.Count;
            var p2 = realSamples[n - 1];
            var p1 = realSamples[n - 2];
            var p0 = realSamples[n - 3];

            var dt = (p2.TimestampMicroseconds - p1.TimestampMicroseconds) / 1_000_000.0;
            if (dt <= 0)
                return Array.Empty<WetInkSample>();

            var vx = (p2.X - p1.X) / dt;
            var vy = (p2.Y - p1.Y) / dt;
            var speed = Math.Sqrt(vx * vx + vy * vy);
            if (speed < MinSpeedDipPerSecond)
                return Array.Empty<WetInkSample>();

            // 曲率：前后两段方向夹角。
            var turn = TurnAngle(p0, p1, p2);
            if (turn > MaxTurnRadians)
                return Array.Empty<WetInkSample>();

            // 转弯越急，视野越短。
            var turnFactor = 1.0 - Math.Min(1.0, turn / MaxTurnRadians);
            var horizon = (long)(MinHorizonMicroseconds +
                                 (MaxHorizonMicroseconds - MinHorizonMicroseconds) * turnFactor);

            var predicted = new WetInkSample[PredictedPointCount];
            for (var i = 0; i < PredictedPointCount; i++)
            {
                var stepUs = horizon * (i + 1) / (double)PredictedPointCount;
                var stepSeconds = stepUs / 1_000_000.0;

                // 末端逐步衰减，避免尾巴僵直。
                var damping = 1.0 - (i / (double)PredictedPointCount) * 0.35;

                predicted[i] = new WetInkSample(
                    p2.PointerId,
                    p2.InputKind,
                    p2.X + vx * stepSeconds * damping,
                    p2.Y + vy * stepSeconds * damping,
                    p2.Pressure,
                    p2.HasPressure,
                    p2.TimestampMicroseconds + (long)stepUs,
                    p2.FrameId,
                    (p2.Flags & WetInkSampleFlags.InContact) | WetInkSampleFlags.Predicted,
                    p2.ContactWidthPixels,
                    p2.ContactHeightPixels);
            }

            return predicted;
        }

        private static double TurnAngle(WetInkSample a, WetInkSample b, WetInkSample c)
        {
            var v1x = b.X - a.X;
            var v1y = b.Y - a.Y;
            var v2x = c.X - b.X;
            var v2y = c.Y - b.Y;

            var len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            var len2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (len1 <= double.Epsilon || len2 <= double.Epsilon)
                return 0;

            var cos = (v1x * v2x + v1y * v2y) / (len1 * len2);
            cos = Math.Max(-1.0, Math.Min(1.0, cos));
            return Math.Acos(cos);
        }
    }
}