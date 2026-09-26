using Ink_Canvas.Properties;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// 文件关联管理器，用于注册和处理.icstk文件的关联
    /// </summary>
    public static class FileAssociationManager
    {
        private const string FileExtension = ".icstk";
        private const string FileTypeName = "InkCanvasStrokesFile";
        private const string AppName = "Ink Canvas";
        private const string AppDescription = "Ink Canvas Strokes File";

        // IPC相关常量
        private const string IpcMutexName = "InkCanvasFileAssociationIpc";
        private const string IpcEventName = "InkCanvasFileAssociationEvent";
        private const string IpcFilePrefix = "InkCanvasFileAssociation_";
        private const string IpcBoardModePrefix = "InkCanvasBoardMode_";
        private const string IpcShowModePrefix = "InkCanvasShowMode_";
        private const string IpcUriCommandPrefix = "InkCanvasUriCommand_";
        // 插件文件打开（文件关联双击）的 IPC 前缀
        private const string IpcPluginFileOpenPrefix = "InkCanvasPluginFileOpen_";

        /// <summary>插件文件关联在 HKCU\Software\Classes\.ext 扩展名键下记录的归属插件 ID 命名值。</summary>
        private const string PluginIdRegistryValueName = "InkCanvasPluginId";

        private const int IpcTimeout = 5000; // 5秒超时

        // ---------------- 内置图片查看器（文件关联）相关的常量 ----------------

        /// <summary>
        /// 内置图片查看器所有图片格式共用的 ProgId。多个扩展名指向同一个 ProgId 是合法且常见的做法，
        /// 打开命令统一带 <see cref="ImageViewerCommandLineSwitch"/> 开关，宿主据此分流到图片查看器。
        /// </summary>
        public const string ImageViewerProgId = "InkCanvasImageViewer";

        /// <summary>
        /// 双击图片时交给宿主的命令行开关。注册表写入形如 <c>"InkCanvasForClass.exe" -image "%1"</c>。
        /// </summary>
        public const string ImageViewerCommandLineSwitch = "-image";

        /// <summary>
        /// 图片查看器的 AppUserModelID —— 单独一套，与主程序（<c>InkCanvasForClass.CE</c>）分开：
        /// 任务栏把查看器与其它 ICC 窗口（例如设置窗口）分别归组，各自有各自的图标与预览。
        /// </summary>
        public const string ImageViewerAppUserModelId = "InkCanvasForClass.CE.ImageViewer";

        /// <summary>图片查看器文件关联的 IPC 文件前缀（已运行实例接收图片路径用）。</summary>
        private const string IpcImageViewerFilePrefix = "InkCanvasImageViewerFile_";

        /// <summary>
        /// 图片查看器的展示名（"Ink Canvas 图片查看器"）。
        /// 用于资源管理器「打开方式」列表里显示的应用名。
        /// </summary>
        public static string ImageViewerAppDisplayName
            => Ink_Canvas.Windows.SettingsViews.Helpers.UriSchemeShortcutHelper.ImageViewerDisplayName;

        /// <summary>
        /// 图片查看器专属图标（.ico）路径 —— 与「创建快捷方式」用的是同一个图标生成器，
        /// 只是程序内图标换成图片查看器图标。生成失败返回 null，调用方回退到主程序 exe 图标。
        /// <para>
        /// 进程内只生成一次：一次「写入关联」要为每个绑定格式各写一遍注册表，
        /// 不缓存就会把同一个文件反复重绘、重写十几遍。
        /// </para>
        /// </summary>
        public static string GetImageViewerIconPath()
        {
            if (_imageViewerIconResolved) return _cachedImageViewerIconPath;
            _imageViewerIconResolved = true;

            try
            {
                _cachedImageViewerIconPath = Ink_Canvas.Windows.SettingsViews.Helpers.UriSchemeShortcutHelper.EnsureImageViewerIconFile();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"生成图片查看器图标失败: {ex.Message}", LogHelper.LogType.Warning);
                _cachedImageViewerIconPath = null;
            }

            return _cachedImageViewerIconPath;
        }

        private static string _cachedImageViewerIconPath;
        private static bool _imageViewerIconResolved;

        /// <summary>DefaultIcon 的注册表值：有专属图标就用它，否则退回 exe 图标。</summary>
        private static string BuildImageViewerIconValue(string exePath, string iconPath)
            => string.IsNullOrEmpty(iconPath) ? $"\"{exePath}\",0" : $"\"{iconPath}\",0";

        /// <summary>
        /// 注册.icstk文件关联
        /// </summary>
        public static bool RegisterFileAssociation()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;

                // 注册文件类型
                using (RegistryKey fileTypeKey = Registry.ClassesRoot.CreateSubKey(FileTypeName))
                {
                    fileTypeKey.SetValue("", AppDescription);
                    fileTypeKey.SetValue("FriendlyTypeName", AppDescription);

                    // 设置默认图标
                    using (RegistryKey defaultIconKey = fileTypeKey.CreateSubKey("DefaultIcon"))
                    {
                        defaultIconKey.SetValue("", $"\"{exePath}\",0");
                    }

                    // 设置打开命令
                    using (RegistryKey shellKey = fileTypeKey.CreateSubKey("shell"))
                    using (RegistryKey openKey = shellKey.CreateSubKey("open"))
                    using (RegistryKey commandKey = openKey.CreateSubKey("command"))
                    {
                        commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
                    }
                }

                // 注册文件扩展名
                using (RegistryKey extensionKey = Registry.ClassesRoot.CreateSubKey(FileExtension))
                {
                    extensionKey.SetValue("", FileTypeName);
                }

                // 刷新系统文件关联缓存
                RefreshSystemFileAssociations();

                LogHelper.WriteLogToFile($"成功注册{FileExtension}文件关联", LogHelper.LogType.Event);
                return true;
            }
            catch (SecurityException ex)
            {
                LogHelper.WriteLogToFile($"注册文件关联时权限不足: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                LogHelper.WriteLogToFile($"注册文件关联时访问被拒绝: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"注册文件关联时出错: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 注销.icstk文件关联
        /// </summary>
        public static bool UnregisterFileAssociation()
        {
            try
            {
                // 删除文件扩展名关联
                Registry.ClassesRoot.DeleteSubKeyTree(FileExtension, false);

                // 删除文件类型定义
                Registry.ClassesRoot.DeleteSubKeyTree(FileTypeName, false);

                // 刷新系统文件关联缓存
                RefreshSystemFileAssociations();

                LogHelper.WriteLogToFile($"成功注销{FileExtension}文件关联", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"注销文件关联时出错: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 检查文件关联是否已注册
        /// </summary>
        public static bool IsFileAssociationRegistered()
        {
            try
            {
                using (RegistryKey extensionKey = Registry.ClassesRoot.OpenSubKey(FileExtension))
                {
                    if (extensionKey == null) return false;

                    string fileType = extensionKey.GetValue("") as string;
                    if (string.IsNullOrEmpty(fileType)) return false;

                    using (RegistryKey fileTypeKey = Registry.ClassesRoot.OpenSubKey(fileType))
                    {
                        if (fileTypeKey == null) return false;

                        using (RegistryKey shellKey = fileTypeKey.OpenSubKey("shell\\open\\command"))
                        {
                            if (shellKey == null) return false;

                            string command = shellKey.GetValue("") as string;
                            if (string.IsNullOrEmpty(command)) return false;

                            // 检查命令是否指向当前应用程序
                            string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                            return command.Contains(currentExePath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"检查文件关联状态时出错: {ex.Message}", LogHelper.LogType.Error);
            }

            return false;
        }

        /// <summary>
        /// 显示文件关联状态
        /// </summary>
        public static void ShowFileAssociationStatus()
        {
            bool isRegistered = IsFileAssociationRegistered();
            LogHelper.WriteLogToFile($"{FileExtension}文件关联状态: {(isRegistered ? "已注册" : "未注册")}", LogHelper.LogType.Event);
        }

        #region 插件自定义扩展名关联

        // 禁止插件覆盖的系统关键/可执行类型，避免插件注册恶意或危险的文件关联
        private static readonly string[] ProtectedExtensions =
        {
            ".exe", ".dll", ".bat", ".cmd", ".com", ".msi", ".msp", ".ps1", ".vbs", ".vbe",
            ".js", ".jse", ".wsf", ".wsh", ".scr", ".reg", ".sys", ".inf", ".jar", ".lnk",
            ".url", ".pif", ".hta", ".cpl", ".sh", ".bash", ".ico", ".ocx", ".drv",
            ".docm", ".xlsm", ".pptm",
            ".icstk" // 宿主自身的文件类型，不允许插件覆盖
        };

        private static bool IsProtectedExtension(string extension)
        {
            return Array.IndexOf(ProtectedExtensions, extension) >= 0;
        }

        private static string NormalizeExtension(string extension)
        {
            string ext = extension.Trim().ToLowerInvariant();
            if (!ext.StartsWith(".")) ext = "." + ext;
            return ext;
        }

        /// <summary>
        /// 注册自定义文件扩展名关联（供插件使用）。
        /// 写入 <c>HKCU\Software\Classes</c>（无需管理员权限），把该扩展名的打开命令指向宿主 exe。
        /// <paramref name="pluginId"/> 非空时在扩展名键下写入 <see cref="PluginIdRegistryValueName"/>，
        /// 使宿主在双击打开该扩展名文件时能把路径派发回对应插件。
        /// </summary>
        /// <returns>是否注册成功；扩展名/ProgId 无效或扩展名受保护时返回 false。</returns>
        public static bool RegisterFileAssociation(string extension, string progId, string description, string iconPath = null, string pluginId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(progId)) return false;

                string normalizedExtension = NormalizeExtension(extension);
                if (IsProtectedExtension(normalizedExtension)) return false;

                string normalizedProgId = progId.Trim();
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                string normalizedDescription = description ?? "";

                // 注册文件类型
                using (RegistryKey fileTypeKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + normalizedProgId))
                {
                    fileTypeKey.SetValue("", normalizedDescription);
                    fileTypeKey.SetValue("FriendlyTypeName", normalizedDescription);

                    // 设置默认图标
                    using (RegistryKey defaultIconKey = fileTypeKey.CreateSubKey("DefaultIcon"))
                    {
                        string iconValue = string.IsNullOrEmpty(iconPath)
                            ? $"\"{exePath}\",0"
                            : $"\"{iconPath}\",0";
                        defaultIconKey.SetValue("", iconValue);
                    }

                    // 设置打开命令：打开该扩展名文件时启动宿主 exe 并传入文件路径
                    using (RegistryKey shellKey = fileTypeKey.CreateSubKey("shell"))
                    using (RegistryKey openKey = shellKey.CreateSubKey("open"))
                    using (RegistryKey commandKey = openKey.CreateSubKey("command"))
                    {
                        commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
                    }
                }

                // 注册文件扩展名
                using (RegistryKey extensionKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + normalizedExtension))
                {
                    extensionKey.SetValue("", normalizedProgId);
                    // 记录归属插件：宿主要据此把该扩展名文件派发回对应插件
                    if (!string.IsNullOrWhiteSpace(pluginId))
                        extensionKey.SetValue(PluginIdRegistryValueName, pluginId.Trim());
                }

                // 刷新系统文件关联缓存
                RefreshSystemFileAssociations();

                LogHelper.WriteLogToFile($"成功注册{normalizedExtension}文件关联（ProgId: {normalizedProgId}）", LogHelper.LogType.Event);
                return true;
            }
            catch (SecurityException ex)
            {
                LogHelper.WriteLogToFile($"注册文件关联时权限不足: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                LogHelper.WriteLogToFile($"注册文件关联时访问被拒绝: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"注册文件关联时出错: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 注销自定义文件扩展名关联（供插件使用）。
        /// 自动读取扩展名当前指向的 ProgId 并一并清理，无需调用方提供。
        /// </summary>
        /// <returns>是否注销成功。</returns>
        public static bool UnregisterFileAssociation(string extension)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(extension)) return false;

                string normalizedExtension = NormalizeExtension(extension);
                if (IsProtectedExtension(normalizedExtension)) return false;

                // 先读取扩展名当前指向的 ProgId，用于清理文件类型定义
                string linkedProgId = null;
                using (RegistryKey extensionKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + normalizedExtension))
                {
                    linkedProgId = extensionKey?.GetValue("") as string;
                }

                // 删除文件扩展名关联
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + normalizedExtension, false);

                // 删除文件类型定义（排除宿主自身的 InkCanvasStrokesFile）
                if (!string.IsNullOrWhiteSpace(linkedProgId)
                    && !string.Equals(linkedProgId, FileTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + linkedProgId, false);
                }

                // 刷新系统文件关联缓存
                RefreshSystemFileAssociations();

                LogHelper.WriteLogToFile($"成功注销{normalizedExtension}文件关联", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"注销文件关联时出错: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 检查自定义文件扩展名关联是否已注册且指向宿主 exe（供插件使用）。
        /// </summary>
        /// <param name="progId">可选的 ProgId；非空时校验扩展名指向的 ProgId 是否与之匹配。</param>
        /// <returns>已注册且命令指向宿主 exe 时返回 true。</returns>
        public static bool IsFileAssociationRegistered(string extension, string progId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(extension)) return false;

                string normalizedExtension = NormalizeExtension(extension);

                using (RegistryKey extensionKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + normalizedExtension))
                {
                    if (extensionKey == null) return false;

                    string fileType = extensionKey.GetValue("") as string;
                    if (string.IsNullOrEmpty(fileType)) return false;

                    // 指定了 ProgId 时校验是否一致
                    if (!string.IsNullOrWhiteSpace(progId)
                        && !string.Equals(fileType, progId.Trim(), StringComparison.OrdinalIgnoreCase))
                        return false;

                    using (RegistryKey shellKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + fileType + @"\shell\open\command"))
                    {
                        if (shellKey == null) return false;

                        string command = shellKey.GetValue("") as string;
                        if (string.IsNullOrEmpty(command)) return false;

                        // 检查命令是否指向当前应用程序
                        string currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                        return command.Contains(currentExePath);
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"检查文件关联状态时出错: {ex.Message}", LogHelper.LogType.Error);
            }

            return false;
        }

        #endregion

        #region 内置图片查看器文件关联

        /// <summary>当前进程的可执行文件全路径。</summary>
        private static string CurrentExecutablePath()
            => Process.GetCurrentProcess().MainModule.FileName;

        /// <summary>当前进程的可执行文件名（用于 <c>HKCU\Software\Classes\Applications\&lt;exe&gt;</c> 键）。</summary>
        private static string CurrentExecutableFileName()
            => Path.GetFileName(CurrentExecutablePath());

        /// <summary><c>HKCU\Software\Classes\Applications\&lt;exe&gt;</c> 键的完整路径。</summary>
        private static string ImageViewerApplicationKeyPath
            => @"Software\Classes\Applications\" + CurrentExecutableFileName();

        /// <summary>
        /// 登记「本程序可以被用来打开图片」这一事实（不针对具体扩展名）：
        /// <list type="bullet">
        /// <item><c>HKCU\Software\Classes\Applications\&lt;exe&gt;</c> —— 让本程序出现在
        /// 「打开方式 → 选择其他应用」列表中；</item>
        /// <item><c>shell\open\command</c> 带 <see cref="ImageViewerCommandLineSwitch"/> 开关，
        /// 双击图片时宿主能直接分流到图片查看器；</item>
        /// <item><c>SupportedTypes</c> —— 声明本程序支持的扩展名集合（Windows 据此在
        /// 文件属性「打开方式」里提供本程序）。</item>
        /// </list>
        /// 全部写入 HKCU，无需管理员权限。
        /// </summary>
        /// <param name="extensions">要声明的扩展名集合（可带或不带点）。</param>
        /// <returns>是否写入成功。</returns>
        public static bool RegisterImageViewerApplication(IEnumerable<string> extensions)
        {
            try
            {
                string exePath = CurrentExecutablePath();
                string iconValue = BuildImageViewerIconValue(exePath, GetImageViewerIconPath());

                using (RegistryKey appKey = Registry.CurrentUser.CreateSubKey(ImageViewerApplicationKeyPath))
                {
                    if (appKey == null) return false;

                    // FriendlyAppName 就是资源管理器「打开方式」列表里显示的名字。
                    appKey.SetValue("FriendlyAppName", ImageViewerAppDisplayName);
                    appKey.SetValue("ApplicationName", ImageViewerAppDisplayName);
                    appKey.SetValue("ApplicationIcon", iconValue);
                    appKey.SetValue("AppUserModelID", ImageViewerAppUserModelId);

                    using (RegistryKey iconKey = appKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey?.SetValue("", iconValue);
                    }

                    using (RegistryKey commandKey = appKey.CreateSubKey(@"shell\open\command"))
                    {
                        commandKey?.SetValue("", $"\"{exePath}\" {ImageViewerCommandLineSwitch} \"%1\"");
                    }

                    using (RegistryKey supportedKey = appKey.CreateSubKey("SupportedTypes"))
                    {
                        if (supportedKey != null)
                        {
                            foreach (var raw in extensions ?? Enumerable.Empty<string>())
                            {
                                string extension = NormalizeExtension(raw);
                                if (extension.Length <= 1) continue;
                                supportedKey.SetValue(extension, string.Empty, RegistryValueKind.String);
                            }
                        }
                    }
                }

                LogHelper.WriteLogToFile(
                    $"已登记图片查看器应用条目（{CurrentExecutableFileName()}）", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"登记图片查看器应用条目失败: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 为一个图片扩展名注册「打开方式」关联：
        /// <list type="number">
        /// <item>写 <c>HKCU\Software\Classes\&lt;ProgId&gt;</c> 文件类型定义（描述 / 图标 / 打开命令）；</item>
        /// <item>把 ProgId 追加到 <c>HKCU\Software\Classes\&lt;.ext&gt;\OpenWithProgids</c>，
        /// 使其出现在右键「打开方式」列表中。</item>
        /// </list>
        /// <b>不</b>改写扩展名的默认处理程序 —— Windows 的默认应用由 <c>UserChoice</c> 保护，
        /// 程序无法静默修改；需要设为默认时由 <see cref="OpenDefaultAppsSettings"/> 引导用户去系统设置确认。
        /// </summary>
        /// <param name="extension">图片扩展名（可带或不带点）。</param>
        /// <param name="displayName">文件类型显示名（用于 FriendlyTypeName），可为空。</param>
        /// <returns>是否注册成功。</returns>
        public static bool RegisterImageViewerAssociation(string extension, string displayName = null)
        {
            try
            {
                string ext = NormalizeExtension(extension);
                if (ext.Length <= 1) return false;
                if (IsProtectedExtension(ext)) return false;

                string exePath = CurrentExecutablePath();
                string iconValue = BuildImageViewerIconValue(exePath, GetImageViewerIconPath());
                string description = string.IsNullOrWhiteSpace(displayName)
                    ? $"{ext.TrimStart('.').ToUpperInvariant()} 图片"
                    : displayName.Trim();

                using (RegistryKey fileTypeKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ImageViewerProgId))
                {
                    if (fileTypeKey == null) return false;

                    fileTypeKey.SetValue("", description);
                    fileTypeKey.SetValue("FriendlyTypeName", description);

                    using (RegistryKey iconKey = fileTypeKey.CreateSubKey("DefaultIcon"))
                    {
                        iconKey?.SetValue("", iconValue);
                    }

                    using (RegistryKey commandKey = fileTypeKey.CreateSubKey(@"shell\open\command"))
                    {
                        commandKey?.SetValue("", $"\"{exePath}\" {ImageViewerCommandLineSwitch} \"%1\"");
                    }

                    // 宿主自身处理该扩展名，不归属任何插件：清掉可能残留的插件归属标记。
                    using (RegistryKey extKey0 = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext, true))
                    {
                        extKey0?.DeleteValue(PluginIdRegistryValueName, false);
                    }
                }

                using (RegistryKey extensionKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext))
                {
                    if (extensionKey == null) return false;

                    using (RegistryKey openWithKey = extensionKey.CreateSubKey("OpenWithProgids"))
                    {
                        // REG_NONE + 空数据是「出现在打开方式列表」的标准写法。
                        openWithKey?.SetValue(ImageViewerProgId, new byte[0], RegistryValueKind.None);
                    }
                }

                LogHelper.WriteLogToFile($"已注册图片格式关联: {ext}（ProgId: {ImageViewerProgId}）", LogHelper.LogType.Event);
                return true;
            }
            catch (SecurityException ex)
            {
                LogHelper.WriteLogToFile($"注册图片格式关联时权限不足（{extension}）: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"注册图片格式关联失败（{extension}）: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 注销一个图片扩展名的「打开方式」关联：
        /// 从 <c>OpenWithProgids</c> 移除本程序，并在没有任何扩展名再引用该 ProgId 时删除文件类型定义。
        /// 不会触碰其它程序写入的关联。
        /// </summary>
        /// <param name="extension">图片扩展名（可带或不带点）。</param>
        /// <param name="deleteProgIdIfUnused">为 true 时在无人引用后删除 ProgId 键。</param>
        /// <returns>是否注销成功。</returns>
        public static bool UnregisterImageViewerAssociation(string extension, bool deleteProgIdIfUnused = true)
        {
            try
            {
                string ext = NormalizeExtension(extension);
                if (ext.Length <= 1) return false;

                using (RegistryKey extensionKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext, true))
                {
                    using (RegistryKey openWithKey = extensionKey?.OpenSubKey("OpenWithProgids", true))
                    {
                        openWithKey?.DeleteValue(ImageViewerProgId, false);
                    }
                }

                if (deleteProgIdIfUnused && !AnyOpenWithProgidsReference(ImageViewerProgId))
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ImageViewerProgId, false);
                }

                LogHelper.WriteLogToFile($"已注销图片格式关联: {ext}", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"注销图片格式关联失败（{extension}）: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>移除本程序写入的全部图片查看器关联（ProgId + Applications 条目）。</summary>
        /// <param name="extensions">要清理的扩展名集合。</param>
        public static void RemoveAllImageViewerAssociations(IEnumerable<string> extensions)
        {
            foreach (var raw in (extensions ?? Enumerable.Empty<string>()).ToList())
            {
                UnregisterImageViewerAssociation(raw, deleteProgIdIfUnused: false);
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ImageViewerProgId, false);
                Registry.CurrentUser.DeleteSubKeyTree(ImageViewerApplicationKeyPath, false);
                LogHelper.WriteLogToFile("已清除本程序写入的图片查看器关联", LogHelper.LogType.Event);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"清除图片查看器关联失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>某个 ProgId 是否仍被任何扩展名的 <c>OpenWithProgids</c> 引用。</summary>
        private static bool AnyOpenWithProgidsReference(string progId)
        {
            try
            {
                using (RegistryKey classesKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes"))
                {
                    if (classesKey == null) return false;

                    foreach (var subKeyName in classesKey.GetSubKeyNames())
                    {
                        if (!subKeyName.StartsWith(".", StringComparison.Ordinal)) continue;

                        using (RegistryKey openWithKey = classesKey.OpenSubKey(subKeyName + @"\OpenWithProgids"))
                        {
                            if (openWithKey?.GetValue(progId) != null) return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"检查 ProgId 引用失败: {ex.Message}", LogHelper.LogType.Warning);
            }

            return false;
        }

        /// <summary>
        /// 检查某图片扩展名是否已由本程序写入「打开方式」关联，且命令确实指向当前 exe。
        /// </summary>
        /// <param name="extension">图片扩展名（可带或不带点）。</param>
        /// <returns>已注册且指向本进程时返回 true。</returns>
        public static bool IsImageViewerAssociationRegistered(string extension)
        {
            try
            {
                string ext = NormalizeExtension(extension);
                if (ext.Length <= 1) return false;

                using (RegistryKey openWithKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids"))
                {
                    if (openWithKey?.GetValue(ImageViewerProgId) == null) return false;
                }

                using (RegistryKey commandKey = Registry.CurrentUser.OpenSubKey(
                           @"Software\Classes\" + ImageViewerProgId + @"\shell\open\command"))
                {
                    string command = commandKey?.GetValue("") as string;
                    if (string.IsNullOrEmpty(command)) return false;

                    string exePath = CurrentExecutablePath();
                    return command.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0
                           && command.IndexOf(ImageViewerCommandLineSwitch, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"检查图片格式关联状态失败（{extension}）: {ex.Message}", LogHelper.LogType.Warning);
                return false;
            }
        }

        /// <summary>
        /// 尽力把某扩展名的「当前用户默认处理程序」指向本程序。
        /// <para>
        /// Windows 10/11 的默认应用由 <c>FileExts\.ext\UserChoice</c>（带哈希保护）决定，
        /// 程序无法合法改写；本方法只在<b>不存在</b> <c>UserChoice</c> 时写
        /// <c>HKCU\Software\Classes\.ext</c> 的默认值。返回 false 表示需要用户到系统设置里手动确认，
        /// 调用方应随后调用 <see cref="OpenDefaultAppsSettings"/>。
        /// </para>
        /// </summary>
        /// <param name="extension">图片扩展名（可带或不带点）。</param>
        /// <returns>是否已把 UserChoice 之外的默认值写为本程序。</returns>
        public static bool TrySetUserDefaultProgId(string extension)
        {
            try
            {
                string ext = NormalizeExtension(extension);
                if (ext.Length <= 1) return false;

                using (RegistryKey userChoice = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice"))
                {
                    if (userChoice?.GetValue("ProgId") != null)
                    {
                        LogHelper.WriteLogToFile(
                            $"{ext} 已被 UserChoice 指定默认程序，无法程序化改写", LogHelper.LogType.Event);
                        return false;
                    }
                }

                using (RegistryKey extensionKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext))
                {
                    extensionKey?.SetValue("", ImageViewerProgId);
                }

                RefreshSystemFileAssociations();
                LogHelper.WriteLogToFile($"{ext} 的默认处理程序已指向 {ImageViewerProgId}", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"设置 {extension} 默认处理程序失败: {ex.Message}", LogHelper.LogType.Warning);
                return false;
            }
        }

        /// <summary>打开系统「默认应用」设置页，让用户把图片格式指定给本程序。</summary>
        /// <returns>是否成功拉起设置页。</returns>
        public static bool OpenDefaultAppsSettings()
        {
            // Win11 先尝试直接跳到本应用的默认值设置；失败再退回到通用页。
            string[] targets = { "ms-settings:defaultapps?registeredAppUser=" + ImageViewerAppDisplayName, "ms-settings:defaultapps" };
            foreach (var target in targets)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    LogHelper.WriteLogToFile($"已打开系统默认应用设置: {target}", LogHelper.LogType.Event);
                    return true;
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"打开默认应用设置失败（{target}）: {ex.Message}", LogHelper.LogType.Warning);
                }
            }

            return false;
        }

        /// <summary>
        /// 按扩展名列表整体写入 / 撤销图片查看器关联。
        /// </summary>
        /// <param name="extensions">要注册的扩展名集合。</param>
        /// <param name="displayNameLookup">扩展名 → 显示名的映射，可为 null。</param>
        /// <returns>成功注册的扩展名数量与失败数量。</returns>
        public static (int Succeeded, int Failed) ApplyImageViewerAssociations(
            IEnumerable<string> extensions, Func<string, string> displayNameLookup = null)
        {
            var list = (extensions ?? Enumerable.Empty<string>())
                .Select(NormalizeExtension)
                .Where(e => e.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int ok = 0, failed = 0;
            RegisterImageViewerApplication(list);

            foreach (var ext in list)
            {
                string displayName = null;
                try { displayName = displayNameLookup?.Invoke(ext); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

                if (RegisterImageViewerAssociation(ext, displayName)) ok++;
                else failed++;
            }

            RefreshSystemFileAssociations();
            return (ok, failed);
        }

        /// <summary>
        /// 从命令行参数中取出「应由内置图片查看器打开的图片路径」。
        /// <para>
        /// 判定与顺序无关：只要某个参数是现存的图片文件，且其扩展名落在
        /// <paramref name="settings"/> 已绑定的格式内，就返回它。这样无论注册表把图片写成
        /// <c>-image "x.png"</c>、单独 <c>"x.png"</c>，还是被拖到 exe 上都能识别。
        /// </para>
        /// </summary>
        /// <param name="args">命令行参数。</param>
        /// <param name="settings">图片查看器设置（用于取已绑定扩展名）。</param>
        /// <returns>找到的图片路径；没有则返回 null。</returns>
        public static string GetImageViewerFileFromArgs(string[] args, Ink_Canvas.ImageViewerSettings settings)
        {
            if (args == null || args.Length == 0) return null;

            foreach (string arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                if (arg.StartsWith("-", StringComparison.Ordinal) || arg.StartsWith("/", StringComparison.Ordinal)) continue;

                if (ImageViewerFormats.ShouldOpenInViewer(arg, settings))
                {
                    LogHelper.WriteLogToFile($"从命令行参数中找到图片: {arg}", LogHelper.LogType.Event);
                    return arg;
                }
            }

            return null;
        }

        /// <summary>
        /// 尝试通过 IPC 把图片路径交给已运行的实例打开（多实例：由该实例再决定新开还是复用窗口）。
        /// </summary>
        /// <param name="filePath">图片路径。</param>
        /// <returns>是否成功发送。</returns>
        public static bool TrySendImageViewerFileToExistingInstance(string filePath)
        {
            try
            {
                LogHelper.WriteLogToFile($"尝试通过IPC发送图片路径给已运行实例: {filePath}", LogHelper.LogType.Event);

                string tempDir = Path.GetTempPath();
                string ipcFilePath = Path.Combine(tempDir, IpcImageViewerFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(ipcFilePath, filePath, Encoding.UTF8);

                using (EventWaitHandle ipcEvent = new EventWaitHandle(false, EventResetMode.ManualReset, IpcEventName))
                {
                    ipcEvent.Set();
                }

                Thread.Sleep(1000);

                try
                {
                    if (File.Exists(ipcFilePath)) File.Delete(ipcFilePath);
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"清理图片 IPC 文件失败: {ex.Message}", LogHelper.LogType.Warning);
                }

                LogHelper.WriteLogToFile("图片 IPC 路径发送完成", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"通过IPC发送图片路径失败: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        #endregion

        /// <summary>
        /// 刷新系统文件关联缓存
        /// </summary>
        private static void RefreshSystemFileAssociations()
        {
            try
            {
                // 通知系统文件关联已更改
                unsafe { PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, 0, null, null); }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"刷新文件关联缓存时出错: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 处理命令行参数中的文件路径
        /// </summary>
        /// <param name="args">命令行参数</param>
        /// <returns>找到的.icstk文件路径，如果没有找到则返回null</returns>
        public static string GetIcstkFileFromArgs(string[] args)
        {
            if (args == null || args.Length == 0) return null;

            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg)) continue;

                // 检查是否为.icstk文件
                if (Path.GetExtension(arg).Equals(FileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    // 检查文件是否存在
                    if (File.Exists(arg))
                    {
                        LogHelper.WriteLogToFile($"从命令行参数中找到.icstk文件: {arg}", LogHelper.LogType.Event);
                        return arg;
                    }
                    else
                    {
                        LogHelper.WriteLogToFile($"命令行参数中的.icstk文件不存在: {arg}", LogHelper.LogType.Warning);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 读取某扩展名注册的归属插件 ID（插件文件关联用）。
        /// 从 <c>HKCU\Software\Classes\.ext</c> 扩展名键的 <see cref="PluginIdRegistryValueName"/> 命名值读取。
        /// </summary>
        /// <param name="extension">文件扩展名（可带或不带点，忽略大小写）。</param>
        /// <returns>归属插件 ID；扩展名无效/受保护/未记录时返回 null。</returns>
        public static string GetPluginIdForExtension(string extension)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(extension)) return null;

                string normalizedExtension = NormalizeExtension(extension);
                if (IsProtectedExtension(normalizedExtension)) return null;

                using (RegistryKey extensionKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + normalizedExtension))
                {
                    if (extensionKey == null) return null;
                    string pluginId = extensionKey.GetValue(PluginIdRegistryValueName) as string;
                    return string.IsNullOrWhiteSpace(pluginId) ? null : pluginId.Trim();
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"读取扩展名归属插件失败: {ex.Message}", LogHelper.LogType.Warning);
                return null;
            }
        }

        /// <summary>
        /// 处理命令行参数中「已注册插件文件关联」的文件路径（文件关联打开）。
        /// 只有扩展名能映射到某个插件（注册表记录了归属插件 ID）的现存文件才会被识别。
        /// </summary>
        /// <param name="args">命令行参数</param>
        /// <returns>找到的插件文件路径；没有则返回 null。</returns>
        public static string GetPluginFileFromArgs(string[] args)
        {
            if (args == null || args.Length == 0) return null;

            foreach (string arg in args)
            {
                if (string.IsNullOrEmpty(arg)) continue;

                string extension = Path.GetExtension(arg);
                if (string.IsNullOrEmpty(extension)) continue;

                if (string.IsNullOrEmpty(GetPluginIdForExtension(extension))) continue;

                if (File.Exists(arg))
                {
                    LogHelper.WriteLogToFile($"从命令行参数中找到插件关联文件: {arg}", LogHelper.LogType.Event);
                    return arg;
                }

                LogHelper.WriteLogToFile($"命令行参数中的插件关联文件不存在: {arg}", LogHelper.LogType.Warning);
            }

            return null;
        }

        /// <summary>
        /// 把一个插件关联文件（双击打开）派发给注册该扩展名的插件。
        /// 构造 <c>icc://plugin/&lt;pluginId&gt;/open?path=&lt;urlencoded&gt;</c> 直接 <see cref="Plugins.PluginManager.TryDispatchUri"/>，
        /// 不走 <c>MainWindow.HandleUriCommand</c>，避免受「启用 URI 协议」设置门禁。
        /// </summary>
        /// <param name="filePath">要打开的文件路径。</param>
        /// <returns>是否已派发（插件未注册处理器或未加载返回 false）。</returns>
        public static bool DispatchPluginFileOpen(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath)) return false;

                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrEmpty(extension)) return false;

                string pluginId = GetPluginIdForExtension(extension);
                if (string.IsNullOrEmpty(pluginId))
                {
                    LogHelper.WriteLogToFile($"无法派发文件（扩展名 {extension} 未记录归属插件）: {filePath}", LogHelper.LogType.Warning);
                    return false;
                }

                string uri = "icc://plugin/" + pluginId + "/open?path=" + Uri.EscapeDataString(filePath);
                bool handled = Plugins.PluginManager.Instance.TryDispatchUri(pluginId, "open", uri);
                if (handled)
                    LogHelper.WriteLogToFile($"已派发文件给插件 {pluginId}: {filePath}", LogHelper.LogType.Event);
                else
                    LogHelper.WriteLogToFile($"派发文件打开失败（插件 {pluginId} 未注册处理器或未加载）: {uri}", LogHelper.LogType.Warning);
                return handled;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"派发插件文件打开失败: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 尝试通过IPC将文件路径发送给已运行的实例
        /// </summary>
        /// <param name="filePath">要打开的文件路径</param>
        /// <returns>是否成功发送</returns>
        public static bool TrySendFileToExistingInstance(string filePath)
        {
            try
            {
                LogHelper.WriteLogToFile($"尝试通过IPC发送文件路径给已运行实例: {filePath}", LogHelper.LogType.Event);

                // 创建IPC文件
                string tempDir = Path.GetTempPath();
                string ipcFileName = IpcFilePrefix + Guid.NewGuid().ToString("N") + ".tmp";
                string ipcFilePath = Path.Combine(tempDir, ipcFileName);

                // 写入文件路径到IPC文件
                File.WriteAllText(ipcFilePath, filePath, Encoding.UTF8);

                // 创建事件通知已运行实例
                using (EventWaitHandle ipcEvent = new EventWaitHandle(false, EventResetMode.ManualReset, IpcEventName))
                {
                    ipcEvent.Set();
                }

                // 等待一段时间让已运行实例处理文件
                Thread.Sleep(1000);

                // 清理IPC文件
                try
                {
                    if (File.Exists(ipcFilePath))
                    {
                        File.Delete(ipcFilePath);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"清理IPC文件失败: {ex.Message}", LogHelper.LogType.Warning);
                }

                LogHelper.WriteLogToFile("IPC文件路径发送完成", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"通过IPC发送文件路径失败: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 尝试通过IPC将插件关联文件路径发送给已运行的实例（从文件关联打开）。
        /// 已运行实例收到后在 UI 线程把路径派发给注册该扩展名的插件。
        /// </summary>
        /// <param name="filePath">要打开的插件关联文件路径</param>
        /// <returns>是否成功发送</returns>
        public static bool TrySendPluginFileToExistingInstance(string filePath)
        {
            try
            {
                LogHelper.WriteLogToFile($"尝试通过IPC发送插件关联文件路径给已运行实例: {filePath}", LogHelper.LogType.Event);

                string tempDir = Path.GetTempPath();
                string ipcFileName = IpcPluginFileOpenPrefix + Guid.NewGuid().ToString("N") + ".tmp";
                string ipcFilePath = Path.Combine(tempDir, ipcFileName);

                File.WriteAllText(ipcFilePath, filePath, Encoding.UTF8);

                using (EventWaitHandle ipcEvent = new EventWaitHandle(false, EventResetMode.ManualReset, IpcEventName))
                {
                    ipcEvent.Set();
                }

                Thread.Sleep(1000);

                try
                {
                    if (File.Exists(ipcFilePath))
                    {
                        File.Delete(ipcFilePath);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"清理插件文件 IPC 失败: {ex.Message}", LogHelper.LogType.Warning);
                }

                LogHelper.WriteLogToFile("插件文件 IPC 路径发送完成", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"通过IPC发送插件关联文件路径失败: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 尝试通过IPC将白板模式命令发送给已运行的实例
        /// </summary>
        /// <returns>是否成功发送</returns>
        public static bool TrySendBoardModeCommandToExistingInstance()
        {
            try
            {
                LogHelper.WriteLogToFile("尝试通过IPC发送白板模式命令给已运行实例", LogHelper.LogType.Event);

                // 创建IPC文件
                string tempDir = Path.GetTempPath();
                string ipcFileName = IpcBoardModePrefix + Guid.NewGuid().ToString("N") + ".tmp";
                string ipcFilePath = Path.Combine(tempDir, ipcFileName);

                // 写入白板模式命令到IPC文件
                File.WriteAllText(ipcFilePath, "BOARD_MODE", Encoding.UTF8);

                // 创建事件通知已运行实例
                using (EventWaitHandle ipcEvent = new EventWaitHandle(false, EventResetMode.ManualReset, IpcEventName))
                {
                    ipcEvent.Set();
                }

                // 等待一段时间让已运行实例处理命令
                Thread.Sleep(1000);

                // 清理IPC文件
                try
                {
                    if (File.Exists(ipcFilePath))
                    {
                        File.Delete(ipcFilePath);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"清理IPC文件失败: {ex.Message}", LogHelper.LogType.Warning);
                }

                LogHelper.WriteLogToFile("IPC白板模式命令发送完成", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"通过IPC发送白板模式命令失败: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 尝试通过IPC将展开浮动栏命令发送给已运行的实例
        /// </summary>
        /// <returns>是否成功发送</returns>
        public static bool TrySendShowModeCommandToExistingInstance()
        {
            try
            {
                LogHelper.WriteLogToFile("尝试通过IPC发送展开浮动栏命令给已运行实例", LogHelper.LogType.Event);

                // 创建IPC文件
                string tempDir = Path.GetTempPath();
                string ipcFileName = IpcShowModePrefix + Guid.NewGuid().ToString("N") + ".tmp";
                string ipcFilePath = Path.Combine(tempDir, ipcFileName);

                // 写入展开浮动栏命令到IPC文件
                File.WriteAllText(ipcFilePath, "SHOW_MODE", Encoding.UTF8);

                // 创建事件通知已运行实例
                using (EventWaitHandle ipcEvent = new EventWaitHandle(false, EventResetMode.ManualReset, IpcEventName))
                {
                    ipcEvent.Set();
                }

                // 等待一段时间让已运行实例处理命令
                Thread.Sleep(1000);

                // 清理IPC文件
                try
                {
                    if (File.Exists(ipcFilePath))
                    {
                        File.Delete(ipcFilePath);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"清理IPC文件失败: {ex.Message}", LogHelper.LogType.Warning);
                }

                LogHelper.WriteLogToFile("IPC展开浮动栏命令发送完成", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"通过IPC发送展开浮动栏命令失败: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 尝试通过IPC将URI命令发送给已运行的实例
        /// </summary>
        /// <param name="uri">URI命令</param>
        /// <returns>是否成功发送</returns>
        public static bool TrySendUriCommandToExistingInstance(string uri)
        {
            try
            {
                LogHelper.WriteLogToFile($"尝试通过IPC发送URI命令给已运行实例: {uri}", LogHelper.LogType.Event);

                // 创建IPC文件
                string tempDir = Path.GetTempPath();
                string ipcFileName = IpcUriCommandPrefix + Guid.NewGuid().ToString("N") + ".tmp";
                string ipcFilePath = Path.Combine(tempDir, ipcFileName);

                // 写入URI命令到IPC文件
                File.WriteAllText(ipcFilePath, uri, Encoding.UTF8);

                // 创建事件通知已运行实例
                using (EventWaitHandle ipcEvent = new EventWaitHandle(false, EventResetMode.ManualReset, IpcEventName))
                {
                    ipcEvent.Set();
                }

                // 等待一段时间让已运行实例处理命令
                Thread.Sleep(1000);

                // 清理IPC文件
                try
                {
                    if (File.Exists(ipcFilePath))
                    {
                        File.Delete(ipcFilePath);
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.WriteLogToFile($"清理IPC文件失败: {ex.Message}", LogHelper.LogType.Warning);
                }

                LogHelper.WriteLogToFile("IPC URI命令发送完成", LogHelper.LogType.Event);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"通过IPC发送URI命令失败: {ex.Message}", LogHelper.LogType.Error);
                return false;
            }
        }

        /// <summary>
        /// 清理可能残留的旧 IPC 文件，防止重启后重复执行上次未处理的命令
        /// </summary>
        private static void CleanupStaleIpcFiles()
        {
            try
            {
                string tempDir = Path.GetTempPath();
                string[] prefixes = { IpcFilePrefix, IpcBoardModePrefix, IpcShowModePrefix, IpcUriCommandPrefix, IpcPluginFileOpenPrefix, IpcImageViewerFilePrefix };
                int cleaned = 0;
                foreach (string prefix in prefixes)
                {
                    string[] files = Directory.GetFiles(tempDir, prefix + "*.tmp");
                    foreach (string file in files)
                    {
                        try { File.Delete(file); cleaned++; }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                    }
                }
                if (cleaned > 0)
                    LogHelper.WriteLogToFile($"IPC 启动清理：删除了 {cleaned} 个残留 IPC 文件", LogHelper.LogType.Event);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"IPC 启动清理失败: {ex.Message}", LogHelper.LogType.Warning);
            }
        }

        /// <summary>
        /// 启动IPC监听器，等待其他实例发送文件路径
        /// </summary>
        public static void StartIpcListener()
        {
            try
            {
                Thread ipcThread = new Thread(() =>
                {
                    try
                    {
                        LogHelper.WriteLogToFile("启动IPC监听器", LogHelper.LogType.Event);

                        // 启动前清理旧的 IPC 文件，防止重启后重复执行上次的命令
                        CleanupStaleIpcFiles();

                        using (EventWaitHandle ipcEvent = new EventWaitHandle(false, EventResetMode.ManualReset, IpcEventName))
                        {
                            // 重置事件，清除可能残留的信号
                            ipcEvent.Reset();

                            while (true)
                            {
                                // 等待IPC事件
                                if (ipcEvent.WaitOne(IpcTimeout))
                                {
                                    // 处理IPC文件
                                    ProcessIpcFiles();

                                    // 重置事件
                                    ipcEvent.Reset();
                                }

                                // 检查应用是否还在运行
                                if (Application.Current == null || Application.Current.Dispatcher == null)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"IPC监听器出错: {ex.Message}", LogHelper.LogType.Error);
                    }
                });

                ipcThread.IsBackground = true;
                ipcThread.Start();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"启动IPC监听器失败: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        /// <summary>
        /// 处理IPC文件
        /// </summary>
        private static void ProcessIpcFiles()
        {
            try
            {
                string tempDir = Path.GetTempPath();

                // 处理文件路径IPC文件
                string[] ipcFiles = Directory.GetFiles(tempDir, IpcFilePrefix + "*.tmp");
                foreach (string ipcFile in ipcFiles)
                {
                    try
                    {
                        // 读取文件路径
                        string filePath = File.ReadAllText(ipcFile, Encoding.UTF8);

                        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        {
                            LogHelper.WriteLogToFile($"IPC接收到文件路径: {filePath}", LogHelper.LogType.Event);

                            // 在UI线程中处理文件打开
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    // 获取主窗口并打开文件
                                    if (Application.Current.MainWindow is MainWindow mainWindow)
                                    {
                                        mainWindow.OpenSingleStrokeFile(filePath);
                                        mainWindow.ShowNotification(string.Format(AutomationStrings.FileAssoc_LoadedStrokesFile, Path.GetFileName(filePath)));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.WriteLogToFile($"IPC处理文件打开失败: {ex.Message}", LogHelper.LogType.Error);
                                }
                            }));
                        }

                        // 删除IPC文件
                        File.Delete(ipcFile);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"处理IPC文件失败: {ex.Message}", LogHelper.LogType.Warning);

                        // 尝试删除损坏的IPC文件
                        try
                        {
                            if (File.Exists(ipcFile))
                            {
                                File.Delete(ipcFile);
                            }
                        }
                        catch (Exception innerEx) { System.Diagnostics.Debug.WriteLine(innerEx); }
                    }
                }

                // 处理插件关联文件路径IPC文件（文件关联打开）
                string[] pluginOpenFiles = Directory.GetFiles(tempDir, IpcPluginFileOpenPrefix + "*.tmp");
                foreach (string ipcFile in pluginOpenFiles)
                {
                    try
                    {
                        string filePath = File.ReadAllText(ipcFile, Encoding.UTF8);

                        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        {
                            LogHelper.WriteLogToFile($"IPC接收到插件关联文件路径: {filePath}", LogHelper.LogType.Event);

                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    // 派发给注册该扩展名的插件。不走 MainWindow.HandleUriCommand，
                                    // 避免受「启用 URI 协议」设置门禁；PluginManager.TryDispatchUri 本身无该守卫。
                                    DispatchPluginFileOpen(filePath);
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.WriteLogToFile($"IPC处理插件文件打开失败: {ex.Message}", LogHelper.LogType.Error);
                                }
                            }));
                        }

                        File.Delete(ipcFile);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"处理插件文件 IPC 失败: {ex.Message}", LogHelper.LogType.Warning);

                        try
                        {
                            if (File.Exists(ipcFile))
                            {
                                File.Delete(ipcFile);
                            }
                        }
                        catch (Exception innerEx) { System.Diagnostics.Debug.WriteLine(innerEx); }
                    }
                }

                // 处理白板模式命令IPC文件
                string[] boardModeFiles = Directory.GetFiles(tempDir, IpcBoardModePrefix + "*.tmp");
                foreach (string ipcFile in boardModeFiles)
                {
                    try
                    {
                        // 读取命令内容
                        string command = File.ReadAllText(ipcFile, Encoding.UTF8);

                        if (command == "BOARD_MODE")
                        {
                            LogHelper.WriteLogToFile("IPC接收到白板模式命令", LogHelper.LogType.Event);

                            // 在UI线程中处理白板模式切换
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    // 获取主窗口并切换到白板模式
                                    if (Application.Current.MainWindow is MainWindow mainWindow)
                                    {
                                        mainWindow.SwitchToBoardMode();
                                        mainWindow.ShowNotification(AutomationStrings.FileAssoc_SwitchedToBoardMode);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.WriteLogToFile($"IPC处理白板模式切换失败: {ex.Message}", LogHelper.LogType.Error);
                                }
                            }));
                        }

                        // 删除IPC文件
                        File.Delete(ipcFile);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"处理白板模式IPC文件失败: {ex.Message}", LogHelper.LogType.Warning);

                        // 尝试删除损坏的IPC文件
                        try
                        {
                            if (File.Exists(ipcFile))
                            {
                                File.Delete(ipcFile);
                            }
                        }
                        catch (Exception innerEx) { System.Diagnostics.Debug.WriteLine(innerEx); }
                    }
                }

                // 处理展开浮动栏命令IPC文件
                string[] showModeFiles = Directory.GetFiles(tempDir, IpcShowModePrefix + "*.tmp");
                foreach (string ipcFile in showModeFiles)
                {
                    try
                    {
                        // 读取命令内容
                        string command = File.ReadAllText(ipcFile, Encoding.UTF8);

                        if (command == "SHOW_MODE")
                        {
                            LogHelper.WriteLogToFile("IPC接收到展开浮动栏命令", LogHelper.LogType.Event);

                            // 在UI线程中处理展开浮动栏
                            Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
                            {
                                try
                                {
                                    // 获取主窗口并展开浮动栏
                                    if (Application.Current.MainWindow is MainWindow mainWindow)
                                    {
                                        // 如果当前处于收纳模式，则展开浮动栏
                                        if (mainWindow.isFloatingBarFolded)
                                        {
                                            await mainWindow.UnFoldFloatingBar(new object());
                                        }
                                        mainWindow.ShowNotification(AutomationStrings.FileAssoc_ExitedFoldMode);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.WriteLogToFile($"IPC处理展开浮动栏失败: {ex.Message}", LogHelper.LogType.Error);
                                }
                            }));
                        }

                        // 删除IPC文件
                        File.Delete(ipcFile);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"处理展开浮动栏IPC文件失败: {ex.Message}", LogHelper.LogType.Warning);

                        // 尝试删除损坏的IPC文件
                        try
                        {
                            if (File.Exists(ipcFile))
                            {
                                File.Delete(ipcFile);
                            }
                        }
                        catch (Exception innerEx) { System.Diagnostics.Debug.WriteLine(innerEx); }
                    }
                }

                // 处理图片查看器文件路径 IPC 文件（双击图片启动，已有实例接收）
                string[] imageViewerFiles = Directory.GetFiles(tempDir, IpcImageViewerFilePrefix + "*.tmp");
                foreach (string ipcFile in imageViewerFiles)
                {
                    try
                    {
                        string filePath = File.ReadAllText(ipcFile, Encoding.UTF8);

                        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        {
                            LogHelper.WriteLogToFile($"IPC接收到图片路径: {filePath}", LogHelper.LogType.Event);

                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    Windows.ImageViewerManager.OpenImage(filePath);
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.WriteLogToFile($"IPC处理图片打开失败: {ex.Message}", LogHelper.LogType.Error);
                                }
                            }));
                        }

                        File.Delete(ipcFile);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"处理图片 IPC 文件失败: {ex.Message}", LogHelper.LogType.Warning);

                        try
                        {
                            if (File.Exists(ipcFile)) File.Delete(ipcFile);
                        }
                        catch (Exception innerEx) { System.Diagnostics.Debug.WriteLine(innerEx); }
                    }
                }

                // 处理URI命令IPC文件
                string[] uriCommandFiles = Directory.GetFiles(tempDir, IpcUriCommandPrefix + "*.tmp");
                foreach (string ipcFile in uriCommandFiles)
                {
                    try
                    {
                        // 读取命令内容
                        string uri = File.ReadAllText(ipcFile, Encoding.UTF8);

                        if (!string.IsNullOrEmpty(uri))
                        {
                            LogHelper.WriteLogToFile($"IPC接收到URI命令: {uri}", LogHelper.LogType.Event);

                            // 在UI线程中处理URI命令
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    // 获取主窗口并处理URI命令
                                    if (Application.Current.MainWindow is MainWindow mainWindow)
                                    {
                                        mainWindow.HandleUriCommand(uri);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogHelper.WriteLogToFile($"IPC处理URI命令失败: {ex.Message}", LogHelper.LogType.Error);
                                }
                            }));
                        }

                        // 删除IPC文件
                        File.Delete(ipcFile);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"处理URI命令IPC文件失败: {ex.Message}", LogHelper.LogType.Warning);

                        // 尝试删除损坏的IPC文件
                        try
                        {
                            if (File.Exists(ipcFile))
                            {
                                File.Delete(ipcFile);
                            }
                        }
                        catch (Exception innerEx) { System.Diagnostics.Debug.WriteLine(innerEx); }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"处理IPC文件时出错: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        //[DllImport("shell32.dll")]
        //private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}