using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// 为**单个窗口**设置 Windows 的「应用标识」（AppUserModelID）与任务栏元数据。
    /// <para>
    /// 背景：任务栏按 AppUserModelID 归组。<c>SetCurrentProcessExplicitAppUserModelID</c>
    /// 是进程级的，所以进程内所有顶层窗口默认挤在同一个任务栏按钮下、共用同一个图标与缩略图列表。
    /// 内置图片查看器需要与其它 ICC 窗口（主界面、设置窗口…）<b>分开</b>，
    /// 于是改用窗口级属性存储 <c>PKEY_AppUserModel_ID</c>。
    /// </para>
    /// <para>
    /// 顺带写入「固定到任务栏」用的名称与图标（<c>RelaunchDisplayNameResource</c> /
    /// <c>RelaunchIconResource</c>），否则固定之后任务栏上会退化成进程默认的图标与名字。
    /// </para>
    /// <para>
    /// COM 互操作复用 <see cref="EdgeGestureUtil"/> 里已有的
    /// <c>IPropertyStore</c>/<c>PropertyKey</c>/<c>PropVariant</c> 声明，不另起一套。
    /// </para>
    /// </summary>
    internal static class WindowAppIdentity
    {
        /// <summary>属性键的格式 GUID（PKEY_AppUserModel_* 共用同一个 fmtid）。</summary>
        private static readonly Guid AppUserModelFormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

        private const uint PidRelaunchIconResource = 3;
        private const uint PidRelaunchDisplayNameResource = 4;
        private const uint PidAppUserModelId = 5;

        private static readonly Guid IIDPropertyStore = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

        /// <summary>
        /// 把窗口标识为「Ink Canvas 图片查看器」：独立任务栏分组 + 独立图标与名称。
        /// 必须在窗口已有 HWND 之后调用（<c>SourceInitialized</c> 之后）。
        /// 幂等，失败只记日志、不影响功能。
        /// </summary>
        /// <param name="window">目标窗口。</param>
        /// <param name="appUserModelId">要写入的应用标识。</param>
        /// <param name="displayName">「固定到任务栏」时显示的名字。</param>
        /// <param name="iconPath">「固定到任务栏」时使用的 .ico 路径，可为 null。</param>
        public static void Apply(Window window, string appUserModelId, string displayName, string iconPath)
        {
            if (window == null) return;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    LogHelper.WriteLogToFile("设置窗口应用标识失败: 窗口尚无 HWND", LogHelper.LogType.Warning);
                    return;
                }

                var store = OpenPropertyStore(hwnd);
                if (store == null) return;

                try
                {
                    SetString(store, PidAppUserModelId, appUserModelId);

                    if (!string.IsNullOrWhiteSpace(displayName))
                        SetString(store, PidRelaunchDisplayNameResource, displayName);

                    // RelaunchIconResource 要求 "路径,索引"；文件不存在时不要写，否则固定会失败。
                    if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                        SetString(store, PidRelaunchIconResource, iconPath + ",0");

                    store.Commit();

                    // 读回校验：写入失败不影响功能，但任务栏就会退回与其它 ICC 窗口同组，
                    // 这类「静默不生效」必须留下可诊断的痕迹。
                    string readBack = TryReadString(store, PidAppUserModelId);
                    if (!string.Equals(readBack, appUserModelId, StringComparison.Ordinal))
                    {
                        LogHelper.WriteLogToFile(
                            $"窗口应用标识未生效（读回 \"{readBack ?? "<空>"}\"，期望 \"{appUserModelId}\"）",
                            LogHelper.LogType.Warning);
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(store);
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"设置窗口应用标识失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        private static unsafe EdgeGestureUtil.IPropertyStore OpenPropertyStore(IntPtr hwnd)
        {
            fixed (Guid* iid = &IIDPropertyStore)
            {
                var hr = PInvoke.SHGetPropertyStoreForWindow(new HWND(hwnd), iid, out object store);
                if (hr.Succeeded && store is EdgeGestureUtil.IPropertyStore propertyStore)
                    return propertyStore;

                LogHelper.WriteLogToFile(
                    $"打开窗口属性存储失败: HRESULT=0x{hr.Value:X8}", LogHelper.LogType.Warning);
            }

            return null;
        }

        /// <summary>读回一个 VT_LPWSTR 属性值（读回失败返回 null）。</summary>
        private static string TryReadString(EdgeGestureUtil.IPropertyStore store, uint pid)
        {
            var key = new EdgeGestureUtil.PropertyKey(AppUserModelFormatId, pid);
            var variant = new EdgeGestureUtil.PropVariant();

            try
            {
                store.GetValue(ref key, ref variant);
                return variant.vt == (short)VarEnum.VT_LPWSTR && variant.pwszVal != IntPtr.Zero
                    ? Marshal.PtrToStringUni(variant.pwszVal)
                    : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                return null;
            }
        }

        /// <summary>写入一个 VT_LPWSTR 属性值。</summary>
        private static void SetString(EdgeGestureUtil.IPropertyStore store, uint pid, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            var key = new EdgeGestureUtil.PropertyKey(AppUserModelFormatId, pid);
            var variant = new EdgeGestureUtil.PropVariant
            {
                vt = (short)VarEnum.VT_LPWSTR,
                pwszVal = Marshal.StringToCoTaskMemUni(value),
            };

            try
            {
                store.SetValue(ref key, ref variant);
            }
            finally
            {
                // 属性存储会把值拷走，这里负责释放我们自己分配的那份字符串。
                Marshal.FreeCoTaskMem(variant.pwszVal);
                variant.pwszVal = IntPtr.Zero;
            }
        }
    }
}
