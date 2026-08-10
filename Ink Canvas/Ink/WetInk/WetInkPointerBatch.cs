using System;
using System.Collections.Generic;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 一批 newest-first 的原始采样，来自 WM_POINTER 历史或 WPF 兜底桥。
    /// </summary>
    internal sealed class WetInkPointerBatch
    {
        public WetInkPointerBatch(
            uint pointerId,
            WetInkInputKind inputKind,
            IReadOnlyList<WetInkSample> samplesNewestFirst,
            bool isPromotedMouse,
            bool isSecondaryBarrelButtonDown,
            bool historyIncomplete,
            bool historyError)
        {
            PointerId = pointerId;
            InputKind = inputKind;
            SamplesNewestFirst = samplesNewestFirst ?? Array.Empty<WetInkSample>();
            IsPromotedMouse = isPromotedMouse;
            IsSecondaryBarrelButtonDown = isSecondaryBarrelButtonDown;
            HistoryIncomplete = historyIncomplete;
            HistoryError = historyError;
        }

        public uint PointerId { get; }
        public WetInkInputKind InputKind { get; }

        /// <summary>newest first（WM_POINTER 历史顺序），由处理器反转/去重。</summary>
        public IReadOnlyList<WetInkSample> SamplesNewestFirst { get; }

        /// <summary>由笔/触摸提升为鼠标的消息（WI_SIGNATURE）。</summary>
        public bool IsPromotedMouse { get; }

        public bool IsSecondaryBarrelButtonDown { get; }

        /// <summary>历史缓冲不足，中间段可能丢失。</summary>
        public bool HistoryIncomplete { get; }

        public bool HistoryError { get; }

        public static readonly WetInkPointerBatch Empty = new WetInkPointerBatch(
            0, WetInkInputKind.Unknown, Array.Empty<WetInkSample>(), false, false, false, false);
    }
}