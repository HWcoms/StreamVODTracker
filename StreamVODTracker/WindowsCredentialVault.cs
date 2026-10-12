using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Json;

namespace StreamVODTracker;

public sealed record PlatformApiCredentials(string ClientId, string ClientSecret);

[SupportedOSPlatform("windows")]
public static class WindowsCredentialVault
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const string Prefix = "VODTracker/PlatformApi/";

    public static bool HasCredentials(string platform)
    {
        var target = Prefix + NormalizePlatform(platform);
        if (!CredRead(target, GenericCredential, 0, out var pointer)) return false;
        CredFree(pointer);
        return true;
    }

    public static PlatformApiCredentials? Read(string platform)
    {
        var target = Prefix + NormalizePlatform(platform);
        if (!CredRead(target, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null; // ERROR_NOT_FOUND
            throw new Win32Exception(error, "Windows 자격 증명 저장소를 읽을 수 없습니다.");
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.CredentialBlobSize > 16_384 || native.CredentialBlob == IntPtr.Zero)
                throw new SecurityException("저장된 API 인증정보 형식이 올바르지 않습니다.");
            var bytes = new byte[checked((int)native.CredentialBlobSize)];
            Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return JsonSerializer.Deserialize<PlatformApiCredentials>(Encoding.UTF8.GetString(bytes));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Save(string platform, string clientId, string clientSecret)
    {
        var target = Prefix + NormalizePlatform(platform);
        var blob = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new PlatformApiCredentials(clientId.Trim(), clientSecret)));
        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                Comment = "VODTracker platform API credentials",
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = PersistLocalMachine,
                UserName = clientId.Trim()
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 자격 증명 저장소에 인증정보를 저장할 수 없습니다.");
        }
        finally
        {
            var zeroes = new byte[blob.Length];
            Marshal.Copy(zeroes, 0, blobPointer, zeroes.Length);
            Marshal.FreeHGlobal(blobPointer);
            Array.Clear(blob);
        }
    }

    public static void Delete(string platform)
    {
        var target = Prefix + NormalizePlatform(platform);
        if (!CredDelete(target, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error, "Windows 자격 증명 저장소에서 인증정보를 삭제할 수 없습니다.");
        }
    }

    private static string NormalizePlatform(string platform) => platform switch
    {
        "치지직" or "CHZZK" => "CHZZK",
        "SOOP" or "soop" => "SOOP",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), "지원하지 않는 플랫폼입니다.")
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
