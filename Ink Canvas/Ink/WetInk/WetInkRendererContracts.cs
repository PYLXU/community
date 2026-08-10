using System;
using System.Collections.Generic;

namespace Ink_Canvas.Ink.WetInk
{
    /// <summary>
    /// 湿墨批渲染器接口：渲染线程持有实现，只通过信箱命令驱动。
    /// 抽出接口是为了让上层（控制器 / 窗口宿主）不依赖具体图形 API。
    /// </summary>
    internal interface IWetInkBatchRenderer : IDisposable
    {
        /// <summary>把渲染器绑定到覆盖层 HWND，并建立设备/合成树/交换链。</summary>
        void BindTarget(IntPtr hwnd, in WetInkTargetSnapshot target);

        /// <summary>覆盖层尺寸/DPI 变化时更新（必要时重建交换链）。</summary>
        void UpdateTarget(in WetInkTargetSnapshot target);

        /// <summary>应用一批命令并按需呈现一帧。返回本帧已退休的会话 id。</summary>
        IReadOnlyList<long> Apply(List<WetInkCommand> commands);

        /// <summary>设备是否就绪；false 表示上层应回退到旧管线。</summary>
        bool IsDeviceReady { get; }
    }
}