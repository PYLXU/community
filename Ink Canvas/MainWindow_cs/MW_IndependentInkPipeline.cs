using Ink_Canvas.Helpers;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Threading;

namespace Ink_Canvas
{
    /// <summary>
    /// 独立画布（UI 联动批注层）的收笔后处理适配层。
    /// <para>
    /// 定位：独立画布的内层是一块裸 <see cref="System.Windows.Controls.InkCanvas"/>，
    /// 它不会经过主画布的 <c>ProcessCommittedStroke</c> 管线。本类只负责把
    /// <b>宿主现成的服务</b>接到独立画布的笔迹集合上，不复制主画布的算法。
    /// </para>
    /// <para>
    /// <b>已接通的项</b>（入口见 <see cref="IndependentCanvas_StrokeCollected"/>）：
    /// </para>
    /// <list type="bullet">
    /// <item>压感归一 —— 复用 <c>Settings.Canvas.DisablePressure</c> 的语义（与主画布同口径）。</item>
    /// <item>触摸压感模拟 —— 复用 <c>TryApplyTouchPressureSimulation</c>，含 <c>AbortPipeline</c> 透传。</item>
    /// <item>实时笔锋 —— 复用 <c>ShouldApplyVelocityBrushTip</c> / <c>ApplyVelocityBrushTipFromSpeed</c>。</item>
    /// <item>直线拉直 + 端点吸附 —— 复用主画布同一批判定与几何生成
    /// （<see cref="TryStraightenIndependentStroke"/>，吸附候选只取本画布笔迹）。</item>
    /// <item>形状识别 —— 走抽取出的共享核心 <c>RunInkToShapeCorrectionCoreAsync</c>，
    /// 每实例一份 <c>InkToShapeTargetContext</c>（缓冲/替换实现都不与主画布共享）。</item>
    /// <item>墨迹平滑 —— 复用宿主共享的 <see cref="InkSmoothingManager"/>（含
    /// <see cref="InkSmoothingManager.CopyPropertyData"/>，保证激光标记等 property data 不丢）。</item>
    /// <item>墨迹渐隐 / 激光笔 —— 复用同一个 <see cref="InkFadeManager"/>，视觉宿主换成
    /// 独立画布自己的渐隐层（<c>IndependentCanvas.FadeVisualHost</c>）。</item>
    /// </list>
    /// <para>
    /// <b>仍然刻意不接的项与原因</b>（避免「改了但没效果」或引入回归）：
    /// </para>
    /// <list type="bullet">
    /// <item><b>形状识别的矩形参考线 / 选区遮罩</b>：二者是主画布专属 chrome，依赖主画布的
    /// 笔迹集合与 <c>TimeMachine</c> 提交语义，独立画布的上下文里显式留空
    /// （见 <c>ProcessGuideLines</c> / <c>HideSelectionCover</c>）。</item>
    /// <item><b>FitToCurve 切换与手写体美化</b>：前者作用于「下一笔」的
    /// <c>DrawingAttributes</c>，而独立画布的该属性由宿主每 250ms 从主画布整对象同步
    /// （改完即被覆盖，改了没有效果）；后者绑定 WinRT 湿墨迹输入路径，独立画布走
    /// WPF 原生墨迹收集，不共享该输入路径。</item>
    /// <item><b>实时（书写中）笔锋</b>：主画布在落笔过程中就按速率改笔尖，
    /// 独立画布依赖 WPF 原生收集、只能在抬笔后一次性应用。</item>
    /// </list>
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>独立画布收笔事件处理：按与主画布一致的开关做压感归一与平滑。</summary>
        private void IndependentCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            var stroke = e?.Stroke;
            if (stroke == null) return;

            var canvas = sender as Ink_Canvas.Controls.IndependentCanvas;
            var surface = canvas?.Strokes;
            if (surface == null || !surface.Contains(stroke)) return;

            // 批注也是「用户在画布上落了墨」。不刷新这个时间戳，延迟自动冻结的判据
            // （MW_InkFreeze.cs:471 读 pageLastUserInkMutationUtc）就看不到联动书写，
            // 于是可能在用户正对着 Word 批注时把白板页冻上 —— 紧随其后的那一笔会被
            // 冻结页拦截并回滚。时间戳按白板页索引，批注不改变页归属，只表示
            // 「刚刚发生过用户墨迹动作」。
            MarkCurrentPageInkChanged();

            // 冻结页：交给主画布同一套拦截提示，不在此静默吞掉。
            if (IsCurrentPageFrozen)
            {
                TryBlockFrozenPageMutation("在联动画布上书写");
                try
                {
                    if (surface.Contains(stroke)) surface.Remove(stroke);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile(
                        $"冻结页回滚联动画布笔迹失败: {ex.Message}", LogHelper.LogType.Warning);
                }
                return;
            }

            try
            {
                // 激光笔 / 墨迹渐隐：与主画布同一分支、同一管理器（InkFadeManager），
                // 只是把视觉宿主换成独立画布自己的渐隐层。语义与主画布一致：
                // 渐隐笔迹「根本不进 InkCanvas.Strokes」，因此必须静默移除且不进撤销栈。
                if (TryFadeIndependentStroke(canvas, stroke))
                    return;

                ApplyIndependentStrokePressurePolicy(stroke);
                _independentTouchPressureSimulationApplied = false;
                _independentAbortPostProcess = false;
                ApplyIndependentStrokePressureSimulation(stroke);
                // 与主画布管线一致：AbortPipeline 分支后不再做任何后处理。
                if (_independentAbortPostProcess) return;
                ApplyIndependentVelocityBrushTip(stroke);

                // 直线拉直 + 端点吸附：复用主画布同一批判定与几何生成（见 TryStraightenIndependentStroke）。
                var wasStraightened = TryStraightenIndependentStroke(canvas, stroke, out stroke);
                if (stroke == null || !surface.Contains(stroke)) return;

                // 形状识别：与主画布同一条核心（RunInkToShapeCorrectionCoreAsync），
                // 识别完成后同样要跑一次尾部（压感合成 + 平滑）。
                var inst = FindUILinkageInstanceByCanvas(canvas);
                if (inst != null && ShapeRecognitionRouter.ShouldRunShapeRecognition(
                        Settings.InkToShape.IsInkToShapeEnabled,
                        ShapeRecognitionRouter.FromSettingsInt(Settings.InkToShape.ShapeRecognitionEngine)))
                {
                    ScheduleUILinkageInkToShape(inst, stroke, wasStraightened);
                    return;
                }

                ApplyIndependentPostStrokeTail(surface, stroke, wasStraightened);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"联动画布收笔后处理失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>按画布反查联动实例（形状识别需要实例级缓冲与替换实现）。</summary>
        private UILinkageCanvasInstance FindUILinkageInstanceByCanvas(
            Ink_Canvas.Controls.IndependentCanvas canvas)
        {
            if (canvas == null) return null;
            foreach (var inst in _uiLinkageInstances)
            {
                if (inst?.Canvas != null && ReferenceEquals(inst.Canvas, canvas)) return inst;
            }

            return null;
        }

        /// <summary>
        /// 直线拉直 + 端点吸附（独立画布版）。
        /// <para>
        /// 判定与几何全部复用主画布管线的方法：<c>IsPotentialStraightLine</c>、
        /// <c>TryGetStraightLineEndpoints</c>（含阈值/吸附设置）、<c>GetSnappedEndpoints</c>、
        /// <c>CreateStraightLine</c>。差异只在<b>作用对象</b>：
        /// </para>
        /// <list type="bullet">
        /// <item>端点吸附候选 = 本画布自己的笔迹（两块画布坐标系不同，绝不互吸）；</item>
        /// <item>替换走 <see cref="Ink_Canvas.Controls.IndependentCanvas.ReplaceStrokes"/>，
        /// 因此独立画布撤销栈只记一步（主画布走 TimeMachine 的形状纠正提交）。</item>
        /// </list>
        /// </summary>
        /// <returns>true 表示发生了拉直（调用方据此跳过平滑，与主画布 <c>wasStraightened</c> 同义）。</returns>
        private bool TryStraightenIndependentStroke(
            Ink_Canvas.Controls.IndependentCanvas canvas, Stroke stroke, out Stroke result)
        {
            result = stroke;
            if (canvas == null || stroke == null) return false;
            if (!Settings.InkToShape.IsInkToShapeEnabled) return false;
            if (!Settings.Canvas.AutoStraightenLine) return false;
            if (!IsPotentialStraightLine(stroke)) return false;
            if (!TryGetStraightLineEndpoints(stroke, out var endpoint1, out var endpoint2)) return false;

            var startPoint = endpoint1;
            var endPoint = endpoint2;

            // 与主画布一致：仅当启用矩形/三角形识别时才做端点吸附。
            if (Settings.Canvas.LineEndpointSnapping
                && (Settings.InkToShape.IsInkToShapeRectangle || Settings.InkToShape.IsInkToShapeTriangle))
            {
                var snappedPoints = GetSnappedEndpoints(startPoint, endPoint, canvas.Strokes);
                if (snappedPoints != null)
                {
                    startPoint = snappedPoints[0];
                    endPoint = snappedPoints[1];
                }
            }

            var attributes = stroke.DrawingAttributes?.Clone()
                             ?? canvas.InkSurface?.DefaultDrawingAttributes?.Clone();
            var straightStroke = new Stroke(CreateStraightLine(startPoint, endPoint))
            {
                DrawingAttributes = attributes
            };
            straightStroke.AddPropertyData(Helpers.ModernInkAnalyzer.ShapeStrokePropertyGuid, true);

            canvas.ReplaceStrokes(new StrokeCollection { stroke }, new StrokeCollection { straightStroke });
            result = straightStroke;
            return true;
        }

        /// <summary>
        /// 收笔尾部（独立画布版）：与主画布 <c>RunStrokeCollectedPostShapeRecognitionTail</c> 同口径。
        /// <list type="bullet">
        /// <item>「压感均匀」前置判定：真实压感笔（压感非均匀）与主画布一样不在尾部改写压感；</item>
        /// <item>均匀压感（触摸/鼠标/忽略压感）时按 <c>InkStyle</c> 合成压感，复用
        /// <c>TryApplyInkStyleTouchPressure</c>；</item>
        /// <item>未拉直时才做高级贝塞尔平滑，复用宿主共享的 <c>InkSmoothingManager</c>。</item>
        /// </list>
        /// <para>
        /// 有意不复制主画布 tail 里的 <c>FitToCurve</c> 切换与手写体美化：
        /// 前者作用于「下一笔」的 DrawingAttributes，而独立画布的 DrawingAttributes 由宿主每 250ms
        /// 从主画布整对象同步（本画布改完即被覆盖，改了也没有效果）；后者绑定 WinRT 湿墨迹输入路径，
        /// 独立画布走 WPF 原生墨迹收集，不共享该输入路径。
        /// </para>
        /// </summary>
        private void ApplyIndependentPostStrokeTail(StrokeCollection surface, Stroke stroke, bool wasStraightened)
        {
            if (surface == null || stroke == null || !surface.Contains(stroke)) return;

            try
            {
                bool pressureIsUniform = !stroke.StylusPoints.Any(p =>
                    (p.PressureFactor > 0.501 || p.PressureFactor < 0.5) && p.PressureFactor != 0);
                if (!pressureIsUniform) return;

                if (!Settings.Canvas.DisablePressure && !wasStraightened)
                {
                    // 与主画布 tail 一致：case 0 且仅 2 点时中止尾部后续处理。
                    if (TryApplyInkStyleTouchPressure(stroke) == InkStylePressureOutcome.AbortTail) return;
                }

                if (Settings.Canvas.UseAdvancedBezierSmoothing && !wasStraightened)
                    TrySmoothIndependentStroke(surface, stroke);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"联动画布收笔尾部处理失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 异步调度「墨迹转形状」：与主画布一样延后到 <see cref="DispatcherPriority.ApplicationIdle"/>
        /// 执行，避免在 <c>StrokeCollected</c> 内同步等待识别结果造成抬笔卡顿。
        /// </summary>
        private void ScheduleUILinkageInkToShape(
            UILinkageCanvasInstance inst, Stroke stroke, bool wasStraightened)
        {
            var canvas = inst?.Canvas;
            if (canvas == null) return;

            var ctx = GetOrCreateUILinkageInkToShapeContext(inst);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await RunInkToShapeCorrectionCoreAsync(ctx, stroke);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }

                // 与主画布一致：形状纠正后仍要跑一次尾部（压感合成 / 平滑）。
                ApplyIndependentPostStrokeTail(canvas.Strokes, stroke, wasStraightened);
            }), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>
        /// 独立画布的「墨迹转形状」上下文：缓冲与替换实现都是本画布专属。
        /// </summary>
        private InkToShapeTargetContext GetOrCreateUILinkageInkToShapeContext(UILinkageCanvasInstance inst)
        {
            if (inst.InkToShapeContext != null) return inst.InkToShapeContext;

            var canvas = inst.Canvas;
            inst.InkToShapeContext = new InkToShapeTargetContext
            {
                Surface = canvas.Strokes,
                DefaultAttributesProvider = () => canvas.InkSurface?.DefaultDrawingAttributes,
                SnapTargets = canvas.Strokes,
                Replace = (toRemove, replacements, fallback) =>
                    ReplaceUILinkageStrokes(canvas, toRemove, replacements, fallback),

                // 以下两项是主画布专属 chrome：矩形参考线系统依赖主画布的笔迹集合与
                // TimeMachine 提交语义；选区遮罩是主画布的选择工具视觉。独立画布均无，保持 null。
                ProcessGuideLines = null,
                HideSelectionCover = null,

                CanvasSize = () => canvas.ViewportSize
            };
            return inst.InkToShapeContext;
        }

        /// <summary>
        /// 独立画布的笔迹替换：把 <paramref name="fallback"/>（通常是被识别的原笔迹）并入待移除集合，
        /// 再走 <see cref="Ink_Canvas.Controls.IndependentCanvas.ReplaceStrokes"/>，
        /// 使形状纠正（含已移除的原笔迹）在独立画布撤销栈里只占一步。
        /// </summary>
        private static void ReplaceUILinkageStrokes(
            Ink_Canvas.Controls.IndependentCanvas canvas,
            StrokeCollection toRemove, StrokeCollection replacements, Stroke fallback)
        {
            if (canvas == null || replacements == null || replacements.Count == 0) return;

            var removed = new StrokeCollection();
            if (toRemove != null)
            {
                foreach (var s in toRemove)
                    if (canvas.Strokes.Contains(s) && !removed.Contains(s)) removed.Add(s);
            }

            if (fallback != null && canvas.Strokes.Contains(fallback) && !removed.Contains(fallback))
                removed.Add(fallback);

            canvas.ReplaceStrokes(removed, replacements);
        }

        /// <summary>
        /// 切换文档/重绑时清空形状识别缓冲：旧文档的笔迹引用与坐标都不应参与新文档的识别。
        /// </summary>
        private void ResetUILinkageInkToShapeBuffer(UILinkageCanvasInstance inst)
        {
            var ctx = inst?.InkToShapeContext;
            if (ctx == null) return;
            ctx.RecentStrokes.Clear();
            ctx.Circles.Clear();
        }

        /// <summary>
        /// 激光笔/渐隐分支：复用主画布的 <see cref="InkFadeManager"/>（含激光 PropertyData 标记、
        /// 按落笔时长的动画时长、分段/统一渐隐动画），仅把视觉宿主换成独立画布的渐隐层。
        /// </summary>
        /// <returns>true 表示已接管该笔迹（调用方必须立即返回，不得再做后续处理）。</returns>
        private bool TryFadeIndependentStroke(
            Ink_Canvas.Controls.IndependentCanvas canvas, Stroke stroke)
        {
            // 与主画布同口径：_inkFadeManager.IsEnabled 只在「激光笔 + 设置开启墨迹渐隐」时为 true，
            // 因此普通笔迹在独立画布上仍会永久保留（不会被误渐隐）。
            if (_inkFadeManager == null || !_inkFadeManager.IsEnabled) return false;

            try
            {
                var startPoint = stroke.StylusPoints.Count > 0
                    ? stroke.StylusPoints[0].ToPoint()
                    : new Point();
                var endPoint = stroke.StylusPoints.Count > 0
                    ? stroke.StylusPoints[stroke.StylusPoints.Count - 1].ToPoint()
                    : new Point();

                if (penType == 2 && !stroke.ContainsPropertyData(InkFadeManager.LaserRenderModeGuid))
                    stroke.AddPropertyData(InkFadeManager.LaserRenderModeGuid, true);

                long strokeDurationMs = 0;
                if (_uiLinkageInkDownTimestampMs > 0)
                {
                    strokeDurationMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _uiLinkageInkDownTimestampMs;
                    _uiLinkageInkDownTimestampMs = 0;
                }

                _inkFadeManager.AddFadingStroke(stroke, startPoint, endPoint, strokeDurationMs,
                    canvas.FadeVisualHost);

                // 与主画布一致：渐隐笔迹从画布集合移除（视觉由渐隐层继续渲染到消失）。
                // 差别：这里额外保证「不进独立画布撤销栈」，否则撤销会把渐隐掉的笔迹贴回来。
                canvas.RemoveStrokesSilently(new StrokeCollection { stroke });
                canvas.ForgetStrokeAddition(stroke);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"联动画布渐隐处理失败: {ex.Message}", LogHelper.LogType.Warning);
                // 失败则退回普通笔迹路径（笔迹留在画布上），不要静默丢墨迹。
                return false;
            }
        }

        /// <summary>
        /// 与主画布同口径的压感归一：开启「屏蔽压感」时把所有点的 PressureFactor 归到 0.5，
        /// 保证独立画布上的线条粗细与主画布一致。
        /// </summary>
        private static void ApplyIndependentStrokePressurePolicy(Stroke stroke)
        {
            if (stroke?.StylusPoints == null || stroke.StylusPoints.Count == 0) return;
            if (!Settings.Canvas.DisablePressure) return;

            var alreadyUniform = stroke.StylusPoints.All(p => p.PressureFactor == 0.5f);
            if (alreadyUniform) return;

            var normalized = new StylusPointCollection();
            foreach (var point in stroke.StylusPoints)
            {
                normalized.Add(new StylusPoint(point.X, point.Y, 0.5f));
            }

            stroke.StylusPoints = normalized;
        }

        /// <summary>
        /// 触摸压感模拟：直接复用主画布收笔管线的 <see cref="TryApplyTouchPressureSimulation"/>，
        /// 与主画布共用同一份曲线算法与开关（<c>EnablePressureTouchMode</c>/<c>InkStyle</c>）。
        /// 返回的 <c>AbortPipeline</c> 语义与主画布一致：该笔迹不再走后续平滑。
        /// </summary>
        private void ApplyIndependentStrokePressureSimulation(Stroke stroke)
        {
            // 「屏蔽压感」已做归一，此时与主画布一样跳过触摸模拟。
            if (Settings.Canvas.DisablePressure) return;

            var outcome = TryApplyTouchPressureSimulation(stroke);
            _independentTouchPressureSimulationApplied = outcome == TouchPressureSimulationOutcome.Applied;
            _independentAbortPostProcess = outcome == TouchPressureSimulationOutcome.AbortPipeline;
        }

        /// <summary>
        /// 实时笔锋：复用主画布管线的 <see cref="ShouldApplyVelocityBrushTip"/> +
        /// <see cref="ApplyVelocityBrushTipFromSpeed"/>（同一判定、同一实现）。
        /// </summary>
        private void ApplyIndependentVelocityBrushTip(Stroke stroke)
        {
            if (!ShouldApplyVelocityBrushTip(stroke, false, _independentTouchPressureSimulationApplied)) return;
            ApplyVelocityBrushTipFromSpeed(stroke);
        }

        /// <summary>本次收笔是否应用了触摸压感模拟（笔锋判定需要）。</summary>
        private bool _independentTouchPressureSimulationApplied;

        /// <summary>本次收笔是否应终止后续后处理（与主画布管线的 AbortPipeline 分支同义）。</summary>
        private bool _independentAbortPostProcess;

        /// <summary>
        /// 复用宿主共享的 <see cref="InkSmoothingManager"/> 做高级贝塞尔平滑。
        /// 平滑器返回新实例时用单次 <c>Strokes.Replace</c> 替换，因此独立画布撤销栈只增加一步。
        /// </summary>
        private void TrySmoothIndependentStroke(StrokeCollection surface, Stroke stroke)
        {
            if (surface == null || stroke == null) return;
            if (!surface.Contains(stroke)) return; // 与主画布 tail 一致：笔迹已不在画布上就跳过
            if (!Settings.Canvas.UseAdvancedBezierSmoothing) return;
            if (_inkSmoothingManager == null) return;

            var smoothed = _inkSmoothingManager.SmoothStroke(stroke);
            if (smoothed == null || ReferenceEquals(smoothed, stroke)) return;

            // 平滑器只克隆 DrawingAttributes，会丢激光标记等 property data。
            InkSmoothingManager.CopyPropertyData(stroke, smoothed);

            ReplaceIndependentStrokes(surface, new StrokeCollection { stroke }, new StrokeCollection { smoothed });
        }

        /// <summary>
        /// 在指定集合上做「移除 + 新增」的替换。
        /// 集合属于独立画布时走 <see cref="Ink_Canvas.Controls.IndependentCanvas.ReplaceStrokes"/>，
        /// 使其撤销栈只记一步。
        /// </summary>
        private void ReplaceIndependentStrokes(
            StrokeCollection surface, StrokeCollection removed, StrokeCollection replacements)
        {
            if (surface == null || replacements == null || replacements.Count == 0) return;

            foreach (var inst in _uiLinkageInstances)
            {
                if (inst?.Canvas?.Strokes == null || !ReferenceEquals(inst.Canvas.Strokes, surface)) continue;
                inst.Canvas.ReplaceStrokes(removed, replacements);
                return;
            }

            // 未归属任何联动实例（理论上不会发生）：退回集合级替换，保证不丢笔迹。
            try
            {
                if (removed != null && removed.Count > 0) surface.Remove(removed);
                surface.Add(replacements);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"联动画布笔迹替换失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }
    }
}
