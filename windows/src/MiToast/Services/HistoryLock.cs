using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace MiToast.Services;

/// <summary>
/// 历史通知访问锁：
/// 查看历史通知前调用系统身份验证（Windows Hello PIN/指纹/人脸），验证通过才放行。
/// 通过 UserConsentVerifier 弹出系统「Windows 安全中心」凭据对话框（与系统登录 PIN 一致）；
/// 设备不支持 Hello 时回退到 CredUI 密码验证（LogonUser 校验本机账户）。
/// 验证结果缓存一段时间，避免短时间内反复打开历史页面时重复弹窗。
/// </summary>
public static class HistoryLock
{
    /// <summary>一次验证的有效窗口（毫秒）。</summary>
    private const long UnlockWindowMs = 2 * 60 * 1000;

    private static readonly object Gate = new();
    private static long _unlockedAt;

    /// <summary>当前是否处于已解锁状态（供 UI 提示用）。</summary>
    public static bool IsUnlocked
    {
        get
        {
            lock (Gate)
            {
                return _unlockedAt > 0 &&
                       Environment.TickCount64 - _unlockedAt < UnlockWindowMs;
            }
        }
    }

    /// <summary>
    /// 确保已解锁：缓存有效直接放行；否则弹系统身份验证对话框。
    /// 返回是否验证通过（用户取消/失败时返回 false，调用方应放弃打开历史页面）。
    /// </summary>
    public static async Task<bool> EnsureUnlockedAsync()
    {
        lock (Gate)
        {
            if (_unlockedAt > 0 && Environment.TickCount64 - _unlockedAt < UnlockWindowMs)
            {
                return true;
            }
        }

        bool verified = await VerifyAsync();
        if (verified)
        {
            lock (Gate)
            {
                _unlockedAt = Environment.TickCount64;
            }
        }
        return verified;
    }

    /// <summary>系统身份验证：Windows Hello 优先，密码回退兜底。</summary>
    private static async Task<bool> VerifyAsync()
    {
        // 1) Windows Hello（PIN/指纹/人脸）——系统「Windows 安全中心」对话框
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability == UserConsentVerifierAvailability.Available)
            {
                var result = await UserConsentVerifier.RequestVerificationAsync(
                    "查看历史通知前，请先验证身份");
                return result == UserConsentVerificationResult.Verified;
            }
        }
        catch
        {
            // Hello 不可用时走密码回退
        }

        // 2) 密码回退：CredUI 凭据对话框 + LogonUser 本机校验
        return VerifyWithPassword();
    }

    // ---------- CredUI 密码验证 ----------

    private const int CREDUIWIN_GENERIC = 0x1;
    private const int CREDUIWIN_AUTHPACKAGE_ONLY = 0x10;
    private const int CRED_PACK_GENERIC_CREDENTIALS = 0x1;
    private const int CREDUI_MAX_MESSAGE_LENGTH = 32767;

    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_WINNT50 = 3;

    private const int ERROR_LOGON_FAILURE = 1326;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszMessageText;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredUIPromptForWindowsCredentials(
        ref CREDUI_INFO pUiInfo, int dwAuthError, ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer, uint ulInAuthBufferSize, out IntPtr ppvOutAuthBuffer,
        out uint pulOutAuthBufferSize, ref bool pfSave, int dwFlags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBufferW(
        int dwFlags, IntPtr pAuthBuffer, uint cbAuthBuffer,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszUserName, ref int pcchMaxUserName,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDomainName, ref int pcchMaxDomainName,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszPassword, ref int pcchMaxPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername, string lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static bool VerifyWithPassword()
    {
        var uiInfo = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            pszCaptionText = "MiToast 身份验证",
            pszMessageText = "请输入本机 Windows 登录密码以查看历史通知"
        };

        uint authPackage = 0;
        bool save = false;
        int rc = CredUIPromptForWindowsCredentials(ref uiInfo, 0, ref authPackage,
            IntPtr.Zero, 0, out IntPtr buffer, out uint bufferSize, ref save,
            CREDUIWIN_GENERIC | CREDUIWIN_AUTHPACKAGE_ONLY);

        if (rc != 0 || buffer == IntPtr.Zero) return false; // 用户取消等

        try
        {
            var user = new StringBuilder(256);
            var domain = new StringBuilder(256);
            var password = new StringBuilder(CREDUI_MAX_MESSAGE_LENGTH);
            int userLen = user.Capacity;
            int domainLen = domain.Capacity;
            int passLen = password.Capacity;

            if (!CredUnPackAuthenticationBufferW(CRED_PACK_GENERIC_CREDENTIALS,
                    buffer, bufferSize, user, ref userLen, domain, ref domainLen,
                    password, ref passLen))
            {
                return false;
            }

            string username = user.ToString();
            string userDomain = domain.ToString();
            string pass = password.ToString();

            bool ok = LogonUser(username, userDomain.Length > 0 ? userDomain : ".",
                pass, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_WINNT50, out IntPtr token);
            if (ok) CloseHandle(token);
            return ok;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
        }
    }
}
