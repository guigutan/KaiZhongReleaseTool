using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KaiZhongReleaseTool;

/// <summary>
/// 负责枚举并删除 Windows 凭据管理器中与指定远程服务器相关的旧凭据。
/// </summary>
internal static class WindowsCredentialHelper
{
    private const int ErrorNotFound = 1168;
    private const uint CredTypeGeneric = 1;
    private const uint CredTypeDomainPassword = 2;
    private const uint CredPersistLocalMachine = 2;

    /// <summary>
    /// 删除“通用凭据”和“Windows 凭据”中指向当前服务器的条目，防止旧账户覆盖新账户。
    /// </summary>
    public static void DeleteServerCredentials(string host, int port)
    {
        if (!CredEnumerate(null, 0, out var count, out var credentialsPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return;
            }

            throw new Win32Exception(error, "无法读取 Windows 凭据列表。");
        }

        try
        {
            for (var index = 0; index < count; index++)
            {
                var credentialPointer = Marshal.ReadIntPtr(credentialsPointer, index * IntPtr.Size);
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                var targetName = Marshal.PtrToStringUni(credential.TargetName) ?? string.Empty;

                if ((credential.Type == CredTypeGeneric || credential.Type == CredTypeDomainPassword) &&
                    IsServerTarget(targetName, host, port))
                {
                    if (!CredDelete(targetName, credential.Type, 0))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorNotFound)
                        {
                            throw new Win32Exception(error, $"删除旧远程凭据失败：{targetName}");
                        }
                    }
                }
            }
        }
        finally
        {
            CredFree(credentialsPointer);
        }
    }

    /// <summary>直接保存远程桌面凭据，用户名允许使用“.\administrator”。</summary>
    public static void SaveRemoteDesktopCredential(string targetName, string userName, string password)
    {
        var passwordBytes = System.Text.Encoding.Unicode.GetBytes(password);
        var passwordPointer = Marshal.AllocCoTaskMem(passwordBytes.Length);
        var targetPointer = Marshal.StringToCoTaskMemUni(targetName);
        var userPointer = Marshal.StringToCoTaskMemUni(userName);
        try
        {
            Marshal.Copy(passwordBytes, 0, passwordPointer, passwordBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeDomainPassword,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = passwordPointer,
                Persist = CredPersistLocalMachine,
                UserName = userPointer
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "新增或修改 Windows 远程桌面凭据失败。");
        }
        finally
        {
            // 密码使用完毕后清零非托管内存。
            for (var index = 0; index < passwordBytes.Length; index++)
                Marshal.WriteByte(passwordPointer, index, 0);
            Marshal.FreeCoTaskMem(passwordPointer);
            Marshal.FreeCoTaskMem(targetPointer);
            Marshal.FreeCoTaskMem(userPointer);
        }
    }

    /// <summary>只匹配当前服务器本身及其远程桌面端口，避免误删其他服务器的凭据。</summary>
    private static bool IsServerTarget(string targetName, string host, int port)
    {
        var normalizedTarget = targetName.Trim().TrimEnd('/');
        var candidates = new[]
        {
            host,
            $"{host}:{port}",
            $"TERMSRV/{host}",
            $"TERMSRV/{host}:{port}"
        };

        return candidates.Any(candidate =>
            normalizedTarget.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(string? filter, uint flags, out int count,
        out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
