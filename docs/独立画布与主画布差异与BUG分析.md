# 独立画布（IndependentCanvas）与主画布（inkCanvas）差异与 BUG 分析

> ⚠️ **本文档的 §0 一句话结论、§4 能力对照总表、§5 修复优先级建议、§8 修复进度已过期。**
> 后续实现已经远远超出这些章节的描述（多项当时标为「未接」的能力已经接通）。
> **最新状态见文末 §9「当前状态（权威）」**，其余章节保留为审计历史，请勿据此判断代码能力。
>
> 分析对象：`Ink Canvas` 主程序，分支 `net10`。
> 结论 = 逐行源码阅读 + 3 路独立子代理交叉审计 + 一个 WPF 行为探针程序实测 + 真实 `dotnet build` 校验。

---

## 0. 一句话结论（⚠️ 已过期，见 §9）

**独立画布不是「主画布的第二个实例」，而是一块裸 WPF `InkCanvas`。**
主画布上几乎所有行为都是**宿主代码挂在 `inkCanvas` 事件上**实现的，而非 WPF 控件自带能力。
凡属「宿主挂上去的东西」，独立画布**一个都没有**。

```csharp
// Ink Canvas\Controls\IndependentCanvas.cs:81-85 —— 独立画布的全部墨迹初始化
_inkCanvas = new InkCanvas
{
    Background = Brushes.Transparent,
    ClipToBounds = true
};
```

对照主画布（`MainWindow.xaml.cs:756-812`）：

```csharp
inkCanvas.Strokes.StrokesChanged += StrokesOnStrokesChanged;    // 撤销/冻结/插件事件中枢
_inkSmoothingManager = new InkSmoothingManager(Dispatcher);
_inkFadeManager = new InkFadeManager(this);                      // 激光笔/渐隐
inkCanvas.PreviewMouseDown += ...; inkCanvas.StylusDown += ...;  // 自定义输入管线
```

独立画布的全部墨迹逻辑只有三处：`IndependentInkHistory`（`IndependentCanvas.cs:811-895`，200 步轻量撤销）、
`ApplyShift`（`:730-739`，滚动时 `stroke.Transform`）、`ClearVisibleRegionStrokes`（`:786-797`）。
**没有** `StrokeCollected`、`StylusDown`、`TouchDown`、`DefaultDrawingAttributes` 初始化、`EditingMode` 管理。

> **这段结论已被推翻。** 现在独立画布接有 `StrokeCollected` 后处理管线
> （`MW_IndependentInkPipeline.cs`）、触摸抑制、手掌擦、`DrawingAttributes` 同步、
> `EditingMode` 管理，且 `IndependentInkHistory` 同时记录集合增删与框选几何变更。详见 §9。

---

## 1. 关键坐标不变式（后面多个 BUG 的共同根因）

独立画布的笔迹**数据坐标不在视口坐标系里，而是被累计滚动偏移 `S = AppliedScrollOffset` 平移过**：

```csharp
// IndependentCanvas.cs:593-605
double deltaX = offset.X - _scrollX;
double deltaY = offset.Y - _scrollY;
_scrollX = offset.X;                       // S 累积的是目标文档滚动偏移（向下为正）
_scrollY = offset.Y;
...
var shift = new Vector(-deltaX, -deltaY);  // 文档向下滚 → 笔迹向上平移
ApplyShift(shift);                         // 整集合 stroke.Transform
```

```csharp
// IndependentCanvas.cs:730-738
private void ApplyShift(Vector shift)
{
    if (shift.X == 0 && shift.Y == 0) return;
    if (_inkCanvas.Strokes.Count == 0) return;
    var matrix = new Matrix(1, 0, 0, 1, shift.X, shift.Y);
    foreach (Stroke stroke in _inkCanvas.Strokes)
        stroke.Transform(matrix, false);
}
```

即：滚轮把文档往下滚了 `S` 之后，整个笔迹集合被 `Transform(-S)`。
因此 **`数据坐标 d  ↔  视口坐标 p` 的换算是 `d = p − S`，不是 `d = p + S`**。
原生 `InkCanvas` 直接落笔时拿到的是视口坐标 `p`，写进去就是 `p`；而任何**从外部点反算**的代码必须减 `S`。

> 该结论由本节代码推导 + 与 `SaveUILinkageInk` 的注释互相印证（`MW_UILinkage.cs:2716-2723`：
> 「平移 **+当前偏移**，使保存数据以文档内容左上角为原点」，即内容原点 `C = d + S`）。

**§2.1 / §2.2 / §2.3 是同一个根因的三个表现。**

---

## 2. BUG 清单（按严重度排序）

### P0（正确性 / 崩溃级）

---

#### 2.1 首笔迁移落点偏移一个滚动距离

```csharp
// Ink Canvas\MainWindow_cs\MW_UILinkage.cs:1239-1240、1284-1291
var margin = inst.Canvas.Margin;
var toLocalMatrix = new Matrix(1, 0, 0, 1, -margin.Left, -margin.Top);
...
var screenPt = inkCanvas.PointToScreen(target.StylusPoints[0].ToPoint());
if (!inst.ScreenRect.Contains(screenPt)) continue;

target.Transform(toLocalMatrix, false);   // ← 只减了 Margin，没减 S
inkCanvas.Strokes.Remove(target);
inst.Canvas.Strokes.Add(target);
```

`toLocalMatrix` 只把主画布坐标平移到独立画布的**视口**坐标，但独立画布的笔迹数据空间已经整体偏移了 `-S`。
所以这一笔会渲染在 `视口位置 − S` —— **视觉上凭空跳动一个文档滚动距离**。

**触发**：联动已绑定 → 把 Word 滚到文档中部（`S ≠ 0`）→ 写第一笔。文档在最顶部（`S = 0`）时才看不出来。

**正解**：`d = toLocal(p) − S`，即再叠加 `new Matrix(1,0,0,1,-S.X,-S.Y)`。

> 注：子代理 C 的审计把符号写成了 `+ S`，方向错误；本节已按 §1 推导更正。

---

#### 2.2 橡皮擦在独立画布上擦错位置（同一根因）

```csharp
// MW_UILinkage.cs:1355-1357
var margin = instance.Canvas.Margin;
localPoint = new Point(inkCanvasPoint.X - margin.Left, inkCanvasPoint.Y - margin.Top);
```
```csharp
// MW_Eraser.cs:269-277
uiLinkageHitTester = resolved.Canvas.Strokes.GetIncrementalStrokeHitTester(_uiLinkageEraserShape);
...
uiLinkageHitTester.AddPoint(uiLocal);   // ← 这里的命中测试器作用在 d 空间，uiLocal 却是 p 空间
```

增量命中测试器建立在 `d` 空间的笔迹上，喂进去的却是 `p` 空间的点，
于是橡皮擦的落点整体偏差 `S`：**光标处擦不掉，却在别处擦掉笔迹**。

**触发**：Word 已滚动 → 用面积擦/线擦在独立画布墨迹上擦。`S` 大时表现为「擦不动」。

同源问题：2 参重载 `TryGetUILinkageLocalPoint(Point, out Point)`（`:1345-1346`）**0 个调用点**，
说明这条换算路径在写完后就没人用过，符号错误未被发现。

---

#### 2.3 同窗口切换文档后，撤销会把旧文档的笔迹画到新文档上

```csharp
// MW_UILinkage.cs:1208-1211（绑定/新建路径：正确）
LoadUILinkageInk(inst);
inst.Canvas.ResetInkHistory();     // ← 有
inst.Canvas.ShowFollowNotice();
```
```csharp
// MW_UILinkage.cs:1216-1227（同窗口换文档路径：漏了）
SaveUILinkageScrollPosition(inst);
SaveUILinkageInk(inst);
inst.Canvas.Strokes.Clear();
inst.DocName = docName;
inst.Canvas.ResetScrollFrame();
if (kind == UILinkageTargetKind.SumatraPdf)
    CalibrateUILinkageSumatraScroll(inst, r);
LoadUILinkageInk(inst);
RestoreUILinkageScrollPosition(inst);   // ← 没有 ResetInkHistory()
```

`Strokes.Clear()` 会让 `IndependentInkHistory` 把「移除」记进撤销栈，
但历史没有被清空。**之后点独立画布的「撤销」，会把文档 A 的笔迹重新 `Add` 回文档 B 的画布。**
同样的遗漏也存在于轮询兜底的换文档分支（`:1855-1872`）。

**触发**：同一个 Word 窗口里从 A.docx 换到 B.docx（或改标题触发轮询兜底）→ 在 B 上写字 → 点画布右下角 Undo。

---

#### 2.4 主画布 Ctrl+Z 会把已迁移的笔迹「复活」到主画布，造成一笔两画布

```csharp
// MW_UILinkage.cs:1287-1291
target.Transform(toLocalMatrix, false);
// 主画布 StrokesChanged 会自动提交「擦除」历史，主画布撤销可还原该笔。
inkCanvas.Strokes.Remove(target);
inst.Canvas.Strokes.Add(target);
```

迁移对主画布而言是一次「删除」，于是 `TimeMachine` 里留下一条「恢复该笔」的记录。
在独立画布 Undo **之前**于主画布按 Ctrl+Z：`ApplyHistoryToCanvas` 会把**同一个 `Stroke` 对象**重新 `Add` 回
`inkCanvas.Strokes` —— 而它此时仍在 `inst.Canvas.Strokes` 里。

**探针实测（T3）证明 WPF 允许同一 `Stroke` 同时属于两个 `StrokeCollection`**（不抛异常）。
后果：一笔被渲染两次、两个撤销栈各自独立地增删同一对象，任一侧的操作都会让另一侧状态失真。

> 只有「同一集合重复 Add」会抛 `ArgumentException`（探针 T3）。所以这个坑不会崩，只会静默错乱。

---

#### 2.5 清空画布 / 保存 / 撤销 全线漏掉独立画布（6 个集成点之外全部未接）

只有工具栏删除按钮做了双向处理：

```csharp
// MW_FloatingBarIcons.cs:1237-1239
foreach (var inst in GetVisibleBoundUILinkageInstances())
    inst.Canvas.ClearVisibleRegionStrokes();
```

而公共清屏入口 `PerformCanvasClear()`（`MW_FloatingBarIcons.cs:4639-4669`）**只清主画布**：

```csharp
ClearStrokes(false);        // MW_BoardControls.cs:196-210 → inkCanvas.Strokes.Clear()
inkCanvas.Children.Clear();
```

**实测证据（0 个调用点）**——说明这些接口「设计了但从未接上」：

| 接口 | 位置 | 调用点数 |
|---|---|---|
| `IsUILinkageCanvasBound` | `MW_UILinkage.cs:2792` | **0** |
| `GetUILinkageStrokes()` | `MW_UILinkage.cs:2798-2799` | **0** |
| `TryGetUILinkageLocalPoint(Point, out Point)` | `MW_UILinkage.cs:1345-1346` | **0** |
| `IndependentCanvas.IsScrollBarsSuppressed` | `IndependentCanvas.cs:504` | **0（外部）** |
| `IndependentCanvas.ViewportSize` | `IndependentCanvas.cs:651` | **0（外部）** |

漏掉独立画布的入口（全部只动 `inkCanvas`）：

| 功能 | 位置 | 后果 |
|---|---|---|
| 玻璃浮动栏清屏 | `MW_LiquidGlassBar.cs:207-208` | 主画布清空，Word 批注残留 |
| 迷你栏清屏 | `MW_IdleMiniBar.cs:437` | 同上 |
| 长按撤销清屏 | `MW_UndoLongPressClear.cs:145` | 同上 |
| 自动化 `inkcanvas.clearstrokes` | `Automation\Actions\ClearStrokesAction.cs` | 协议清不掉 |
| 插件 `TryClearStrokes` | `MW_CanvasInk.cs:115-141` | 插件清不掉 |
| 另存为图片 / 插件 PNG 导出 | `MW_Save&OpenStrokes.cs:1178-1227`、`1266-1293` | `VisualBrush(inkCanvas)` 不含独立画布墨迹 |
| 保存批注 `.icstk`/XML/ZIP | `MW_Save&OpenStrokes.cs:826/1593/1733/1902/1958` | 用户存档丢失独立画布墨迹 |
| UInk 导出/导入 | `MW_UInk.cs:57/75/91/280-336` | 跨软件格式静默丢弃 |
| **周期性自动保存** | `MW_Timer.cs:328-337` | 崩溃/被杀进程 → 上次解绑之后的批注全丢 |
| 区域截图 / 截图插图 | `MW_ImageInsert.cs:71-72/346-403`、`MW_Screenshot.cs:246-304` | 窗口先 `Hidden` 再 `VisualBrush(inkCanvas)` 合成，独立画布墨迹**完全丢失**；`CreateInkOverlayPreviewBitmapSource` 在「只有独立画布墨迹」时直接 `return null`（`MW_ImageInsert.cs:355-358`） |
| 墨迹重播 | `MW_FloatingBarIcons.cs:1652/1670` | 只克隆主画布；同时把父容器设为 `Hidden`（顺带隐藏独立画布） |
| 页面缩略图 | `MW_PageListView.cs:115-116`、`MW_BoardToolbarHost.cs:712-713` | 缩略图无独立画布墨迹 |
| 页面保存/切页/删页 | `MW_BoardControls.cs:87-185/204/263` | 独立画布墨迹不随页；切到白板页后仍浮在白板之上且可写 |
| 选择/克隆/删除/翻转/旋转 | `MW_SelectionGestures.cs:71/265/302/336/543-574` | 只读 `inkCanvas.GetSelectedStrokes()` |
| 冻结记账 | `MW_InkFreeze.cs:91`、`MW_TimeMachine.cs:741-742` | 独立画布书写**不刷新** `pageLastUserInkMutationUtc` → **延迟自动冻结可能在用户正在写字时触发** |
| 边缘扩展平移 | `MW_EdgeExpandHint.cs:562/584` | 主画布整体平移时独立画布墨迹不动 |
| 插件指针会话 | `MW_PluginCanvasExtensions.cs:274-283/327` | 插件会收到来自独立画布的指针事件并当成白板输入 |
| 空闲栏 / 自动折叠启发式 | `MW_IdleMiniBar.cs:116-128`、`MW_AutoFold.cs:134` | 只看 `inkCanvas.Strokes.Count` |

---

#### 2.6 荧光笔 / 压感属性在独立画布上失效

```csharp
// MW_UILinkage.cs:1403-1409
var attrs = inkCanvas.DefaultDrawingAttributes;
if (attrs != null)
{
    var cur = inst.Canvas.InkSurface.DefaultDrawingAttributes;
    if (cur == null || cur.Color != attrs.Color || cur.Width != attrs.Width || cur.Height != attrs.Height)
        inst.Canvas.InkSurface.DefaultDrawingAttributes = attrs.Clone();
}
```

荧光笔是这样设置的（`MW_FloatingBarIcons.cs:3780-3789`）：

```csharp
drawingAttributes.IsHighlighter = !Settings.Canvas.HighlighterOverlapEnabled;
drawingAttributes.StylusTip = StylusTip.Rectangle;
drawingAttributes.Width  = Settings.Canvas.HighlighterWidth / 2;
drawingAttributes.Height = Settings.Canvas.HighlighterWidth;
```

**探针实测（T4）** WPF `DrawingAttributes` 默认值：`StylusTip = Ellipse`、`IsHighlighter = False`、`IgnorePressure = False`。

因为白名单只比对 `Color/Width/Height`，独立画布的 `IsHighlighter` / `StylusTip` **永远停在默认值**：
选「荧光笔」在 Word 上划重点 → 画出**不透明椭圆硬笔**，没有半透明叠加。
`IgnorePressure`（`MW_SettingsToLoad.cs:441`）、`FitToCurve`、`StylusTipTransform` 同样不同步。

> `attrs.Clone()` 本来就在代码里，只是被那 3 个属性判断挡住了。

---

#### 2.7 触摸输入在独立画布上无任何抑制 → 手指直接落墨

主画布有完整的一层宿主拦截（`MW_TouchEvents.cs`）：`InkCanvas_PreviewTouchDown`（`:1901`，拦截 WPF 的触摸→触笔提升）、
手掌擦（`:2071-2084`，`EnablePalmEraser` 默认 `true`）、双指手势时 `EditingMode = None`（`:1924-1935`）、
`IsManipulationEnabled = true`（`MainWindow.xaml:236`）。

独立画布：裸 `InkCanvas`，`IsManipulationEnabled` 默认 `false`（探针 T6），构造函数未订阅任何 `Touch*`/`Stylus*`，
`SyncUILinkageCanvasState` 只改 `EditingMode`/`DefaultDrawingAttributes`/`EraserShape`/`IsHitTestVisible`。

WPF 默认会把触摸**提升为触笔事件**（本项目自己的注释印证：`MW_BoardRoaming.cs:31`「WispLogic.PromoteMainToOther：Stylus Handled 即跳过提升」）。
→ **在 Word 视口上用手指触摸，独立画布直接产生笔迹**，既不触发手势也不被手掌擦抑制。

---

#### 2.8 整个笔迹后处理管线缺失（架构级）

`ProcessCommittedStroke`（`MW_SimulatePressure&InkToShape.cs:372-1079`）的 15 个阶段全部挂在**主画布** `StrokeCollected` 上：

| 设置 | 主画布 | 独立画布 |
|---|---|---|
| 删除线（激光笔）/ 墨迹渐隐 | 笔迹渐隐消失 | **永久保留** |
| 压感模拟 / 屏蔽压感 | 生效 | 失效 |
| 自动拉直直线 + 端点吸附 | 生效 | 失效 |
| 形状识别（圆/矩形族等） | 生效（延迟+串行化） | 失效 |
| 高级贝塞尔平滑 | 生效 | 失效 |
| 手写体美化 | 生效（防抖批处理） | 失效 |
| 笔锋（InkStyle 3） | 生效（`StrokeVisual` 手工收集） | 失效 |
| 边缘扩展提示 / 批注点提示 | 生效 | 失效 |

**激光笔的失效方式最危险** —— 主画布走的是

```csharp
// MW_SimulatePressure&InkToShape.cs:444 / 460-463 / 471
_inkFadeManager.AddFadingStroke(e.Stroke, startPoint, endPoint, strokeDurationMs);
...
if (inkCanvas.Strokes.Contains(e.Stroke)) { inkCanvas.Strokes.Remove(e.Stroke); }
return;   // 管线在此终止
```

即「渐隐笔迹根本不进 `InkCanvas.Strokes`」。独立画布没有这条分支，
`penType == 2`（激光笔）写下的笔迹会变成**普通永久墨迹**，与用户预期完全相反。

---

#### 2.9 轮询 Tick 没有 try/catch，异常直达全局崩溃处理器

```csharp
// MW_UILinkage.cs:248-254
_uiLinkageTimer.Tick += (_, _) =>
{
    UpdateUILinkageForegroundWatch();   // ← 新增（未提交改写）
    SyncUILinkageCanvasState();
    UpdateUILinkageDebugUi();
    UpdateUILinkageScrollSync();
    UpdateUILinkageCanvasGeometry();
};
```

链路里未加保护的调用包括 `ApplyUILinkageScrollPercent` → `sp.SetScrollPercent(...)`（`:2067-2068`，跨进程 COM）、
`VisualTreeHelper.GetDpi(this)`、`PointFromScreen`、`SetViewportSize`/`SetClip`。

本项目对 `Application.DispatcherUnhandledException` 的处理是**错误弹窗 + 崩溃日志 + 按设置静默重启**（`App.xaml.cs:1018-1047`）。
→ Word 恰好在读取时退出，就可能把一次 UIA 读取失败升级成一次「崩溃」。

---

#### 2.10 SumatraPDF 滚动闭环在 UI 线程上最多阻塞数秒

```csharp
// Helpers\UILinkageScrollReader.cs:414-468（TrySetSumatraScrollPercent → ScrollToPos）
const int MaxSteps = 192;
const int SettleMs = 25;
...
if (after == pos) System.Threading.Thread.Sleep(SettleMs);
```

每次 `SendMessageTimeout(..., 200ms, SMTO_ABORTIFHUNG)`。
调用方 `ApplyUILinkageScrollPercent`（`MW_UILinkage.cs:2047-2062`）由 `OnUILinkageCanvasUserScroll` / `OnUILinkageCanvasScrollStep`
在 **UI 线程** 调用（滚动条 `Scroll` 事件、辅助按钮 Click）。
→ 拖一次独立画布滚动条 = 最多 192 次阻塞发送 + 多次 `Sleep`，**整个应用（含书写）卡死数秒**。
代码注释把这件事类比成「Word 的 `ScrollPattern` 阻塞特性」，但 Word 只是**一次**跨进程调用。

---

### P1（功能/交互缺陷）

---

#### 2.11 独立画布的「清空」只清当前视口，滚动后旧批注会「复活」

```csharp
// IndependentCanvas.cs:786-797
var viewport = new Rect(0, 0, Math.Max(1, w), Math.Max(1, h));
var toRemove = new StrokeCollection(_inkCanvas.Strokes.Where(s => s.GetBounds().IntersectsWith(viewport)));
```

因滚动靠 `Transform` 把笔迹保持在视口坐标，视口外的笔迹不在交集中。
**点「清空」后滚动 Word，之前写过的批注会重新出现**；这一批墨迹只存在于内存的 `IndependentInkHistory` 里。

#### 2.12 `Ctrl+Z` / 全局撤销无法作用于独立画布，且会误删主画布内容

```csharp
// Helpers\GlobalHotkeyManager.cs:360-361
RegisterHotkey("Undo", Key.Z, ModifierKeys.Control, () => _mainWindow.SymbolIconUndo_MouseUp(null, null));
RegisterHotkey("Redo", Key.Y, ModifierKeys.Control, () => _mainWindow.SymbolIconRedo_MouseUp(null, null));
```

`SymbolIconUndo_MouseUp`（`MW_FloatingBarIcons.cs:896-902`）→ `BtnUndo_Click` → `TimeMachine`，只回放主画布。
独立画布的 200 步历史只能通过画布右下角两个 40×40 按钮触发。
→ 用户在 Word 批注区写错后习惯性按 Ctrl+Z，**撤销的是白板内容**。

#### 2.13 独立画布上的框选「移动笔迹」不被任何撤销栈记录

`GetUILinkageEffectiveEditingMode` 允许 `select` → `EditingMode.Select`（`MW_UILinkage.cs:1378-1379`）。
独立画布的原生选择会直接改笔迹坐标，而 `IndependentInkHistory` **只监听 `Strokes.StrokesChanged`**（`IndependentCanvas.cs:827`）。
移动笔迹既不进独立画布历史，也不进 `TimeMachine` → **不可撤销**。

#### 2.14 WinRT 原生湿墨迹开启时，Word 上的笔会被降级成「鼠标路径」

WinRT 输入门把「命中元素不在 `inkCanvas` 子树内」判定为 UI chrome：

```csharp
// MW_WinRTInk.cs:1193-1196
if (hit != null && !IsUnderElement(hit, inkCanvas))
    return CanvasHitZone.UiChrome;         // → BlockAndForward
// MW_WinRTInk.cs:1312-1316
if (zone == CanvasHitZone.CanvasSurface) return ...AllowInk;
return Ink.WinRT.PointerGateResult.BlockAndForward;
```

`IndependentCanvas` 位于 `inkCanvas` **之外**（同父容器），因此被判为 chrome →
`ChromeInputForwarder.ForwardDown` 合成 `WM_LBUTTONDOWN/MOUSEMOVE/LBUTTONUP`（`Ink\WinRT\ChromeInputForwarder.cs:47-79`）再转发。
→ 打开 `UseWinRTInk` 后，独立画布上的书写走鼠标合成路径，**没有压感/倾斜**；独立画布自己的滚动条与按钮也被归为 chrome。
`WetInkOverlayWindow(hwnd, IsCanvasPoint)`（`MW_WinRTInk.cs:156`）用的同一分类器，屏幕区域判定同样受影响。

#### 2.15 文档名相同 → 墨迹与滚动状态互相串写

```csharp
// MW_UILinkage.cs:2696-2700 / 2025-2028
Path.Combine(App.RootPath, "UILinkageInk", SanitizeFileName(docName) + ".isf");
Path.Combine(App.RootPath, "UILinkageInk", SanitizeFileName(docName) + ".state.json");
```

文档名由标题**去后缀**猜出（`:2664-2687`，后缀表只有 `" - Word"`, `" - Microsoft Word"`, `"- Word"`, `" - SumatraPDF"`, `" - Sumatra PDF"`）：

1. **同名文件**（两个窗口都开 `报告.docx`）共用同一份 `.isf`/`.state.json` → 串写；
2. 标题带 `[兼容模式]`/`[只读]`/`- 只读` 等标记时后缀表匹配不到 → 同一文档每次得到不同 key（记忆失效）；
3. 第三方 Office 标签页插件下多文档共用一个窗口标题 → 全部映射到同一 key；
4. `SanitizeFileName`（`:2689-2694`）把非法字符统一替换成 `_` → `a:b.docx` 与 `a?b.docx` 碰撞。

#### 2.16 解绑时默认触发长截屏：关开关 = 截屏上传

```csharp
// MW_UILinkage.cs:1010-1014
private void UnbindUILinkageCanvasesOfKind(UILinkageTargetKind kind, bool captureLongScreenshot = true)
```

该默认值在 `UpdateUILinkageScrollSync`（`:1433-1434`）被使用 —— **用户在设置页把 Word 联动关掉**时，
会走 `StartUILinkageLongScreenshotCapture`：滚动 Word、逐屏截图、拼接、并 `UploadHelper.UploadFileAsync`（`:2646-2658`）。
只有窗口关闭路径显式传了 `false`（`MainWindow.xaml.cs:1975`）。设置页（`UILinkagePage.xaml.cs:44-66`）只写设置，
靠 250ms 轮询间接触发，用户完全不会预期。

#### 2.17 解绑后实例残留在 `_uiLinkageInstances` 中，滚动循环不检查 `Bound`

```csharp
// MW_UILinkage.cs:1254-1258 —— 为长截屏保留画布，提前 return
if (captureLongScreenshot && StartUILinkageLongScreenshotCapture(inst))
    return;
```
```csharp
// MW_UILinkage.cs:1489-1504 —— 滚动循环只检查 Canvas/存活/最小化/截图标志，唯一没有 Bound 判定
foreach (var inst in _uiLinkageInstances.ToList())
{
    if (inst?.Canvas == null) continue;
    if (!UILinkageWindowFinder.IsWindowAlive(inst.BoundHwnd))
```

其他所有消费点都过滤了 `Bound`（`AnyUILinkageCanvasContains` `:1355`、`TryGetUILinkageInstanceAt` `:1372`、
`GetVisibleBoundUILinkageInstances` `:1385`、几何循环 `:1834`）。
→ 截图进行期间，已解绑实例仍被 250ms 轮询驱动（含 `SetScrollInfo` 与 `CompositionTarget.Rendering` 动画）。

#### 2.18 无边框截图模式永不恢复顶部提示条

```csharp
// IndependentCanvas.cs:657-674
public void BeginChromelessCapture()
{
    ...
    _noticeBar.Visibility = Visibility.Collapsed;   // 隐藏
}
public void EndChromelessCapture()
{
    ...
    _contentHost.BorderBrush = _borderBrushBackup;  // 只还原边框
    UpdateOverlayVisibility();                      // 不还原 _noticeBar
}
```

`Begin` 显式备份/还原了边框画刷，却没有备份/还原提示条与 `_noticeTimer`。
→ 走过一次长截屏后，该实例的「当前区域支持笔迹随页面移动」提示**整个会话都不再出现**。

#### 2.19 长截屏期间几何仍在被改写

`UpdateUILinkageCanvasGeometry`（`:1830-1876`）没有 `IsCapturingLongScreenshot` 守卫，仍在每拍执行
`SetViewportSize(w, h)`（`:1879-1887` 的 `UpdateUILinkageInstanceGeometry`）与 `SetClip`。
而截图拼接的数学依赖快照下来的 `View`/`Ext`/`DocRect` 保持稳定。
→ 若滚动过程中 Word 的 Document 矩形变化（例如滚动条出现/消失改变客户区），**切片会错位/出现接缝**。
对比：滚动同步循环明确检查了该标志（`:1456`）—— 两条循环的守卫不对称。

#### 2.20 `IsCapturingLongScreenshot` 可能永久卡在 true

```csharp
// MW_UILinkage.cs:2248-2249
inst.IsCapturingLongScreenshot = true;
inst.Canvas.BeginChromelessCapture();
...
_ = Task.Run(() => RunUILinkageLongScreenshotCapture(snapshot));
```

标志先于 `Task.Run` 置位；若任务未能被调度（线程池/调度器关闭）或 `snapshot` 初始化抛异常且
`inst.Canvas` 并发变 null 导致 catch 里的 `inst.Canvas?.EndChromelessCapture()` 无法复位，
则该标志卡住 → 永久禁用该实例的滚动同步（`:1456`、`:2216`），并使画布停留在无边框模式
（`BeginChromelessCapture` 已置 `_inChromelessCapture`，`if (_inChromelessCapture) return;` 使二次恢复无门）。

#### 2.21 折叠浮动栏：先清空独立画布，紧接着的长截屏必然放弃

```csharp
// MW_FloatingBarIcons.cs:4163-4164
SymbolIconDelete_MouseUp(sender, null);   // 内部：ClearVisibleRegionStrokes()（:1237-1239）
CursorIcon_Click(null, null);             // 内部：CaptureAllUILinkageLongScreenshots()（:3533）
```
```csharp
// MW_UILinkage.cs:2172
if (inst.Canvas.Strokes.Count == 0) return false;   // 刚被清空 → 必然放弃
```

`SymbolIconDelete_MouseUp` 先清掉独立画布可见区墨迹，`CursorIcon_Click` 再尝试长截屏，此时必然 `return false`；
随后解绑把**清空后**的状态写入 `.isf`（`:1205`）→ 被清掉的批注**跨会话不可恢复**。
`MW_AutoFold.cs:137` 会自动走到这条路径。同理，`MW_InkFreeze.cs:183` 的冻结也会走 `CursorIcon_Click`，
触发一次用户没要求的独立画布长截屏。

---

### P2（状态机 / 精度 / 边界）

| # | 问题 | 位置 | 说明 |
|---|---|---|---|
| 2.22 | `ResetScrollFrame` 竖直条拿到**水平**视口宽，且 extent 硬编码 1 | `IndependentCanvas.cs:638-645` | `ApplyToScrollBar(_verticalScrollBar, 0, _viewportW, 1)` → 竖直条 `ViewportSize` 错（应为 `_viewportH`）、`Maximum = max(0, 1 − _viewportW) = 0`。250ms 后被 `SetScrollInfo` 覆盖（短时），但目标最小化/Word 卡住时会一直错 |
| 2.23 | 保存/加载坐标系不对称 | `MW_UILinkage.cs:2719-2723` vs `:2756-2763` | 保存时烘焙 `+AppliedScrollOffset`（内容锚定），加载时直接 `Add` 依赖首轮 `SetScrollInfo` 增量搬回视口。若加载后首个 `SetScrollInfo` 的 offset 恰为 0，或恢复滚动失败（后台重试 8 次后放弃，`:2088-2121`），历史墨迹会整体错位；缩放补偿 `ConsumeUILinkageSavedZoom`（`:1620-1631`）只发生一次，无二次机会 |
| 2.24 | 首笔迁移会搬走「画在文档视口之外」的笔迹 | `MW_UILinkage.cs:281-312` vs `:1284-1285` | pending 登记只用 `FindWindowBehind` 判断「后面是 Word 窗口」，没有判断是否在 Document 视口内；迁移时又用 `inst.ScreenRect.Contains` 过滤 → 用户在菜单栏/标尺上起笔会被搬进独立画布，视口内的反而可能留在主画布 |
| 2.25 | 橡皮擦同时喂给两个画布，且实例解析「粘住」 | `MW_Eraser.cs:258-277`、`MW_UILinkage.cs:1345-1358` | 主画布测试器无条件 `AddPoint`；独立画布实例只在首次移动解析一次，之后整个拖拽都继续喂给它（`TryGetUILinkageLocalPoint` 传入实例时**不做包含性判断**，只查 `Visibility`）→ 拖回白板仍在擦独立画布 |
| 2.26 | `SetClip` 每拍重建 `DrawingBrush`/`CombinedGeometry` 图 | `MW_UILinkage.cs:1899-1929`、`IndependentCanvas.cs:756-773` | 每实例每 250ms 无条件下写 `OpacityMask`，即使遮挡集合没变 → GC 压力 |
| 2.27 | `_uiLinkageTimer` 无条件启动 | `MainWindow.xaml.cs:1528` | 不看 `Settings.UILinkage.IsEnabled`。功能关闭时仍每 250ms 跑四轮空循环（含 Linq） |
| 2.28 | 提示条 10 秒内遮挡视口中央书写区 | `IndependentCanvas.cs:363-371`、`:31` | `panel.IsHitTestVisible = true`（只有内部卡片是 false），居中悬浮 → 绑定后 10 秒内**正中央无法落笔** |
| 2.29 | `UpdateUILinkageInstanceGeometry` 无条件 `Visibility = Visible` | `MW_UILinkage.cs:1840-1841` | 可见性唯一依据是目标窗口，不看本程序自身状态；重播模式（`MW_FloatingBarIcons.cs:1652` 把父容器设 `Hidden`）下仍在每拍复位 |
| 2.30 | 用**主窗口** DPI 换算**目标窗口**的物理矩形 | `MW_UILinkage.cs:1879-1887`、`:1551-1553` | 跨显示器不同缩放比时，长截屏的切片/拼接步距可能错；`viewH` 若取自刚切显示器后的旧 `GetDpi` 会加剧 |
| 2.31 | 滚动动画在途时落笔，会按「未烘焙的剩余位移」放置 | `IndependentCanvas.cs:706-727` | ≤220ms 补间期间 `d == p` 只对已烘焙部分成立，`_pendingShift` 仍欠账 → 新笔会被剩余动画继续上移（需实机验证） |
| 2.32 | 滚动/缩放写入笔迹坐标只有一条循环遵守截图标志 | `MW_UILinkage.cs:2321-2322` vs `:1456` | 后台截图线程调 `SetScrollInfo(..., animate:false)`，几何/裁剪循环不设防（不写笔迹数据，故影响有限，但守卫不对称） |

---

### P3（残留 / 清理）

| # | 问题 | 位置 |
|---|---|---|
| 2.33 | `StrokeInitialHistory` / `DrawingAttributesHistory` 对「已迁出主画布」的笔迹不会清理（只在 `e.Removed` 清） | `MW_TimeMachine.cs:721`、`:736` |
| 2.34 | `DisposeUILinkageWatching()` 定义了但**从未被调用** → UIA `AddStructureChangedEventHandler` 订阅与 `WH_MOUSE_LL` 钩子没有窗口关闭出口 | `MW_UILinkageWatch.cs:378-388`（0 调用点）；对比 `MainWindow.xaml.cs:1973-1976` 只调了 `UnbindAllUILinkageCanvases` |
| 2.35 | `_uiLinkageTimer` 从未显式 `Stop()`（依赖 Dispatcher 关闭） | `MW_UILinkage.cs:241-256` |
| 2.36 | 擦除路径对独立画布笔迹做的 `FrozenStrokePropertyGuid` 过滤是空操作（该 GUID 只打在主画布笔迹上） | `MW_Eraser.cs:288`、`:303` vs `MW_InkFreeze.cs:91-96` |

---

## 3. 已排除的疑点（避免重复排查）

用独立 WPF 探针程序实测（`net10.0-windows` + `UseWPF`）：

| 疑点 | 结论 | 证据 |
|---|---|---|
| `SetScrollInfo` 里 `bar.Value = …` 会不会反向触发 `Scroll` → `OnUserScroll` → 回写 Word，形成跨进程反馈环？ | **不会**。程序化写 `Value` 不引发 `Scroll`（未实现化与已实现化的条都测得 0 次）。`OnUserScroll` 的注释是正确的 | 探针 `T1`：`=50` → 0 次；`RaiseEvent(ScrollEvent)` → 1 次；窗口内已布局 `=40` → 0 次 |
| 滚动动画每帧 `stroke.Transform` 会不会污染 `IndependentInkHistory`？ | **不会**。`Stroke.Transform` 不触发 `StrokesChanged` | 探针 `T2`：集合内 `Transform` → 0 次；`foreach Transform` → 0 次 |
| 同一 `Stroke` 能否同时存在于两个 `InkCanvas.Strokes`？ | **可以**（这正是 §2.4 的成因）。但同一集合重复 `Add` 会抛 `ArgumentException` | 探针 `T3` |
| `IndependentInkHistory.Undo/Redo` 在 `Added`+`Removed` 同时非空时会不会错乱？ | 顺序正确：`Undo` 先删 `Added` 再加 `Removed`，`Redo` 反之，且 `_applying` 抑制重入；`Replace` 的事件是 `+2 -1` | 探针 `T5` |
| `CompositionTarget.Rendering` 订阅是否泄漏？ | **平衡**。`StartScrollAnimation` 仅在 `!_scrollAnimating` 时订阅，`StopScrollAnimation` 仅在置位时退订；所有退出路径（补间结束、`animate:false`、`BeginScrollSession`、`FlushScrollAnimation`）都经过退订 | `IndependentCanvas.cs:687-704`、`:679-685` |
| `FlushScrollAnimation` 的提前 return 会不会丢更新？ | 不会。标志为 false 时 `_pendingShift` 可证明为 0 | `IndependentCanvas.cs:679-685` |
| `_snapNextScrollSync` 状态机会不会丢位移？ | 不会。`animate:false` 分支先 `_pendingShift + shift` 再清零 | `IndependentCanvas.cs:607-614` |
| 独立画布会不会遮挡下方 Word？ | 不会，`Background` 显式设为 `Brushes.Transparent`（`:83`、`:91`）；WPF 默认是 `#FFFFFFFF`（探针 T6），说明这里是有意覆盖 | `IndependentCanvas.cs:83` |
| `IsHighlighter` 会不会被上一次高亮卡在 `true`？ | 不会 —— 因为它**从未被设成过 `true`**（问题相反，见 §2.6） | 探针 `T4` + `MW_UILinkage.cs:1403-1409` |
| 窗口最小化时会不会把墨迹推飞？ | 已处理。滚动同步与几何刷新都检查 `IsMinimized` | `MW_UILinkage.cs:1789`、`:1454` |

---

## 4. 两套画布的能力对照总表（⚠️ 已过期，请改看 §9.3）

| 能力 | 主画布 `inkCanvas` | 独立画布 `IndependentCanvas` |
|---|---|---|
| 笔迹后处理管线（15 阶段） | ✅ | ❌ |
| 墨迹渐隐 / 激光笔 | ✅（笔迹不进 `Strokes`） | ❌（留下永久墨迹） |
| 压感归一/模拟/笔锋 | ✅ | ❌ |
| 直线拉直 + 端点吸附 | ✅ | ❌ |
| 形状识别 | ✅ | ❌ |
| 高级贝塞尔平滑 / 手写体美化 | ✅ | ❌ |
| `DrawingAttributes` 同步 | 源 | ⚠️ 只 3 个属性 |
| 触摸抑制 / 手掌擦 / 双指手势 | ✅ | ❌ |
| WinRT 原生湿墨迹 | ✅ | ❌（被降级为合成鼠标输入） |
| 撤销/恢复 | `TimeMachine`，9 种提交类型、按页 | 独立 200 步，仅画布内按钮 |
| 框选移动的可撤销性 | ✅ | ❌（两套历史都不记录） |
| 页面冻结 | ✅ | ❌（含延迟自动冻结误判） |
| 自定义橡皮擦 | `EraserOverlayCanvas`（ZIndex 1000） | 原生擦除 + 坐标偏差 |
| 选择/套索/克隆/变形 | ✅ | 原生选择，无应用 chrome |
| 图形绘制（25 种） | ✅ | ❌ |
| 清空 | 全部 | ⚠️ 只清视口，且多数入口漏调 |
| 保存/另存图片/UInk | ✅ | ❌（仅按文档名 sidecar `.isf`） |
| 周期自动保存 | ✅ | ❌ |
| 墨迹重播 / 页面缩略图 | ✅ | ❌ |
| 插件 / 协议可见性 | ✅ | ❌ |
| 边缘扩展 / roaming 平移 | ✅ | ❌ |

**当前实际接上的集成点只有 6 个**：① 橡皮擦（面积/线擦）、② 清空画布按钮、③ 退出批注/自动折叠的长截屏、
④ 首笔迁移 + 调试采集、⑤ 解绑时保存（关窗/关开关/换文档）、⑥ 250ms 状态/滚动/几何/裁剪轮询。

---

## 5. 修复优先级建议（不含实现细节）

| 优先级 | 项 | 理由 |
|---|---|---|
| **P0** | §2.1 + §2.2 统一 `d = p − S` 换算 | 一个根因、一处封装即可同时修好迁移与擦除；当前是「看得到的错位」 |
| **P0** | §2.4 迁移改为「克隆进独立画布」或让两侧历史互斥 | 一笔两处渲染 + 两个栈争抢同一对象 |
| **P0** | §2.3 换文档路径补 `ResetInkHistory()` | 一行，修「旧文档笔迹复活」 |
| **P0** | §2.6 `DrawingAttributes` 整体克隆 | 一行，修荧光笔/压感 |
| **P0** | §2.7 独立画布触摸抑制 | 触屏设备上直接误落墨 |
| **P0** | §2.8 至少让激光笔/渐隐在独立画布上「禁用或补齐」 | 当前语义相反 |
| **P0** | §2.9 Tick 加 try/catch | 一次 UIA 失败不应升级为崩溃 |
| **P1** | §2.5 收敛清空入口（把独立画布纳入 `PerformCanvasClear`）；把独立画布墨迹纳入自动保存与截图合成 | 数据丢失风险 |
| **P1** | §2.10 Sumatra 滚动异步化 | UI 线程数秒级阻塞 |
| **P1** | §2.12 全局撤销解析「当前焦点/最后落笔的画布」 | 避免误删主画布 |
| **P1** | §2.13 独立画布选择移动纳入历史 | 不可撤销是硬伤 |
| **P1** | §2.15 持久化 key 加入 HWND / 完整标题指纹 | 串写风险 |
| **P1** | §2.17 / §2.20 / §2.21 状态标志与守卫对称化 | 自锁死状态 |
| **P2** | §2.11、§2.14、§2.16、§2.18、§2.19、§2.22–§2.32 | 打磨 |
| **P3** | §2.33–§2.36（含补上 `DisposeUILinkageWatching()` 调用） | 残留与清理 |

---

## 6. 重要：分析期间代码正在被并发改写

分析进行中，工作区出现了**未提交的新文件与改动**（时间戳集中在同一分钟内，即本功能正在被重写）：

```
 M  Ink Canvas/Helpers/UILinkageWindowFinder.cs        (+64)
 M  Ink Canvas/MainWindow_cs/MW_UILinkage.cs           (+13/-3)
??  Ink Canvas/Helpers/UILinkageMouseGrabHook.cs       (新增 7.5 KB)
??  Ink Canvas/MainWindow_cs/MW_UILinkageWatch.cs      (新增 25.5 KB)
```

**改写内容**：用「实时读取前台窗口 + 监听其 UIA 树」替代原来的「首笔采样识别」
（`MW_UILinkageWatch.cs:16-19`），并新增调试面板的「鼠标抓取元素」开关（`WH_MOUSE_LL` 低级钩子）。

**对本文结论的影响**：

* **§2.1 / §2.24 的「首笔采样」路径基本被废弃** —— 新代码在非调试态直接
  `if (!debug) return;`（`MW_UILinkage.cs:320-321`），识别改由 `UpdateUILinkageForegroundWatch()` 驱动，
  `BindUILinkageCanvas` 由 UIA 区域探测直接调用（`MW_UILinkageWatch.cs:363-368`）→ `MovePendingStrokesInto`
  主要只在「打开开关的瞬间已有墨迹」时起作用。**但坐标换算错误本身仍在代码里，尚未修复。**
* **§2.6 / §2.7 / §2.8 / §2.10 / §2.11 / §2.12 / §2.13 完全不受影响**（`SyncUILinkageCanvasState`、
  `IndependentCanvas`、`ProcessCommittedStroke`、结果化路径、`GlobalHotkeyManager` 都未被触碰）。
* **新增风险（未提交改写引入）**：
  * **§2.34 新确认**：`DisposeUILinkageWatching()`（`MW_UILinkageWatch.cs:378-388`）**没有任何调用点**，
    而它负责停止 UIA 树监听并释放鼠标钩子 → 未提交代码本身就带着资源清理缺口。
  * **新交互风险**：识别改为「聚焦 Word 即自动建独立画布」后，**绑定不再需要用户先写一笔**。
    若 Word 不是本程序的白板页而只是被置前，独立画布会在用户切回 Word 的**第一时刻**就处于
    `IsHitTestVisible = true` 状态，用户用来滚动文档的笔会直接落成批注。建议在实机验证。
  * `UpdateUILinkageForegroundWatch()` 被插入到没有 try/catch 的 Tick 链最前面（`MW_UILinkage.cs:250`），
    放大了 §2.9 的影响面。

**构建校验**：`dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Debug -p:Platform=x64`。
改写过程中（resx 正在被写入的窗口期）曾出现 2 个错误：

```
MW_UILinkageWatch.cs(70,97): error CS0117: "UILinkageStrings"未包含"DebugGrabStatus"的定义
MW_UILinkageWatch.cs(73,96): error CS0117: "UILinkageStrings"未包含"DebugNoCapture"的定义
```

待文件写完后**重新构建已通过（0 错误 0 警告）** —— 属于「边写边构建」的瞬时不同步，不是遗留缺陷。
但按 `rules/build.md:86-93` 的要求，请确认新增的 `DebugGrabToggle` / `DebugGrabHint` / `DebugGrabStatus` / `DebugNoCapture`
四个键在 `UILinkageStrings.resx`、`.en-US.resx`、`.zh-ME.resx` 三处齐全（当前已齐全）。

---

## 7. 验证方法

* **源码**：`Controls\IndependentCanvas.cs`（896 行全文）、`MainWindow_cs\MW_UILinkage.cs`（2805→2853 行，分块通读）、
  `MW_UILinkageWatch.cs`、`MW_Eraser.cs`、`MW_SimulatePressure&InkToShape.cs`（管线关键段）、`MW_TimeMachine.cs`、
  `MW_TouchEvents.cs`（关键段）、`MW_FloatingBarIcons.cs`（清屏/工具切换段）、`MainWindow.xaml` / `.xaml.cs`（事件装配段）、
  `MW_Save&OpenStrokes.cs` / `MW_Screenshot.cs` / `MW_ImageInsert.cs` / `MW_Timer.cs` / `MW_UInk.cs`（消费点）、
  `Helpers\UILinkage*.cs`、`Ink\WinRT\*`（输入门）。
* **探针**：临时 WPF 控制台程序（`net10.0-windows` + `UseWPF`，STA），实测 §3 表中各项 WPF 语义。
  探针位于系统临时目录，**不修改本仓库任何文件**。
* **构建**：`dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Debug -p:Platform=x64`，最终 0 错误 0 警告。
* **未做**：未做运行时 UI 验证（需要真实 Word / SumatraPDF 与触屏设备）。
  §2.19 / §2.20 / §2.30 / §2.31 属「代码推演 + 状态机分析」，建议按各条触发场景实机复核。

---

## 8. 修复进度（⚠️ 已过期，本轮修复见 §9.1）

策略：**复用宿主现成服务 + 薄适配层**，不改主画布的 `ProcessCommittedStroke`
（其 ~95 处 `inkCanvas.Strokes` 与 `TimeMachine` 语义交织，参数化等于重写）。
每批改动均以 `dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Debug -p:Platform=x64` 通过为准。

### 第 1 批：P0 正确性

| 章节 | 状态 | 落地位置 |
|---|---|---|
| §2.1 迁移落点偏移（`d = p − S`） | ✅ 已修 | `MW_UILinkage.MovePendingStrokesInto`：补 `FlushScrollAnimation()` + 叠加 `-AppliedScrollOffset` |
| §2.2 橡皮擦擦错位置（同根因） | ✅ 已修 | `TryGetUILinkageLocalPoint`：`d = 主坐标 − Margin − S`（含 2 参重载，原为死代码） |
| §2.3 换文档后撤销画出旧文档笔迹 | ✅ 已修（并发改动） | 两处换文档分支均已有 `ResetInkHistory()` |
| §2.4 Ctrl+Z 复活笔迹致一笔两画布 | ✅ 已修 | 迁移改为 `target.Clone()` 后入独立画布，与主画布撤销历史彻底解耦 |
| §2.6 荧光笔/压感属性失效 | ✅ 已修 | `DrawingAttributesEquivalent` 全维度比对 + 整体 `Clone()` |
| §2.7 触摸直接落墨 | ✅ 已修 | `IndependentCanvas` 触摸抑制：100ms 多指判定窗口、切 `None`、**延后一拍**回滚误笔迹 |
| §2.9 轮询异常升级为崩溃 | ✅ 已修 | `RunUILinkageStage` 逐阶段兜底 + 5s 日志节流 |
| §2.11 清空只清视口 | ✅ 已修 | `ClearAllStrokes()` 全清，语义对齐主画布 |

### 第 2 批：一致性与保存

| 章节 | 状态 | 落地位置 |
|---|---|---|
| §2.5 清空入口漏独立画布 | ✅ 已修 | 收敛进 `PerformCanvasClear`，一次覆盖玻璃栏/迷你栏/长按撤销/自动化/白板删除 |
| §2.5 周期性自动保存丢失 | ✅ 已修 | 新增 `SaveAllUILinkageInk()`，挂到 `AutoSaveStrokesTimer_Tick` |
| §2.5 截图合成丢失批注 | ✅ 已修 | `CreateInkOverlayPreviewBitmapSource` 补画联动实例笔迹（含 `+S` 与画布原点换算） |
| §2.12 Ctrl+Z 无法作用于独立画布 | ✅ 已修 | 新增 `MW_InkActivity.cs`：按「最近落笔画布」分派；`fromHotkey` 区分快捷键与工具栏按钮 |
| §2.19 截图期间几何被改写 | ✅ 已修 | 截图进行中且视口矩形未变时跳过 `SetViewportSize` |
| §2.22 `ResetScrollFrame` 视口宽高用错 | ✅ 已修 | 竖直条改用 `_viewportH`，extent 取视口本身 |

### 第 3 批：管线复用（本轮）

| 章节 | 状态 | 说明 |
|---|---|---|
| §2.8 压感归一 | ✅ 已接 | `MW_IndependentInkPipeline.cs`，与主画布同口径读 `Settings.Canvas.DisablePressure` |
| §2.8 高级贝塞尔平滑 | ✅ 已接 | 复用宿主共享的 `InkSmoothingManager.SmoothStroke` + `CopyPropertyData`（保激光标记等 property data），走 `Strokes.Replace` 单步撤销 |
| §2.8 形状识别 / 直线拉直 | ⏸ 未接（有意） | 几何实现内联在主画布管线（~380 行）且与 `TimeMachine` 提交语义、`GridInkCanvasSelectionCover` 状态交织；复用需先抽取，单独实现属重复造轮子。**Word 批注上手绘圆/矩形保持原样** |
| §2.8 墨迹渐隐 / 激光笔 | ⏸ 未接（有意） | `InkFadeManager` 的视觉挂在 `inkCanvas.Parent`，其调用契约是「把笔迹从主画布集合移除」。沿用会让渐隐视觉与批注层错位；需先给该管理器加「按画布挂载视觉」的能力，属单独一轮 |
| §2.8 压感模拟 / 笔锋 | ⏸ 未接（有意） | 与实时墨迹输入管线（`StrokeVisual` 手工收集）绑定，独立画布走 WPF 原生收集，不共享输入路径 |

### 仍待处理

| 章节 | 项 | 为什么没在本轮做 |
|---|---|---|
| §2.10 | SumatraPDF 滚动闭环在 UI 线程阻塞数秒 | 需要把 `TrySetSumatraScrollPercent` 的 192 次阻塞发送改成异步 + 取消，涉及读数反馈闭环重排，风险面较大 |
| §2.15 | 文档名相同导致墨迹串写 | 加指纹会改变文件路径 → **已存在的 `.isf` 用户批注会失效**。需要先定「同进程双开同名文件」的取舍，属产品决策 |
| §2.14 | WinRT 开启时笔被降级为合成鼠标 | 需要改 `WinRTInkInputGate` 的画布判定，让联动画布被视为 `CanvasSurface`，属独立模块 |
| §2.20 | `IsCapturingLongScreenshot` 可能自锁 | 触发条件很窄（`Task.Run` 调度失败），优先度低于上表 |
| §2.30 | 跨显示器不同缩放比下长截屏步距 | 需实机多显示器验证才能确认，无法在此环境判定 |
| §2.33 | `StrokeInitialHistory` 残留 | 内存级、量小，不紧急 |
| §2.34 | `DisposeUILinkageWatching()` 未调用 | ✅ 已由并发改动补上调用点 |
| §2.5 其余 | 页面保存/切页/缩略图/重播/插件可见性 | 需要先定义「联动画布墨迹是否属于白板页」的语义，属设计问题 |

### 改动文件清单（本轮）

新增：`MainWindow_cs\MW_InkActivity.cs`、`MainWindow_cs\MW_IndependentInkPipeline.cs`。
修改：`Controls\IndependentCanvas.cs`、`MainWindow.xaml.cs`、`MainWindow_cs\MW_UILinkage.cs`、
`MW_FloatingBarIcons.cs`、`MW_Eraser.cs`、`MW_Timer.cs`、`MW_ImageInsert.cs`、`MW_Hotkeys.cs`、`MW_UriHandler.cs`、
`Helpers\GlobalHotkeyManager.cs`、`Windows\SettingsViews\Pages\HotkeyPage.xaml.cs`。

---

## 9. 当前状态（权威）

> 本节是**唯一**应当作为「代码现在能做什么」依据的章节；上面的 §0/§4/§5/§8 保留为审计历史。
> 基线：`dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Debug -p:Platform=x64` **0 错误 0 警告**。
> 全部结论为静态源码 + 编译校验，**未做真实 Word / 触屏的运行时验证**（实机验证项见本节末尾）。

### 9.1 已修正的缺陷（相对本文档 §2 的编号）

| 章节 | 原问题 | 现状 | 落地位置 |
|---|---|---|---|
| §2.1 | 首笔迁移落点偏移一个滚动距离 | ✅ 已修 | `MW_UILinkage.MovePendingStrokesInto`：`FlushScrollAnimation()` + 叠加 `-AppliedScrollOffset` |
| §2.2 | 橡皮擦擦错位置 | ✅ 已修 | `TryGetUILinkageLocalPoint`：`d = 主坐标 − Margin − S`，含 2 参重载 |
| §2.3 | 换文档后撤销画出旧文档笔迹 | ✅ 已修 | 三条换文档路径均补 `ResetInkHistory()`（`:1259`/`:1276`/`:2010` 一带） |
| §2.4 | Ctrl+Z 复活笔迹致一笔两画布 | ✅ 已修（本轮加强） | 迁移改 `target.Clone()`；并新增 **`CommitReason.CodeInput` 抑制**，迁移不再在主画布留下可撤销的「删除」 |
| §2.5 | 清空/保存/截图漏独立画布 | ✅ 大部分已修 | 清空收敛进 `PerformCanvasClear`；周期自动保存 `SaveAllUILinkageInk`；区域截图与**另存为图片**均补画批注 |
| §2.6 | 荧光笔/压感属性失效 | ✅ 已修 | `DrawingAttributesEquivalent` 全维度比对 + 整体 `Clone()` |
| §2.7 | 触摸直接落墨 | ✅ 已修（本轮补齐手掌擦） | 多指抑制 + **手掌擦**（复用 `PalmEraserCalculator` 与主画布同一套设置） |
| §2.9 | 轮询异常升级为崩溃 | ✅ 已修 | `RunUILinkageStage` 逐阶段兜底 + 5s 日志节流 |
| §2.11 | 清空只清视口 | ✅ 已修 | `ClearAllStrokes()` |
| §2.12 | Ctrl+Z 无法作用于独立画布 | ✅ 已修 | `MW_InkActivity.cs` 按「最近落笔」分派 |
| §2.13 | 框选移动不被撤销栈记录 | ✅ 已修（本轮） | `IndependentInkHistory` 新增几何变更记录：订阅每条 `Stroke.StylusPointsChanged`，拖动过程就地合并为一条记录 |
| §2.15 | 同名文档串写 | ✅ 已修（本轮重做） | 持久化键改为「文档名 + 文档内窗口槽位」，见 §9.2 |
| §2.17/§2.19/§2.20/§2.22/§2.34 | 状态标志守卫不对称 / 自锁 / 视口宽高用错 / 清理未调用 | ✅ 已修 | 截图看门狗、`ResetUILinkageCaptureState` 三收尾共用、`ResetScrollFrame` 用 `_viewportH`、`DisposeUILinkageWatching` 有调用点 |
| **新增** | **跨会话丢批注**（历史实现用 HWND 做持久化键后缀） | ✅ 已修（本轮） | 见 §9.2 |
| **新增** | **多实例重叠时输入/擦除落到错误文档** | ✅ 已修（本轮） | `TryGetUILinkageInstanceAt` 逆序取 Z 序最高；`TryGetUILinkageLocalPoint` 带实例重载补包含性校验；橡皮擦按点重新解析实例、拖出视口即停 |
| **新增** | **批注书写不刷新 `pageLastUserInkMutationUtc`** | ✅ 已修（本轮） | `IndependentCanvas_StrokeCollected` 开头 `MarkCurrentPageInkChanged()` |

### 9.1.1 本轮另外两处收尾

* **编译阻塞**：`MW_NativeWetInk.cs`（新原生湿墨迹管线，未跟踪）与 `MW_WinRTInk.cs`（旧 WinRT 管线）
  重复定义了 8 个 `MainWindow` 成员，加上 `MainWindow` 级与 `Ink_Canvas.Ink.Native` 级各有一套同名
  `LogicalInkTool` / `CanvasHitZone`，共 9 个错误。已删除旧文件中的副本、新增显式枚举映射
  （`MapHitZone` / `MapLogicalInkTool`，用具名 `switch` 而非强制转换，避免枚举值顺序变化后静默错位），
  并补齐缺失的 `Settings.Canvas.UseLegacyWetInk` / `EnableNativeInkPrediction`、
  `RealtimeInkPerformanceMonitor.TickLiveStatus`、`AdvancedStrings` 的 resx 键与 Vortice 的三处签名。
  另外 `MW_IndependentInkPipeline` 的类注释曾声称「形状识别/直线拉直、墨迹渐隐、压感模拟/笔锋**刻意不接**」，
  与同文件实际调用点自相矛盾，已重写。
* **订阅生命周期**：`IndependentInkHistory` 现在实现 `IDisposable`，由
  `IndependentCanvas.ReleaseSubscriptions()` 暴露，并在 `UnbindUILinkageInstance` 移出视觉树时调用 ——
  几何变更记录需要把观察者挂到每条 `Stroke.StylusPointsChanged` 上，不解绑会让历史笔迹继续持有它。
  注意必须放在长截屏分支**之后**：截图期间画布仍留在屏幕上，历史要被保留。

### 9.2 持久化键（重要，别再退回 HWND）

历史实现给 `.isf` / `.state.json` 加 `_h{hwnd:x}` 后缀来区分多窗口同名文档。
**HWND 每次进程启动都由系统重新分配**，于是下次运行算出的键与上次不同；
而当时的保存路径还会**删除**旧版无后缀文件 —— 两者合起来导致
**同一文档的批注跨会话永久失联（磁盘上留孤儿文件）**，反向还会在 HWND 被复用时串档。

现在的键 = `SanitizeFileName(文档名)` + 文档内窗口槽位：

* 槽位由 `AssignUILinkageWindowSlot` 分配「该文档当前占用的最小空闲正整数槽」；
* 槽 1 **不带后缀**，与旧版文件名天然兼容；
* 解绑与换文档都释放槽位（`WindowSlot = 0`），所以键不会因开关顺序漂移；
* 保存**不再删除**旧版文件；读取按「稳定键 → 旧版无后缀 → 旧版 `_h*` 孤儿文件」三级回退
  （`TryFindOrphanedHwndKeyedPath` 取最近写入的一份）。

> 已知残留：文档身份仍来自**窗口标题去掉应用后缀**，不含文件路径。
> 因此「不同目录下的同名文件」在同一个窗口内切换时不会触发换档，仍会共用一份 `.isf`；
> `SanitizeFileName` 把非法字符统一替换为 `_`，会让 `a:b.docx` 与 `a?b.docx` 碰撞。
> 彻底解决需要拿到文档的稳定路径指纹（UIA 或窗口标题之外的来源），属独立一轮。

### 9.3 能力对照（替代过期的 §4）

| 能力 | 主画布 | 独立画布 |
|---|---|---|
| 压感归一 / 触摸压感模拟 | ✅ | ✅（同一份设置与算法） |
| 实时笔锋 | ✅ 书写中 | ⚠️ 仅抬笔后一次性应用 |
| 直线拉直 + 端点吸附 | ✅ | ✅（吸附候选只取本画布笔迹） |
| 形状识别 | ✅ | ✅（共享核心；矩形参考线/选区遮罩为主画布专属，独立画布显式留空） |
| 高级贝塞尔平滑 | ✅ | ✅（含 `CopyPropertyData`） |
| 墨迹渐隐 / 激光笔 | ✅ | ✅（同一 `InkFadeManager`，视觉宿主换成本画布渐隐层） |
| `DrawingAttributes` 同步 | 源 | ✅ 全维度克隆（250ms 轮询） |
| `EditingMode` 同步 | 源 | ✅ 按逻辑工具映射 |
| 触摸抑制 / 多指手势 | ✅ | ✅ |
| 手掌擦 | ✅ | ✅（本轮补齐，同一套接触面积判定） |
| 撤销/恢复 | `TimeMachine`，按页 | 独立 200 步，含**框选几何变更**；Ctrl+Z 按最近落笔分派 |
| 框选移动可撤销 | ✅ | ✅（本轮补齐） |
| 页面冻结拦截 | ✅ | ✅ |
| 清空画布 | 全部 | ✅ 已收敛进 `PerformCanvasClear` |
| 周期自动保存 | ✅ | ✅ sidecar `.isf`（受 `UILinkage.AutoSaveInk` 门控） |
| 区域截图 / 插入截图 / **另存为图片** | ✅ | ✅ 均已合成批注 |
| 主画布存档格式（`.icstk`/XML/ZIP/UInk） | ✅ | ❌ 不含批注（设计取舍：按外部文档而非白板页持久化） |
| 墨迹重播 / 页面缩略图 / 切页 | ✅ | ❌（同上，需先定义「联动画布墨迹是否属于白板页」） |
| 插件 / 协议可见性 | ✅ | ❌ |
| WinRT 湿墨迹开启时 | ✅ | ❌ 被降级为合成鼠标输入（§2.14，未修） |

### 9.4 仍待处理

| 项 | 说明 |
|---|---|
| §2.10 | SumatraPDF 滚动闭环在 UI 线程阻塞数秒（192 次阻塞发送 + Sleep） |
| §2.14 | WinRT 原生湿墨迹开启时，独立画布上的笔被降级为合成鼠标（`WinRTInkInputGate` 的画布判定） |
| §2.16 | 解绑默认触发长截屏：在设置页关掉联动开关会顺带上传播送一张长截图 |
| §2.25 残留 | 面积擦已按点重新解析实例；`FrozenStrokePropertyGuid` 过滤对独立画布仍是空操作 |
| §9.2 残留 | 文档身份不含路径 → 同名不同目录仍可能共用一份 `.isf` |
| §2.5 其余 | 页面保存/切页/缩略图/重播/插件可见性（属设计问题） |
| 实机验证 | `AppliedScrollOffset` 与画布 DIP 的尺度一致性；触屏上的手掌擦阈值；多窗口重叠时的输入路由 |

### 9.5 验证方法

* **源码**：`Controls\IndependentCanvas.cs`、`MainWindow_cs\MW_IndependentInkPipeline.cs`、
  `MainWindow_cs\MW_UILinkage.cs`、`MainWindow_cs\MW_Eraser.cs`、`Helpers\InkFadeManager.cs`、
  `Ink\EraserContactSizing.cs`（手掌擦共用算法）。
* **构建**：`dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Debug -p:Platform=x64` → 0 错误 0 警告。
* **未做**：运行时 UI 验证（需真实 Word / SumatraPDF 与触屏设备）。

---

## 10. Word 横向滚动读数修复（本轮）

> 症状（用户报告）：**Word 视口横向滚动的读取有误** —— 滚动位置「可以置入」Word，
> 但独立画布（向外延伸）的横向滚动条会**回到首位**，且**墨迹不随横向滚动**。

### 10.1 根因：横向只有一路不可靠的来源

横向偏移原先只取自 UIA `ScrollPattern` 的 `HorizontalScrollPercent`/`HorizontalViewSize`
（`UpdateUILinkageScrollSync` 里的 `offX`/`extW`）。Word 这两个横向读数不可靠 ——
文档明明宽于视口时也常报 `NoScroll`（-1）或 0，`HorizontalViewSize` 报 100。于是：

1. `extW = viewW * 100 / hs` 在 `hs >= 100` 时退化成 `viewW` → 画布横向滚动条 `Maximum = 0`
   → 滑块恒在最左（**回到首位**）、横向辅助按钮不可用；
2. `offX = hp/100 * (extW − viewW) = 0` → 每拍 `SetScrollInfo` 把横向偏移写回 0
   → **墨迹不随横向滚动**，已被拖出的位置被弹回；
3. 拖动画布横向滚动条 / 点辅助按钮时，`scrollable <= 0` 分支把 `hpTarget` 兜底成 **0**
   并真的下发（`sp.SetScrollPercent(0, …)`）→ **连 Word 自己也被拉回最左**；
4. 滚动记忆把「读不到」写成 `0`（`IsNaN ? 0 : hp`），恢复时 `SetScrollPercent(0, vp)`
   再次把 Word 横向拉回首位。

原有唯一可靠的一路是「页面左缘实测」（`TryReadUILinkagePageLeftDip`），但它只修正 `offsetX`：
既不决定 `extW`（滚动条范围），在草稿/Web 版式（无页面元素）时也不可用。

### 10.2 修复：横向以文本区水平滚动条为准

横向改用 **文本区（`_WwG`）的水平滚动条**（`GetScrollInfo(SB_HORZ)`，与竖直轴同一套 Win32 通路，
此前只用于调试面板输出）：

| 量 | 取法 | 说明 |
|---|---|---|
| 位置 | `nPos − nMin` | 内容单位，与滚动条同口径 |
| 内容总单位数 | `nMax − nMin + 1` | 换算 DIP 用「视口宽 / nPage」，与单位是像素/缇/列无关 |
| 视口单位数 | `nPage` | |
| 百分比 | `pos / (total − nPage) × 100` | 与 `ScrollPattern` 百分比同义，可直接回写 `SetScrollPercent` |

* 新增 `UILinkageScrollReader.TryGetWordHorizontalScroll`（并为 `RawScrollInfo` 补 `Total`/`MaxPos`）；
* `MW_UILinkage` 新增 `TryReadUILinkageWordHorizontal`：位置取滚动条，**绝对对齐（帧）仍沿用页面左缘实测**
  —— 两者只差一个常量（页面居中留白、桌面边距），首帧标定后沿用
  （`WordHorizontalFrameOffset`；页面帧跳变超过 `UILinkageWordHorizontalFrameToleranceDip` 时保留旧值）。
  这样即使页面实测元素失效/被裁剪，位置仍由滚动条推进，不会停住也不再弹回；
* 三路优先级：**滚动条（可横向滚动时）→ 页面左缘实测（居中态）→ 保持上一拍已应用的口径**
  （`LastOffsetX`/`LastExtW`）。Word 不再退回 `ScrollPattern` 推算横向偏移 —— 那正是「恒为 0」的来源；
  SumatraPDF 仍走它的百分比（由 Win32 滚动条换算，可靠）；
* 帧标定只在「页面实测确实与滚动条同步位移」时跟随（`LastWordFrameSampleBarOffset`/`...PageLeftDip`
  的增量比对），页面读数卡住时保留旧标定 —— 否则标定会把滚动位移整个抵消掉（「墨迹不滚动」的另一种成因）；
  缩放变化与换文档都清空标定（页面留白随版式变化）；
* 回写 Word 的水平目标同样只认滚动条读数：取不到就**不动该轴**（`NoScroll`），不再退回 ScrollPattern 百分比；
* 「不可横向滚动」（`nPage == 0` 或范围退化）时不采用滚动条读数，交由页面实测表达居中留白；
* `_WwG` 句柄按实例缓存 + 2s 冷却重找（避免每拍枚举子窗口）。

### 10.3 顺带修掉的「回写 0」缺陷

| 位置 | 原行为 | 现行为 |
|---|---|---|
| `OnUILinkageCanvasUserScroll` | 算不出范围时 `hpTarget = 0` 并下发 | 该轴传 `NoScroll`（不动） |
| `OnUILinkageCanvasScrollStep` | 横向用 `ScrollPattern` 的范围与当前位置 | 同一路滚动条读数；取不到就 `return` |
| `SaveUILinkageScrollPosition` | `IsNaN ? 0 : hp` | 保持 NaN，并记 `horizontalPercentTrusted` |
| `RestoreUILinkageScrollPosition` | `Math.Max(0, NaN)` → 下发 | 不可信/非法 → `NoScroll`；旧版文件（无标记）一律按不可信处理，见 §10.1-4 |

### 10.4 诊断（实机验证时先看这两处）

* 调试面板 `[Word 水平滚动]` 行现在直接给出 `位置 / 单位总数 / 视口单位 / 可滚动上限 → 百分比`，
  可与上一行 `[视口 滚动位置] H:…`（ScrollPattern）对照：两者不一致即证实横向读数不可靠。
* 日志（`Trace` 级）新增 `UI联动横向读数 | …`：仅在读数形态变化时写、2s 节流，
  输出滚动条位置、页面实测是否可读、帧标定值与最终偏移。

### 10.5 验证与残留

* **构建**：`dotnet build "Ink Canvas\InkCanvasForClass.csproj" -c Debug -p:Platform=x64` → 0 错误 0 警告。
  `-p:Platform=AnyCPU` 的**编译**同样通过；若此时正在运行 `bin\Debug\AnyCPU` 下的程序，
  输出文件被占用，末尾的复制步骤会以 MSB3026/MSB3027 失败（关掉程序后重新构建即可）。
* **未做**：真实 Word 的运行时验证。若仍异常，按 §10.4 分两种情况：
  * `[Word 水平滚动]` 报「不可横向滚动 / 读取失败」→ `_WwG` 的水平滚动条未暴露范围
    （overlay 滚动条等），退化为页面实测帧；
  * 日志中 `页面实测=不可读` 且帧标定从未建立 → 页面元素不成立（草稿/Web 版式），
    横向只能依赖滚动条帧，可能整体差一个未知的桌面边距。
* **残留**：回写 Word 时假定「滚动条百分比 ≡ `ScrollPattern` 百分比」（两者都是 0..100 的滚动范围占比）。
  若某版本 Word 两者口径不同，表现为「拖动画布横向滚动条落点不精确」，但墨迹仍严格跟随实际滚动
  （画布偏移取的是滚动条实测位置，不再依赖回写结果）。

