using Ink_Canvas.Helpers;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace Ink_Canvas
{
    public partial class MainWindow
    {
        /// <summary>
        /// 「墨迹转形状」纠正的目标画布上下文：把主画布收笔管线里内联的识别/替换逻辑
        /// 参数化，使<b>同一份实现</b>可以作用在第二块画布（UI 联动独立画布）上，而不必复制算法。
        /// <para>
        /// 每块画布各自持有一份上下文：笔迹集合、DefaultDrawingAttributes、最近笔迹缓冲、
        /// 已纠正圆列表都互相隔离，因此不会出现「主画布与独立画布串写」的问题。
        /// </para>
        /// </summary>
        internal sealed class InkToShapeTargetContext
        {
            /// <summary>目标笔迹集合（主画布 = inkCanvas.Strokes；独立画布 = Canvas.Strokes）。</summary>
            public StrokeCollection Surface;

            /// <summary>
            /// 新笔迹 DrawingAttributes 的来源（各自画布的 DefaultDrawingAttributes）。
            /// 以「提供者」而非快照保存：主画布原实现在每一处使用点即时读取
            /// <c>inkCanvas.DefaultDrawingAttributes</c>，且宿主会周期性整对象替换独立画布的
            /// DrawingAttributes（工具同步），保存快照会拿到过期实例。
            /// </summary>
            public Func<DrawingAttributes> DefaultAttributesProvider;

            /// <summary>当前 DrawingAttributes（延迟读取，语义等同原实现的即时读取）。</summary>
            public DrawingAttributes DefaultAttributes => DefaultAttributesProvider?.Invoke();

            /// <summary>最近笔迹缓冲（多笔形状/同心圆判定用）；每块画布各自一份。</summary>
            public StrokeCollection RecentStrokes = new StrokeCollection();

            /// <summary>已纠正的圆（同心圆/相切判定用）；每块画布各自一份。</summary>
            public System.Collections.Generic.List<Circle> Circles
                = new System.Collections.Generic.List<Circle>();

            /// <summary>端点吸附候选集合；null 表示不做吸附。</summary>
            public StrokeCollection SnapTargets;

            /// <summary>
            /// 替换实现：移除 <c>toRemove</c>（以及仍在画布上的 <c>fallback</c>）并加入 <c>replacements</c>。
            /// 主画布走 <c>ReplaceStrokesSafely</c>（保留 TimeMachine 的形状纠正提交语义）；
            /// 独立画布走 <c>IndependentCanvas.ReplaceStrokes</c>（撤销栈只记一步）。
            /// </summary>
            public Action<StrokeCollection, StrokeCollection, Stroke> Replace;

            /// <summary>矩形参考线系统（仅主画布启用；独立画布为 null）。</summary>
            public Action<Stroke> ProcessGuideLines;

            /// <summary>纠正完成后隐藏选择框（仅主画布有该 chrome；独立画布为 null）。</summary>
            public Action HideSelectionCover;

            /// <summary>拟合度门禁用的画布尺寸提供者（延迟取值，避免缓存过期尺寸）。</summary>
            public Func<Size> CanvasSize;
        }

        /// <summary>
        /// 主画布的「墨迹转形状」上下文：复用 <c>newStrokes</c>/<c>circles</c> 字段，保持既有语义
        /// （字段实例被同一个上下文引用，行为与重构前一致）。
        /// </summary>
        private InkToShapeTargetContext CreateMainCanvasInkToShapeContext()
        {
            return new InkToShapeTargetContext
            {
                Surface = inkCanvas?.Strokes,
                DefaultAttributesProvider = () => inkCanvas?.DefaultDrawingAttributes,
                RecentStrokes = newStrokes,
                Circles = circles,
                SnapTargets = inkCanvas?.Strokes,
                Replace = (toRemove, replacements, fallback) => ReplaceStrokesSafely(toRemove, replacements, fallback),
                ProcessGuideLines = ProcessRectangleGuideLines,
                HideSelectionCover = () => GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed,
                CanvasSize = () => new Size(inkCanvas?.ActualWidth ?? 0, inkCanvas?.ActualHeight ?? 0)
            };
        }

        /// <summary>
        /// 墨迹转形状纠正核心：由主画布 <c>ProcessCommittedStroke</c> 内的本地函数原样抽出，
        /// 仅把「目标画布相关」的部分改为经由 <see cref="InkToShapeTargetContext"/> 注入。
        /// <para>
        /// 识别引擎（WinRT ModernInkAnalyzer / IACore IPC）、拟合度门禁、几何生成
        /// （<c>GenerateEllipseGeometry</c> / <c>GenerateFakePressure*</c> / <c>FixPointsDirection</c>）、
        /// 替换时机与顺序全部与重构前逐行一致，因此主画布行为不变。
        /// </para>
        /// </summary>
        private async System.Threading.Tasks.Task RunInkToShapeCorrectionCoreAsync(
            InkToShapeTargetContext ctx, Stroke targetStroke)
        {
            if (ctx?.Surface == null || targetStroke == null) return;

            await InkToShapeSerial.WaitAsync().ConfigureAwait(true);
            try
            {
                ctx.RecentStrokes.Add(targetStroke);
                if (ctx.RecentStrokes.Count > 4) ctx.RecentStrokes.RemoveAt(0);
                for (var i = 0; i < ctx.RecentStrokes.Count; i++)
                    if (!ctx.Surface.Contains(ctx.RecentStrokes[i]))
                        ctx.RecentStrokes.RemoveAt(i--);

                for (var i = 0; i < ctx.Circles.Count; i++)
                    if (!ctx.Surface.Contains(ctx.Circles[i].Stroke))
                        ctx.Circles.RemoveAt(i);

                // 处理矩形参考线系统
                ctx.ProcessGuideLines?.Invoke(targetStroke);

                var shapeMode = ShapeRecognitionRouter.FromSettingsInt(Settings.InkToShape.ShapeRecognitionEngine);
                InkShapeRecognitionResult result = InkShapeRecognitionResult.Empty;

                if (ShapeRecognitionRouter.ResolveUseWinRt(shapeMode) && Helpers.WinRtInkShapeRecognizer.IsApiAvailable)
                {
                    result = await ModernInkAnalyzer.AnalyzeAsync(ctx.RecentStrokes);
                }
                else
                {
                    var strokeReco = new StrokeCollection();
                    result = await InkRecognizeHelper.RecognizeShapeUnifiedAsync(ctx.RecentStrokes, shapeMode);
                    for (var i = ctx.RecentStrokes.Count - 1; i >= 0; i--)
                    {
                        strokeReco.Add(ctx.RecentStrokes[i]);
                        var newResult = await InkRecognizeHelper.RecognizeShapeUnifiedAsync(strokeReco, shapeMode);
                        if (newResult.IsSuccess &&
                            (newResult.ShapeName == "Circle" || newResult.ShapeName == "Ellipse"))
                        {
                            result = newResult;
                            break;
                        }
                    }
                }

                if (!result.IsSuccess)
                    return;

                // 拟合度门禁：吻合度过低保留原笔画，不强行纠正（涂鸦误判为形状的主修复点）
                if (!PassesShapeFitGate(result, ctx.CanvasSize?.Invoke() ?? default))
                    return;

                if (result.ShapeName == "Circle" &&
                    Settings.InkToShape.IsInkToShapeRounded)
                {
                    if (result.ShapeWidth > 75 * GetResolutionScale())
                    {
                        foreach (var circle in ctx.Circles)
                            //判断是否画同心圆
                            if (Math.Abs(result.Centroid.X - circle.Centroid.X) / result.ShapeWidth < 0.12 &&
                                Math.Abs(result.Centroid.Y - circle.Centroid.Y) / result.ShapeWidth < 0.12)
                            {
                                result.Centroid = circle.Centroid;
                                break;
                            }
                            else
                            {
                                var d = (result.Centroid.X - circle.Centroid.X) *
                                        (result.Centroid.X - circle.Centroid.X) +
                                        (result.Centroid.Y - circle.Centroid.Y) *
                                        (result.Centroid.Y - circle.Centroid.Y);
                                d = Math.Sqrt(d);
                                //判断是否画外切圆
                                var x = result.ShapeWidth / 2.0 + circle.R - d;
                                if (Math.Abs(x) / result.ShapeWidth < 0.1)
                                {
                                    var sinTheta = (result.Centroid.Y - circle.Centroid.Y) / d;
                                    var cosTheta = (result.Centroid.X - circle.Centroid.X) / d;
                                    var newX = result.Centroid.X + x * cosTheta;
                                    var newY = result.Centroid.Y + x * sinTheta;
                                    result.Centroid = new Point(newX, newY);
                                }

                                //判断是否画外切圆
                                x = Math.Abs(circle.R - result.ShapeWidth / 2.0) - d;
                                if (Math.Abs(x) / result.ShapeWidth < 0.1)
                                {
                                    var sinTheta = (result.Centroid.Y - circle.Centroid.Y) / d;
                                    var cosTheta = (result.Centroid.X - circle.Centroid.X) / d;
                                    var newX = result.Centroid.X + x * cosTheta;
                                    var newY = result.Centroid.Y + x * sinTheta;
                                    result.Centroid = new Point(newX, newY);
                                }
                            }

                        var iniP = new Point(result.Centroid.X - result.ShapeWidth / 2,
                            result.Centroid.Y - result.ShapeHeight / 2);
                        var endP = new Point(result.Centroid.X + result.ShapeWidth / 2,
                            result.Centroid.Y + result.ShapeHeight / 2);
                        var pointList = GenerateEllipseGeometry(iniP, endP);
                        var point = new StylusPointCollection(pointList);
                        var stroke = new Stroke(point)
                        {
                            DrawingAttributes = ctx.DefaultAttributes.Clone()
                        };
                        stroke.AddPropertyData(Helpers.ModernInkAnalyzer.ShapeStrokePropertyGuid, true);
                        ctx.Circles.Add(new Circle(result.Centroid, result.ShapeWidth / 2.0, stroke));
                        ctx.Replace(result.StrokesToRemove, new StrokeCollection { stroke }, targetStroke);
                        ctx.RecentStrokes.Clear();
                    }
                }
                else if (result.ShapeName.Contains("Ellipse") &&
                         Settings.InkToShape.IsInkToShapeRounded)
                {
                    var p = result.HotPoints;
                    var a = GetDistance(p[0], p[2]) / 2; //长半轴
                    var b = GetDistance(p[1], p[3]) / 2; //短半轴
                    if (a < b)
                    {
                        var t = a;
                        a = b;
                        b = t;
                    }

                    result.Centroid = new Point((p[0].X + p[2].X) / 2, (p[0].Y + p[2].Y) / 2);
                    var needRotation = true;

                    if (result.ShapeWidth > 75 * GetResolutionScale() || (result.ShapeHeight > 75 * GetResolutionScale() && p.Count == 4))
                    {
                        var iniP = new Point(result.Centroid.X - result.ShapeWidth / 2,
                            result.Centroid.Y - result.ShapeHeight / 2);
                        var endP = new Point(result.Centroid.X + result.ShapeWidth / 2,
                            result.Centroid.Y + result.ShapeHeight / 2);

                        // WinRT 返回的热点顺序/方向不稳定时，用点集反推 IACore 风格椭圆参数（中心/长短轴/方向/四个端点）
                        var hasEllipseParams = TryEstimateEllipseParamsFromStrokes(
                            result.StrokesToRemove,
                            out var ellipseCentroid,
                            out var ellipseA,
                            out var ellipseB,
                            out var ellipseThetaRad,
                            out var ellipseMajor0,
                            out var ellipseMajor1,
                            out var ellipseMinor0,
                            out var ellipseMinor1);

                        foreach (var circle in ctx.Circles)
                            //判断是否画同心椭圆
                            if (Math.Abs(result.Centroid.X - circle.Centroid.X) / a < 0.2 &&
                                Math.Abs(result.Centroid.Y - circle.Centroid.Y) / a < 0.2)
                            {
                                result.Centroid = circle.Centroid;
                                iniP = new Point(result.Centroid.X - result.ShapeWidth / 2,
                                    result.Centroid.Y - result.ShapeHeight / 2);
                                endP = new Point(result.Centroid.X + result.ShapeWidth / 2,
                                    result.Centroid.Y + result.ShapeHeight / 2);

                                //再判断是否与圆相切
                                if (Math.Abs(a - circle.R) / a < 0.2)
                                {
                                    if (result.ShapeWidth >= result.ShapeHeight)
                                    {
                                        iniP.X = result.Centroid.X - circle.R;
                                        endP.X = result.Centroid.X + circle.R;
                                        iniP.Y = result.Centroid.Y - b;
                                        endP.Y = result.Centroid.Y + b;
                                    }
                                    else
                                    {
                                        iniP.Y = result.Centroid.Y - circle.R;
                                        endP.Y = result.Centroid.Y + circle.R;
                                        iniP.X = result.Centroid.X - a;
                                        endP.X = result.Centroid.X + a;
                                    }
                                }

                                break;
                            }
                            else if (Math.Abs(result.Centroid.X - circle.Centroid.X) / a < 0.2)
                            {
                                var sinTheta = Math.Abs(circle.Centroid.Y - result.Centroid.Y) /
                                               circle.R;
                                var cosTheta = Math.Sqrt(1 - sinTheta * sinTheta);
                                var newA = circle.R * cosTheta;
                                if (circle.R * sinTheta / circle.R < 0.9 && a / b > 2 &&
                                    Math.Abs(newA - a) / newA < 0.3)
                                {
                                    iniP.X = circle.Centroid.X - newA;
                                    endP.X = circle.Centroid.X + newA;
                                    iniP.Y = result.Centroid.Y - newA / 5;
                                    endP.Y = result.Centroid.Y + newA / 5;

                                    var topB = endP.Y - iniP.Y;

                                    ctx.RecentStrokes.Clear();

                                    var _pointList = GenerateEllipseGeometry(iniP, endP, false);
                                    var _point = new StylusPointCollection(_pointList);
                                    var _stroke = new Stroke(_point)
                                    {
                                        DrawingAttributes = ctx.DefaultAttributes.Clone()
                                    };
                                    _stroke.AddPropertyData(Helpers.ModernInkAnalyzer.ShapeStrokePropertyGuid, true);
                                    var _dashedLineStroke =
                                        GenerateDashedLineEllipseStrokeCollection(iniP, endP, true, false);
                                    var strokes = new StrokeCollection {
                                        _stroke,
                                        _dashedLineStroke
                                    };
                                    ctx.Replace(result.StrokesToRemove, strokes, targetStroke);
                                    return;
                                }
                            }
                            else if (Math.Abs(result.Centroid.Y - circle.Centroid.Y) / a < 0.2)
                            {
                                var cosTheta = Math.Abs(circle.Centroid.X - result.Centroid.X) /
                                               circle.R;
                                var sinTheta = Math.Sqrt(1 - cosTheta * cosTheta);
                                var newA = circle.R * sinTheta;
                                if (circle.R * sinTheta / circle.R < 0.9 && a / b > 2 &&
                                    Math.Abs(newA - a) / newA < 0.3)
                                {
                                    iniP.X = result.Centroid.X - newA / 5;
                                    endP.X = result.Centroid.X + newA / 5;
                                    iniP.Y = circle.Centroid.Y - newA;
                                    endP.Y = circle.Centroid.Y + newA;
                                    needRotation = false;
                                }
                            }

                        // 用反推参数替换中心与长短轴（比 WinRT 的包围盒更接近 IACore，且不会竖横翻转）
                        if (hasEllipseParams)
                        {
                            result.Centroid = ellipseCentroid;
                            a = ellipseA;
                            b = ellipseB;
                            iniP = new Point(result.Centroid.X - a, result.Centroid.Y - b);
                            endP = new Point(result.Centroid.X + a, result.Centroid.Y + b);
                            // 用端点重写热点，保证后续回退分支也一致
                            p = new PointCollection { ellipseMajor0, ellipseMinor0, ellipseMajor1, ellipseMinor1 };
                        }

                        var pointList = GenerateEllipseGeometry(iniP, endP);
                        var point = new StylusPointCollection(pointList);
                        var stroke = new Stroke(point)
                        {
                            DrawingAttributes = ctx.DefaultAttributes.Clone()
                        };
                        stroke.AddPropertyData(Helpers.ModernInkAnalyzer.ShapeStrokePropertyGuid, true);

                        if (needRotation)
                        {
                            var m = new Matrix();
                            // 优先使用反推参数角度；否则用端点向量角度（使用 Atan2 避免斜率无穷）
                            var theta = hasEllipseParams ? ellipseThetaRad : Math.Atan2(p[2].Y - p[0].Y, p[2].X - p[0].X);
                            m.RotateAt(theta * 180.0 / Math.PI, result.Centroid.X, result.Centroid.Y);
                            stroke.Transform(m, false);
                        }

                        ctx.Replace(result.StrokesToRemove, new StrokeCollection { stroke }, targetStroke);
                        ctx.HideSelectionCover?.Invoke();
                        ctx.RecentStrokes.Clear();
                    }
                }
                else if (result.ShapeName.Contains("Triangle") &&
                         Settings.InkToShape.IsInkToShapeTriangle)
                {
                    var p = result.HotPoints;
                    if ((Math.Max(Math.Max(p[0].X, p[1].X), p[2].X) -
                         Math.Min(Math.Min(p[0].X, p[1].X), p[2].X) >= 100 * GetResolutionScale() ||
                         Math.Max(Math.Max(p[0].Y, p[1].Y), p[2].Y) -
                         Math.Min(Math.Min(p[0].Y, p[1].Y), p[2].Y) >= 100 * GetResolutionScale()) &&
                        result.HotPoints.Count == 3)
                    {
                        //纠正垂直与水平关系
                        var newPoints = FixPointsDirection(p[0], p[1]);
                        p[0] = newPoints[0];
                        p[1] = newPoints[1];
                        newPoints = FixPointsDirection(p[0], p[2]);
                        p[0] = newPoints[0];
                        p[2] = newPoints[1];
                        newPoints = FixPointsDirection(p[1], p[2]);
                        p[1] = newPoints[0];
                        p[2] = newPoints[1];

                        var pointList = p.ToList();
                        //pointList.Add(p[0]);
                        var point = new StylusPointCollection(pointList);
                        var stroke = new Stroke(GenerateFakePressureTriangle(point))
                        {
                            DrawingAttributes = ctx.DefaultAttributes.Clone()
                        };
                        stroke.AddPropertyData(Helpers.ModernInkAnalyzer.ShapeStrokePropertyGuid, true);
                        ctx.Replace(result.StrokesToRemove, new StrokeCollection { stroke }, targetStroke);
                        ctx.HideSelectionCover?.Invoke();
                        ctx.RecentStrokes.Clear();
                    }
                }
                else if ((result.ShapeName.Contains("Rectangle") ||
                          result.ShapeName.Contains("Diamond") ||
                          result.ShapeName.Contains("Parallelogram") ||
                          result.ShapeName.Contains("Square") ||
                          result.ShapeName.Contains("Trapezoid") ||
                          result.ShapeName.Contains("Quadrilateral")) &&
                         Settings.InkToShape.IsInkToShapeRectangle)
                {
                    var p = result.HotPoints;
                    if ((Math.Max(Math.Max(Math.Max(p[0].X, p[1].X), p[2].X), p[3].X) -
                         Math.Min(Math.Min(Math.Min(p[0].X, p[1].X), p[2].X), p[3].X) >= 100 * GetResolutionScale() ||
                         Math.Max(Math.Max(Math.Max(p[0].Y, p[1].Y), p[2].Y), p[3].Y) -
                         Math.Min(Math.Min(Math.Min(p[0].Y, p[1].Y), p[2].Y), p[3].Y) >= 100 * GetResolutionScale()) &&
                        result.HotPoints.Count == 4)
                    {
                        //纠正垂直与水平关系
                        var newPoints = FixPointsDirection(p[0], p[1]);
                        p[0] = newPoints[0];
                        p[1] = newPoints[1];
                        newPoints = FixPointsDirection(p[1], p[2]);
                        p[1] = newPoints[0];
                        p[2] = newPoints[1];
                        newPoints = FixPointsDirection(p[2], p[3]);
                        p[2] = newPoints[0];
                        p[3] = newPoints[1];
                        newPoints = FixPointsDirection(p[3], p[0]);
                        p[3] = newPoints[0];
                        p[0] = newPoints[1];

                        var pointList = p.ToList();
                        pointList.Add(p[0]);
                        var point = new StylusPointCollection(pointList);
                        var stroke = new Stroke(GenerateFakePressureRectangle(point))
                        {
                            DrawingAttributes = ctx.DefaultAttributes.Clone()
                        };
                        stroke.AddPropertyData(Helpers.ModernInkAnalyzer.ShapeStrokePropertyGuid, true);
                        ctx.Replace(result.StrokesToRemove, new StrokeCollection { stroke }, targetStroke);
                        ctx.HideSelectionCover?.Invoke();
                        ctx.RecentStrokes.Clear();
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            finally
            {
                InkToShapeSerial.Release();
            }
        }

        /// <summary>笔迹尾部「按 InkStyle 合成压感」的处理结果。</summary>
        private enum InkStylePressureOutcome
        {
            /// <summary>未处理（点数不满足或笔类型不匹配）。</summary>
            NotApplied = 0,

            /// <summary>已写入合成压感。</summary>
            Applied = 1,

            /// <summary>应终止后续尾部处理（与原实现 <c>return e.Stroke</c> 同义）。</summary>
            AbortTail = 2
        }

        /// <summary>
        /// 无压感设备（触摸/鼠标，PressureFactor 恒为 0.5）按 <see cref="Settings.Canvas.InkStyle"/>
        /// 合成压感曲线的 switch 主体。
        /// <para>
        /// 原实现有两处调用：收笔主路径（受 <c>EnablePressureTouchMode</c> 门控）与收笔尾部
        /// （不受该开关门控，只要求「压感均匀」）。此处抽出主体供两处及独立画布共用，
        /// 各调用方自行保留原有的前置判定与中止语义。
        /// </para>
        /// </summary>
        private InkStylePressureOutcome TryApplyInkStyleTouchPressure(Stroke stroke)
        {
            if (stroke?.StylusPoints == null || stroke.StylusPoints.Count == 0)
                return InkStylePressureOutcome.NotApplied;

            switch (Settings.Canvas.InkStyle)
            {
                case 1:
                    if (penType == 0)
                        try
                        {
                            var stylusPoints = new StylusPointCollection();
                            var n = stroke.StylusPoints.Count - 1;

                            for (var i = 0; i <= n; i++)
                            {
                                var speed = GetPointSpeed(stroke.StylusPoints[Math.Max(i - 1, 0)].ToPoint(),
                                    stroke.StylusPoints[i].ToPoint(),
                                    stroke.StylusPoints[Math.Min(i + 1, n)].ToPoint());
                                var point = new StylusPoint
                                {
                                    PressureFactor = RateBasedPressureFactorFromPointSpeed(speed),
                                    X = stroke.StylusPoints[i].X,
                                    Y = stroke.StylusPoints[i].Y
                                };
                                stylusPoints.Add(point);
                            }

                            stroke.StylusPoints = stylusPoints;
                            return InkStylePressureOutcome.Applied;
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

                    break;
                case 0:
                    if (penType == 0)
                        try
                        {
                            var stylusPoints = new StylusPointCollection();
                            var n = stroke.StylusPoints.Count - 1;
                            var pressure = 0.1;
                            var x = 10;
                            if (n == 1) return InkStylePressureOutcome.AbortTail;
                            if (n >= x)
                            {
                                for (var i = 0; i < n - x; i++)
                                {
                                    var point = new StylusPoint();

                                    point.PressureFactor = (float)0.5;
                                    point.X = stroke.StylusPoints[i].X;
                                    point.Y = stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }

                                for (var i = n - x; i <= n; i++)
                                {
                                    var point = new StylusPoint();

                                    point.PressureFactor = (float)((0.5 - pressure) * (n - i) / x + pressure);
                                    point.X = stroke.StylusPoints[i].X;
                                    point.Y = stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                            }
                            else
                            {
                                for (var i = 0; i <= n; i++)
                                {
                                    var point = new StylusPoint();

                                    point.PressureFactor = (float)(0.4 * (n - i) / n + pressure);
                                    point.X = stroke.StylusPoints[i].X;
                                    point.Y = stroke.StylusPoints[i].Y;
                                    stylusPoints.Add(point);
                                }
                            }

                            stroke.StylusPoints = stylusPoints;
                            return InkStylePressureOutcome.Applied;
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

                    break;
                case 3:
                    break;
            }

            return InkStylePressureOutcome.NotApplied;
        }
    }
}
