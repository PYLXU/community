using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// UI 联动 - Word 缩放读取：在 Word 顶层窗口的状态栏中定位缩放指示元素
    /// （缩放百分比文本优先，其次缩放滑块），供调用处缓存元素后低频读取。
    /// 缩放百分比用于缩放变化时把独立画布墨迹按比例重缩放，保持墨迹锚定文档内容。
    /// </summary>
    internal static class UILinkageZoomReader
    {
        /// <summary>缩放百分比匹配（形如 150%，允许出现在更长文本中）。</summary>
        private static readonly Regex ZoomRegex = new Regex(@"(\d{1,3})\s*%", RegexOptions.Compiled);

        /// <summary>
        /// 在 Word 顶层窗口中定位缩放指示元素：优先缩放百分比文本（Name/Value 含 NNN%），
        /// 其次缩放滑块（RangeValue，值域需与 Word 缩放范围 10..500 相符）。找不到返回 null。
        /// 调用处应缓存元素并自带查找冷却，不要高频调用（UIA 遍历有代价）。
        /// </summary>
        public static AutomationElement FindZoomElement(AutomationElement windowEl)
        {
            if (windowEl == null) return null;
            try
            {
                // 状态栏通常为窗口直接子级；直接子级未命中再限制预算 BFS 兜底。
                var statusBar = windowEl.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));
                if (statusBar == null)
                    statusBar = FindByBfs(windowEl, 150, IsStatusBar);

                if (statusBar != null)
                {
                    var zoom = FindZoomInSubtree(statusBar);
                    if (zoom != null) return zoom;
                }

                // 状态栏定位失败的兜底：限制预算在整窗内查找。
                return FindZoomInSubtree(windowEl, 150);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 读取缩放指示元素的当前百分比：优先 Name/Value 中的 NNN%（精确），
        /// 其次 RangeValue（滑块）。失败返回 false。
        /// </summary>
        public static bool TryReadZoom(AutomationElement el, out double zoomPercent)
        {
            zoomPercent = double.NaN;
            if (el == null) return false;
            try
            {
                // 快路径：状态栏缩放指示的 Name 通常就是「NNN%」。直接从 Name 解析，
                // 不再走「ReadZoomText 先正则取出子串 → TryParseZoom 再正则解析一次」的
                // 两遍匹配 + 中间字符串分配（本方法由 250ms 轮询每拍调用）。
                if (TryParseZoom(el.Current.Name, out zoomPercent)) return true;

                // 次选：部分实现把百分比文本放在 ValuePattern 的 Value 里
                //（原实现经 ReadZoomText 走的就是这条，语义保持一致）。
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vpp
                    && TryParseZoom(vpp.Current.Value, out zoomPercent))
                    return true;

                if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp) && rp is RangeValuePattern rpp)
                {
                    double v = rpp.Current.Value;
                    if (!double.IsNaN(v) && v >= 10 && v <= 500)
                    {
                        zoomPercent = v;
                        return true;
                    }
                }
            }
            catch
            {
                // 元素可能刚被销毁，返回失败交由调用处失效缓存。
            }
            return false;
        }

        /// <summary>在元素子树内查找缩放元素：百分比文本命中即返回，滑块作为备选。</summary>
        private static AutomationElement FindZoomInSubtree(AutomationElement root, int budget = 200)
        {
            AutomationElement sliderFallback = null;
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);

            int visited = 0;
            while (queue.Count > 0 && visited++ < budget)
            {
                var el = queue.Dequeue();
                try
                {
                    // 百分比文本最精确，命中即返回；滑块只作备选，继续找。
                    if (TryParseZoom(ReadZoomText(el), out _)) return el;
                    if (sliderFallback == null && IsTrustedZoomSlider(el))
                        sliderFallback = el;
                }
                catch
                {
                    // 忽略单个元素异常。
                }

                var child = walker.GetFirstChild(el);
                while (child != null) { queue.Enqueue(child); child = walker.GetNextSibling(child); }
            }

            return sliderFallback;
        }

        private static AutomationElement FindByBfs(AutomationElement root, int budget, Func<AutomationElement, bool> match)
        {
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);

            int visited = 0;
            while (queue.Count > 0 && visited++ < budget)
            {
                var el = queue.Dequeue();
                try { if (match(el)) return el; }
                catch { }

                var child = walker.GetFirstChild(el);
                while (child != null) { queue.Enqueue(child); child = walker.GetNextSibling(child); }
            }
            return null;
        }

        private static bool IsStatusBar(AutomationElement el)
        {
            try { return el.Current.ControlType == ControlType.StatusBar; }
            catch { return false; }
        }

        /// <summary>读取元素中形如 NNN% 的缩放文本：先 Name，再 ValuePattern.Value。</summary>
        private static string ReadZoomText(AutomationElement el)
        {
            try
            {
                string name = el.Current.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    var m = ZoomRegex.Match(name);
                    if (m.Success) return m.Value;
                }

                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern vpp)
                {
                    string value = vpp.Current.Value;
                    if (!string.IsNullOrEmpty(value))
                    {
                        var m = ZoomRegex.Match(value);
                        if (m.Success) return m.Value;
                    }
                }
            }
            catch
            {
                // 忽略读取异常。
            }
            return null;
        }

        private static bool TryParseZoom(string text, out double zoomPercent)
        {
            zoomPercent = double.NaN;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = ZoomRegex.Match(text);
            if (!m.Success) return false;
            // 直接用匹配组的切片解析：原实现先取 m.Groups[1].Value（分配一个字符串）再解析，
            // 本方法在 250ms 轮询与元素查找里都是热点。
            if (!double.TryParse(m.Groups[1].ValueSpan, out double v)) return false;
            if (v < 10 || v > 500) return false; // Word 缩放范围 10..500，范围外按无效处理。
            zoomPercent = v;
            return true;
        }

        /// <summary>
        /// 是否为可信的缩放滑块：RangeValue 值域需与 Word 缩放范围（10..500）大体相符。
        /// 错误读数会按错误比例缩放墨迹、主动破坏对齐，因此严格校验、宁缺毋滥。
        /// </summary>
        private static bool IsTrustedZoomSlider(AutomationElement el)
        {
            try
            {
                string type = el.Current.ControlType?.ProgrammaticName ?? "";
                if (!type.EndsWith(".Slider", StringComparison.Ordinal)) return false;
                if (!el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp) || !(rp is RangeValuePattern rpp))
                    return false;

                var c = rpp.Current;
                if (double.IsNaN(c.Minimum) || double.IsNaN(c.Maximum)) return false;
                if (c.Minimum < 10 || c.Minimum > 50 || c.Maximum < 100 || c.Maximum > 500) return false;
                if (c.Maximum <= c.Minimum) return false;
                if (double.IsNaN(c.Value) || c.Value < c.Minimum || c.Value > c.Maximum) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
