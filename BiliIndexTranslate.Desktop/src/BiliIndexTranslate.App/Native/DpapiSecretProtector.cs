using System.Runtime.InteropServices;
using System.Text;
using BiliIndexTranslate.Core.Settings;

namespace BiliIndexTranslate.App.Native;

/// <summary>Windows DPAPI, current-user scope. The blob only decrypts for the same Windows account.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    public string Scheme => "dpapi-user";

    public string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var result = Transform(bytes, protect: true) ?? throw new InvalidOperationException("DPAPI protect failed.");
        return Convert.ToBase64String(result);
    }

    public string? Unprotect(string protectedValue)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(protectedValue); }
        catch (FormatException) { return null; }
        var result = Transform(bytes, protect: false);
        return result is null ? null : Encoding.UTF8.GetString(result);
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var ok = protect
                ? NativeMethods.CryptProtectData(ref inBlob, "BiliIndexTranslate", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : NativeMethods.CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok || outBlob.pbData == IntPtr.Zero) return null;
            try
            {
                var output = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
                return output;
            }
            finally { NativeMethods.LocalFree(outBlob.pbData); }
        }
        finally { inHandle.Free(); }
    }
}
