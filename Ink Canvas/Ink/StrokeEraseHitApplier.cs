using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Ink;

namespace Ink_Canvas.Ink
{
    /// <summary>
    /// 增量几何擦的「命中应用」：把一次 <see cref="InkCanvas.Strokes"/> 命中事件
    /// 变成对目标笔迹集合的替换或删除。
    /// <para>
    /// 主画布几何擦（<c>EraserGeometry_StrokeHit</c>）与 UI 联动批注层几何擦
    /// （<c>UILinkageEraser_StrokeHit</c>）原本各有一份几乎相同的实现，
    /// 现在共用这一段，保证「擦到什么程度、留下什么」在任何画布上都一致，不会再各自漂移。
    /// </para>
    /// <para>
    /// 该段逻辑与 WPF <see cref="InkCanvas"/> 在 <c>EraseByPoint</c> 下内部所做的事<b>同构</b>：
    /// 都是「用同一个 <see cref="StylusShape"/> 建增量命中测试器 → 拿到
    /// <see cref="StrokeHitEventArgs.GetPointEraseResults"/> → 用残段替换原笔迹（无残段则整条删除）」。
    /// 差别只在于谁驱动它，以及这里额外支持锁定笔迹过滤。
    /// </para>
    /// </summary>
    internal static class StrokeEraseHitApplier
    {
        /// <summary>
        /// 应用一次面积擦（几何擦）命中。
        /// </summary>
        /// <param name="target">被擦除的笔迹集合。</param>
        /// <param name="args">增量命中测试器给出的命中事件参数。</param>
        /// <param name="frozenPropertyGuid">
        /// 锁定笔迹标记：带该标记的笔迹不参与擦除。传 null 表示不做过滤。
        /// </param>
        public static void Apply(StrokeCollection target, StrokeHitEventArgs args, Guid? frozenPropertyGuid)
        {
            if (target == null || args?.HitStroke == null) return;

            var strokesToReplace = new List<Stroke>(1);
            if (IsErasable(args.HitStroke, frozenPropertyGuid)) strokesToReplace.Add(args.HitStroke);
            if (strokesToReplace.Count == 0) return;

            var eraseResult = args.GetPointEraseResults()
                .Where(stroke => IsErasable(stroke, frozenPropertyGuid))
                .ToList();

            if (eraseResult.Count > 0)
                target.Replace(new StrokeCollection(strokesToReplace), new StrokeCollection(eraseResult));
            else
                target.Remove(new StrokeCollection(strokesToReplace));
        }

        /// <summary>
        /// 应用一次线擦（笔画擦）命中：命中即整条移除。
        /// </summary>
        /// <param name="target">被擦除的笔迹集合。</param>
        /// <param name="point">命中点（集合自身的坐标系）。</param>
        /// <param name="frozenPropertyGuid">锁定笔迹标记；null 表示不过滤。</param>
        /// <returns>是否真的移除了笔迹。</returns>
        public static bool ApplyWholeStrokeAt(StrokeCollection target, Point point, Guid? frozenPropertyGuid)
        {
            if (target == null) return false;

            var hits = target.HitTest(point)
                .Where(stroke => IsErasable(stroke, frozenPropertyGuid))
                .ToArray();
            if (hits.Length == 0) return false;

            target.Remove(new StrokeCollection(hits));
            return true;
        }

        private static bool IsErasable(Stroke stroke, Guid? frozenPropertyGuid)
            => stroke != null
               && (!frozenPropertyGuid.HasValue || !stroke.ContainsPropertyData(frozenPropertyGuid.Value));
    }
}
