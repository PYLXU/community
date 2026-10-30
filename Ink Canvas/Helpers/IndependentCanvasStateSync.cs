using System;
using System.Windows.Ink;
using System.Windows.Media;
using Ink_Canvas.Controls;
using Ink_Canvas.Ink;
// 用别名而不是 using System.Windows.Controls：后者会与 Ink_Canvas.Controls
// 里的 Canvas / Panel 等类型产生歧义。
using InkCanvasEditingMode = System.Windows.Controls.InkCanvasEditingMode;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// 卫星独立画布（UI 联动批注层、内置图片查看器画布）的批注状态同步。
    /// <para>
    /// 把主画布批注栏的状态推给各块卫星画布：<b>逻辑工具 → 编辑模式</b>、笔属性
    /// （颜色/粗细/荧光笔/笔尖/压感/曲线拟合）、橡皮形状、触摸抑制模式、手掌擦策略。
    /// </para>
    /// <para>
    /// 两类画布走的是<b>同一份实现</b>，所以「主工具栏处于批注状态时可以在卫星画布上书写」
    /// 这一行为天然一致，不存在两套语义会随时间漂移。少数能力差异由画布自己声明：
    /// <see cref="IndependentCanvas.RequiresInputWhenIdle"/>（图片查看器在非批注状态下仍要
    /// 接收鼠标/触摸来平移缩放图片）与
    /// <see cref="IndependentCanvas.HostControlsOverlaySuppression"/>（图片查看器自带底栏，
    /// 不接受宿主暂停其浮层）。
    /// </para>
    /// <para>
    /// 同步方向是「主画布 → 卫星画布」的<b>单向轮询</b>（宿主 250ms 一拍）。卫星画布自身的
    /// 瞬时输入状态（多指手势抑制、手掌擦）不会被这一路覆盖：那两项正占用编辑模式与擦头形状，
    /// 轮询时必须让路，见 <see cref="IndependentCanvas.IsInputSuppressionActive"/>。
    /// </para>
    /// </summary>
    internal static class IndependentCanvasStateSync
    {
        /// <summary>主画布侧的一次状态快照，可对多块画布复用。</summary>
        internal sealed class Context
        {
            /// <summary>逻辑工具换算出的目标编辑模式（原生湿墨迹下物理模式是 None，不能用物理模式）。</summary>
            public InkCanvasEditingMode EffectiveMode { get; set; } = InkCanvasEditingMode.None;

            /// <summary>是否处于可书写/可擦除的批注状态。</summary>
            public bool Interactive { get; set; }

            /// <summary>主画布当前的笔属性（会被克隆后写入各画布）。</summary>
            public DrawingAttributes Attributes { get; set; }

            /// <summary>主画布当前的橡皮形状。</summary>
            public StylusShape EraserShape { get; set; }

            /// <summary>
            /// 手掌擦分类器工厂（入参为具体画布）。为 null 表示不同步手掌擦。
            /// 由宿主在 internal 侧构造策略，画布只拿到原始类型委托；
            /// 委托内部读取 <c>canvas.IsPalmErasing</c> 实时状态，因此只需创建一次。
            /// </summary>
            public Func<IndependentCanvas, PalmContactClassifier> PalmClassifierFactory { get; set; }

            /// <summary>
            /// 手掌擦擦头形状工厂（入参为擦头宽度 DIP）。为 null 时画布退化为方形擦头。
            /// 宿主按主画布的擦头口径构造（圆形擦 → EllipseStylusShape；黑板擦 → 38:56 矩形），
            /// 使手掌擦在卫星画布上的擦除范围与主画布一致。
            /// </summary>
            public Func<double, StylusShape> PalmEraserShapeFactory { get; set; }
        }

        /// <summary>把一次状态快照应用到一块卫星画布。</summary>
        internal static void Apply(IndependentCanvas canvas, Context context)
        {
            if (canvas == null || context == null) return;

            ApplyInkState(canvas, context);
            ApplyInputState(canvas, context);
            ApplyPalmEraser(canvas, context);
        }

        /// <summary>按当前批注状态刷新「墨迹书写」按钮等界面提示（幂等，值未变时不做任何事）。</summary>
        internal static void NotifyAnnotationState(bool annotating)
            => Windows.ImageViewerManager.NotifyAnnotationStateChanged(annotating);

        /// <summary>编辑模式、笔属性、橡皮形状。</summary>
        private static void ApplyInkState(IndependentCanvas canvas, Context context)
        {
            var surface = canvas.InkSurface;
            if (surface == null) return;

            // 输入抑制（多指手势 / 手掌擦）期间不改写编辑模式与擦头形状：
            // 这两项此刻被瞬时状态占用（多指抑制临时切 None、手掌擦临时切 EraseByPoint），
            // 每 250ms 的轮询若照抄主画布状态，就会把抑制/擦除打断 —— 表现为
            // 「第二根手指落下仍然落墨」或「手掌擦到一半变回画笔、抬掌后残留擦头尺寸」。
            // 笔属性（颜色/粗细/荧光笔…）只影响下一笔，抑制期间继续同步没有副作用。
            if (!canvas.IsInputSuppressionActive)
            {
                if (surface.EditingMode != context.EffectiveMode)
                    surface.EditingMode = context.EffectiveMode;

                // 面积擦/线擦在卫星画布上走原生 InkCanvas 擦除，形状必须与主画布一致。
                if (context.EraserShape != null)
                    surface.EraserShape = context.EraserShape;
            }

            var attributes = context.Attributes;
            if (attributes != null)
            {
                // 整体克隆而不是白名单比对：荧光笔（IsHighlighter + StylusTip=Rectangle）、
                // IgnorePressure、FitToCurve、StylusTipTransform 都靠 DrawingAttributes 承载，
                // 只比 Color/Width/Height 会让卫星画布永远停在默认的椭圆不透明硬笔上。
                // IsHighlighter 变化时 Color/Width/Height 往往不变，必须一并纳入判定。
                var current = surface.DefaultDrawingAttributes;
                if (current == null || !DrawingAttributesEquivalent(current, attributes))
                    surface.DefaultDrawingAttributes = attributes.Clone();
            }
        }

        /// <summary>命中测试开关、触摸抑制模式、浮层暂停。</summary>
        private static void ApplyInputState(IndependentCanvas canvas, Context context)
        {
            bool interactive = context.Interactive;

            // 默认：非批注状态下整块画布让出命中测试（鼠标/图形/漫游模式不接收输入）。
            // 例外：图片查看器需要在非批注状态下继续接收鼠标/触摸来平移缩放图片，
            // 由 RequiresInputWhenIdle 声明；此时它靠 EditingMode=None 保证不落墨。
            // 抑制期间不动命中测试：手势/手掌擦进行中切换 IsHitTestVisible 会打断当前触摸的捕获。
            bool wantHitTestVisible = interactive || canvas.RequiresInputWhenIdle;
            if (!canvas.IsInputSuppressionActive && canvas.IsHitTestVisible != wantHitTestVisible)
                canvas.IsHitTestVisible = wantHitTestVisible;

            // 多指手势结束后恢复到宿主当前的逻辑模式，而不是画布当时的物理模式
            // （原生笔模式下物理模式是 None，照抄会把画布永久禁用）。
            // 这里用 EffectiveMode 而不是「Ink/None 二选一」：框选工具下被抑制的那一笔
            // 应当恢复到 Select 而不是 Ink，否则手势结束会莫名其妙变成笔。
            canvas.TouchUserEditingMode = context.EffectiveMode;

            // 多指抑制始终可用：卫星画布上两根手指永远不该产生笔迹。
            // 非批注状态下 EditingMode 已是 None，抑制逻辑自然不会改动任何状态。
            canvas.TouchSuppressionEnabled = true;

            // 浮层暂停由宿主决定时只在非批注状态隐藏（鼠标模式下无法交互）；
            // 图片查看器自带底栏与撤销/恢复按钮，恒不接受该控制。
            if (canvas.HostControlsOverlaySuppression)
            {
                if (canvas.IsScrollBarsSuppressed == interactive)
                    canvas.SetScrollBarsSuppressed(!interactive);
            }
        }

        /// <summary>手掌擦：与主画布共用同一套接触面积判定与设置。</summary>
        private static void ApplyPalmEraser(IndependentCanvas canvas, Context context)
        {
            // 擦头形状与「是否启用手掌判定」无关，先挂上：无论主机是否提供手掌判定策略，
            // 只要给了形状工厂，画布在真正进入手掌擦时就能用上（只挂一次，避免每拍换委托）。
            if (context.PalmEraserShapeFactory != null && canvas.PalmEraserShapeFactory == null)
                canvas.PalmEraserShapeFactory = context.PalmEraserShapeFactory;

            var factory = context.PalmClassifierFactory;
            if (factory == null) return;

            // 只在批注状态下启用手掌擦。
            // 图片查看器即使非批注也接收输入（为了手势平移缩放），若不禁用，
            // 鼠标模式下把手掌搁在图片上会把画布切成 EraseByPoint 并擦掉批注。
            if (!context.Interactive)
            {
                canvas.PalmContactClassifier = null;
                return;
            }

            // 只在尚未挂接时创建一次委托，避免每 250ms 分配新闭包。
            // 委托内部实时读取 canvas.IsPalmErasing：策略里的 IsActive 为真时 Evaluate 走
            // 「已在擦除中」分支、不再返回 ActivatesEraser，用错状态会让第二次落掌被误判成首次激活。
            if (canvas.PalmContactClassifier == null)
                canvas.PalmContactClassifier = factory(canvas);
        }

        /// <summary>
        /// 两个 <see cref="DrawingAttributes"/> 是否在「影响绘制结果」的维度上等价。
        /// 用于避免每拍（250ms）都克隆一次属性对象，同时保证荧光笔等属性变化能被察觉。
        /// </summary>
        internal static bool DrawingAttributesEquivalent(DrawingAttributes a, DrawingAttributes b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;

            return a.Color == b.Color
                && a.Width == b.Width
                && a.Height == b.Height
                && a.StylusTip == b.StylusTip
                && a.IsHighlighter == b.IsHighlighter
                && a.IgnorePressure == b.IgnorePressure
                && a.FitToCurve == b.FitToCurve
                && MatrixEquals(a.StylusTipTransform, b.StylusTipTransform);
        }

        private static bool MatrixEquals(Matrix a, Matrix b)
            => a.M11 == b.M11 && a.M12 == b.M12 && a.M21 == b.M21 && a.M22 == b.M22
               && a.OffsetX == b.OffsetX && a.OffsetY == b.OffsetY;
    }
}
