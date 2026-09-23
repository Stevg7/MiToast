using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MiToast.Services;

/// <summary>
/// 敏感数据的静态加密（DPAPI，Windows 数据保护 API）：
/// 历史通知文件（含验证码、聊天、支付等内容）落盘前用当前用户凭据加密，
/// 只有登录到本机的同一 Windows 用户能解密——换用户、拆盘、拷贝文件均无法读取。
/// 通过 crypt32.dll 的 CryptProtectData/CryptUnprotectData 直接调用，无第三方依赖。
/// </summary>
public static class SensitiveStorage
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length };
        blob.pbData = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        if (blob.pbData == IntPtr.Zero || blob.cbData <= 0) return Array.Empty<byte>();
        var data = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, data, 0, blob.cbData);
        return data;
    }

    /// <summary>加密字节序列（当前用户作用域）。</summary>
    public static byte[] Protect(byte[] plain)
    {
        var inBlob = ToBlob(plain);
        var outBlob = new DATA_BLOB();
        var entropy = new DATA_BLOB();
        try
        {
            if (!CryptProtectData(ref inBlob, "MiToast sensitive data", ref entropy,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }
            return FromBlob(outBlob);
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>解密字节序列；失败（其他用户/文件损坏/明文数据）返回 null。</summary>
    public static byte[]? Unprotect(byte[] cipher)
    {
        var inBlob = ToBlob(cipher);
        var outBlob = new DATA_BLOB();
        var entropy = new DATA_BLOB();
        try
        {
            if (!CryptUnprotectData(ref inBlob, null, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
            {
                return null;
            }
            return FromBlob(outBlob);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>读取历史通知文件：优先按 DPAPI 密文解密；解密失败时兼容旧版明文 JSON。</summary>
    public static string? ReadHistoryText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);

            // 明文 JSON 以 '[' 开头（历史为数组）；密文是 DPAPI 二进制
            if (bytes.Length > 0 && (bytes[0] == '[' || bytes[0] == '{'))
            {
                return Encoding.UTF8.GetString(bytes);
            }

            var plain = Unprotect(bytes);
            return plain != null ? Encoding.UTF8.GetString(plain) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>加密写入历史通知文件（DPAPI，当前用户作用域）。</summary>
    public static void WriteHistoryText(string path, string json)
    {
        var cipher = Protect(Encoding.UTF8.GetBytes(json));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, cipher);
    }
}
