using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;

namespace Ink_Canvas
{
    /// <summary>
    /// 墨迹活动归属跟踪：记录用户「最近一次落笔」落在主画布还是 UI 联动独立画布，
    /// 供撤销/重做快捷键按画布分派。
    /// <para>
    /// 背景：独立画布有自己的墨迹与撤销栈，但全局 Ctrl+Z 原本写死回放主画布。
    /// 用户在 Word 批注区写错后按 Ctrl+Z，撤销掉的却是白板内容，而 Word 上的错误
    /// 笔迹还在——这既没达到预期，又容易误删主画布内容。
    /// </para>
    /// <para>
    /// 判定依据是「最近落笔」而不是焦点或鼠标位置：
    /// UI 联动时焦点在 Word 窗口、鼠标也可能在其它位置，只有落笔位置是可靠的用户意图。
    /// </para>
    /// </summary>
    public partial class MainWindow
    {
        private enum InkActivityTarget
        {
            None = 0,
            Main = 1,
            Independent = 2
        }

        /// <summary>最近一次落笔的画布。</summary>
        private InkActivityTarget _lastInkActivityTarget = InkActivityTarget.None;

        /// <summary>最近一次落笔时刻，用于解绑/重绑后判断记录是否仍然新鲜。</summary>
        private DateTime _lastInkActivityUtc = DateTime.MinValue;

        /// <summary>独立画布落笔记录的新鲜期：超过该时长则不再认定「用户正在批注」。 </summary>
        private static readonly TimeSpan InkActivityFreshness = TimeSpan.FromMinutes(10);

        /// <summary>是否收到过独立画布上的落笔（用于避免把它当成默认撤销目标）。</summary>
        private bool _uiLinkageHasInkActivity;

        /// <summary>独立画布最近一次落笔时间戳（Unix ms）；0 表示无在途笔迹。</summary>
        private long _uiLinkageInkDownTimestampMs;

        private void InitInkActivityTracking()
        {
            if (inkCanvas == null) return;

            // 用 handledEventsToo: true 附加：主画布上的这些事件可能已被其它处理器处理掉，
            // 但我们只关心「用户是否在这里落过笔」。
            inkCanvas.AddHandler(UIElement.StylusDownEvent,
                new StylusDownEventHandler((_, _) => NoteMainCanvasActivity()), true);
            inkCanvas.AddHandler(UIElement.MouseDownEvent,
                new MouseButtonEventHandler((_, _) => NoteMainCanvasActivity()), true);
        }

        private void NoteMainCanvasActivity()
        {
            _lastInkActivityTarget = InkActivityTarget.Main;
            _lastInkActivityUtc = DateTime.UtcNow;
        }

        /// <summary>独立画布创建时挂接落笔/笔迹变化记录。</summary>
        private void AttachUILinkageInkActivityTracking(Ink_Canvas.Controls.IndependentCanvas canvas)
        {
            if (canvas?.InkSurface == null) return;

            // 落笔即认定「用户正在这块画布上批注」（清空/撤销后 InkActivity 仍应保持）。
            canvas.InkSurface.AddHandler(UIElement.StylusDownEvent,
                new StylusDownEventHandler((_, _) => NoteUILinkageCanvasActivity()), true);
            canvas.InkSurface.AddHandler(UIElement.MouseDownEvent,
                new MouseButtonEventHandler((_, _) => NoteUILinkageCanvasActivity()), true);
            canvas.InkSurface.AddHandler(UIElement.TouchDownEvent,
                new EventHandler<TouchEventArgs>((_, _) => NoteUILinkageCanvasActivity()), true);

            // 只认「落笔」这一种信号：不要挂 StrokesChanged —— 滚动同步每拍都会用
            // stroke.Transform 改笔迹几何，长截屏更是逐屏驱动，若把它当成活动信号，
            // 用户只是滚动了一下 Word，Ctrl+Z 就会被劫持到独立画布。
            // 擦除等非落笔操作由各自的调用方显式调用 NoteUILinkageCanvasActivity()。
        }

        private void NoteUILinkageCanvasActivity()
        {
            _uiLinkageHasInkActivity = true;
            _lastInkActivityTarget = InkActivityTarget.Independent;
            _lastInkActivityUtc = DateTime.UtcNow;
            // 落笔时刻：供渐隐动画按「实际书写时长」计算动画时长（与主画布 _stylusDownTimestamp 同义）。
            _uiLinkageInkDownTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        /// <summary>新绑定/重绑独立画布时清掉旧的活动记录，避免撤销被路由到空白画布。</summary>
        private void ResetUILinkageInkActivity()
        {
            _uiLinkageHasInkActivity = false;
            if (_lastInkActivityTarget == InkActivityTarget.Independent)
                _lastInkActivityTarget = InkActivityTarget.None;
        }

        /// <summary>供撤销/重做快捷键取用的「当前应作用的独立画布」；无可作用对象返回 null。</summary>
        private Ink_Canvas.Controls.IndependentCanvas GetActiveUILinkageCanvas()
        {
            if (!_uiLinkageHasInkActivity) return null;
            if (_lastInkActivityTarget != InkActivityTarget.Independent) return null;
            if (DateTime.UtcNow - _lastInkActivityUtc > InkActivityFreshness) return null;

            var inst = _uiLinkageInstances.FirstOrDefault(i =>
                i.Bound && i.Canvas != null && i.Canvas.Visibility == Visibility.Visible);
            return inst?.Canvas;
        }

        /// <summary>
        /// 快捷键撤销：最近落笔在独立画布时撤回它，返回 true 表示已处理（不再走主画布）。
        /// 独立画布自身没有可撤销内容时返回 false 并交回主画布，避免快捷键「按了没反应」。
        /// </summary>
        private bool TryUndoOnActiveUILinkageCanvas()
        {
            var canvas = GetActiveUILinkageCanvas();
            if (canvas == null) return false;
            if (!canvas.InkHistoryCanUndo) return false;

            canvas.InkHistoryUndo();
            HideSubPanels();
            return true;
        }

        /// <summary>快捷键重做：语义同 <see cref="TryUndoOnActiveUILinkageCanvas"/>。</summary>
        private bool TryRedoOnActiveUILinkageCanvas()
        {
            var canvas = GetActiveUILinkageCanvas();
            if (canvas == null) return false;
            if (!canvas.InkHistoryCanRedo) return false;

            canvas.InkHistoryRedo();
            HideSubPanels();
            return true;
        }
    }
}
