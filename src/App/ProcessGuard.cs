using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SmartClassroom.App;

/// <summary>
/// Windows 下给**自己这个进程**加一道锁：拒绝普通用户结束它。
///
/// 场景：教室一体机上，同学打开任务管理器就能把应用结束掉。
/// 做法是给进程对象设置 DACL —— 拒绝 Builtin\Users 的 PROCESS_TERMINATE，
/// 同时保留 SYSTEM / Administrators / 所有者 的完全控制。
///
/// 说明与边界：
///   · 只影响"结束进程"这一个权限，不影响读写文件、不影响应用自身退出；
///   · 管理员令牌带 SeDebugPrivilege 时仍可强行结束（Windows 机制如此），
///     目标是拦住同学，不是防管理员；
///   · 这是**可选项**，默认关闭；关掉后立即恢复正常（进程 DACL 会被还原）。
/// </summary>
public static class ProcessGuard
{
    private const uint ProcessTerminate = 0x0001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int SeKernelObject = 6;
    private const int SddlRevision1 = 1;

    /// <summary>当前是否已经加上保护（用于界面显示真实状态）。</summary>
    public static bool IsProtected { get; private set; }

    /// <summary>本平台是否支持（只有 Windows 有这套机制）。</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 开启/关闭保护。返回是否成功；失败只记日志，不影响应用运行。
    /// </summary>
    public static bool Apply(bool enabled)
    {
        if (!IsSupported)
        {
            IsProtected = false;
            return false;
        }
        try
        {
            // 允许：SYSTEM、Administrators、所有者完全控制；拒绝：Users 结束进程
            var sddl = enabled
                ? "D:(D;;0x0001;;;BU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)"
                : "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)(A;;GA;;;BU)";
            return SetProcessDacl(sddl) && (IsProtected = enabled) == enabled;
        }
        catch (Exception)
        {
            IsProtected = false;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool SetProcessDacl(string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, SddlRevision1, out var sd, out _))
            return false;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    sddl, SddlRevision1, out var sdForDacl, out _))
                return false;
            try
            {
                // 取出 DACL 指针（SECURITY_DESCRIPTOR 的布局：Revision, Sbz1, Control(2), Owner, Group, Sacl, Dacl）
                var daclPtr = Marshal.ReadIntPtr(sdForDacl, IntPtr.Size == 8 ? 16 : 12);
                var ok = SetSecurityInfo(GetCurrentProcess(), SeKernelObject, DaclSecurityInformation,
                    IntPtr.Zero, IntPtr.Zero, daclPtr, IntPtr.Zero) == 0;
                return ok;
            }
            finally
            {
                LocalFree(sdForDacl);
            }
        }
        finally
        {
            LocalFree(sd);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSd, int revision, out IntPtr sd, out int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetSecurityInfo(
        IntPtr handle, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
