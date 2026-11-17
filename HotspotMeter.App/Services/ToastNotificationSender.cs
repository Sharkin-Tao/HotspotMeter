using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using HotspotMeter.Core.TrafficGuard;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace HotspotMeter.App.Services;

/// <summary>
/// Windows 11 Toast 通知适配器。
///
/// 未打包桌面程序要显示 Toast，必须满足两个条件：
/// 1. 进程通过 SetCurrentProcessExplicitAppUserModelID 声明 AUMID；
/// 2. 开始菜单里存在一个带同一 AUMID 的快捷方式，供通知平台识别来源。
///
/// 注意：未打包进程读取 ToastNotifier.Setting 会抛 0x80070490，不能用它判断
/// “通知是否被关闭”。这里直接 Show，并把异常记录为 Failed；系统勿扰/权限关闭
/// 可能让 Show 成功但实际不显示，这是平台限制，不能伪称已确认送达。
/// </summary>
public sealed class ToastNotificationSender : IGuardNotificationSender, IDisposable
{
    public const string Aumid = "HotspotMeter.TrafficGuard";

    private static readonly Guid AppUserModelIdFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelIdPid = 5;
    private const uint GpsReadWrite = 2;

    private readonly object _gate = new();
    private readonly Action _onActivated;
    private readonly Action<string, Exception?>? _log;
    private ToastNotifier? _notifier;
    private ToastNotification? _lastToast;
    private bool _registered;
    private bool _disposed;

    public ToastNotificationSender(Action onActivated, Action<string, Exception?>? log = null)
    {
        _onActivated = onActivated ?? throw new ArgumentNullException(nameof(onActivated));
        _log = log;
    }

    public Task<GuardNotificationResult> SendAsync(
        GuardNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            EnsureRegistered();
            var xml = new XmlDocument();
            xml.LoadXml(BuildXml(notification));
            var toast = new ToastNotification(xml);
            toast.Activated += (_, _) => _onActivated();
            toast.Failed += (_, args) =>
                _log?.Invoke($"Traffic Guard 通知发送失败：{args.ErrorCode}", null);

            lock (_gate)
            {
                _lastToast = toast;
                _notifier!.Show(toast);
            }

            return Task.FromResult(new GuardNotificationResult(GuardNotificationStatus.Sent));
        }
        catch (Exception exception)
        {
            _log?.Invoke($"Traffic Guard 通知失败：{exception.GetType().Name}", exception);
            return Task.FromResult(new GuardNotificationResult(
                GuardNotificationStatus.Failed,
                $"{exception.GetType().Name}: {exception.Message}"));
        }
    }

    private void EnsureRegistered()
    {
        lock (_gate)
        {
            if (_registered)
            {
                return;
            }

            SetCurrentProcessExplicitAppUserModelID(Aumid);
            var programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");
            Directory.CreateDirectory(programs);
            var shortcut = Path.Combine(programs, "HotspotMeter Traffic Guard.lnk");
            if (!File.Exists(shortcut) || !string.Equals(ReadAumid(shortcut), Aumid, StringComparison.Ordinal))
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法确定当前可执行文件路径。");
                CreateShortcut(shortcut, executable, AppContext.BaseDirectory);
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }

            _notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
            _registered = true;
        }
    }

    private static string BuildXml(GuardNotification notification) =>
        $"""
         <toast activationType="foreground" launch="{SecurityElement.Escape(notification.LaunchArgument)}">
           <visual>
             <binding template="ToastGeneric">
               <text>{SecurityElement.Escape(notification.Title)}</text>
               <text>{SecurityElement.Escape(notification.Body)}</text>
             </binding>
           </visual>
           <actions>
             <action content="{SecurityElement.Escape(notification.ActionText)}"
                     arguments="{SecurityElement.Escape(notification.LaunchArgument)}"
                     activationType="foreground"/>
           </actions>
         </toast>
         """;

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription("HotspotMeter Traffic Guard");
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }

        var iid = typeof(IPropertyStore).GUID;
        SHGetPropertyStoreFromParsingName(
            shortcutPath,
            IntPtr.Zero,
            GpsReadWrite,
            ref iid,
            out var store);
        try
        {
            var key = new PropertyKey(AppUserModelIdFormat, AppUserModelIdPid);
            var value = new PropVariant
            {
                vt = 31,
                pointerValue = Marshal.StringToCoTaskMemUni(Aumid),
            };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(store);
        }
    }

    private static string ReadAumid(string shortcutPath)
    {
        var iid = typeof(IPropertyStore).GUID;
        SHGetPropertyStoreFromParsingName(shortcutPath, IntPtr.Zero, 0, ref iid, out var store);
        try
        {
            var key = new PropertyKey(AppUserModelIdFormat, AppUserModelIdPid);
            store.GetValue(ref key, out var value);
            try
            {
                return value.vt == 31
                    ? Marshal.PtrToStringUni(value.pointerValue) ?? string.Empty
                    : string.Empty;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(store);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lastToast = null;
            _notifier = null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr pbc,
        uint flags,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int count, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public IntPtr pointerValue;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}