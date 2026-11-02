using System.Runtime.InteropServices;
using System.Text;

namespace ByteScape.Core.Util;

/// <summary>
/// Keeps secrets (the local AI's API key) out of the configuration file's plain text: Windows DPAPI encrypts them for the current
/// Windows user on this computer, so the XML only holds base64 that another user or another computer cannot read. Called
/// directly through crypt32 (no extra package in the single file exe).
/// </summary>
public static class Secrets
{
    /// <summary>Marks a protected value in the configuration XML (<c>protection="dpapi"</c>).</summary>
    public const string Scheme = "dpapi";

    // ties the blobs to this purpose: another program using DPAPI for the same user cannot pass them off as its own
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(Branding.Name + ".Secrets.v1");

    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        return Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plain), protect: true)
            ?? throw new InvalidOperationException("Windows could not encrypt the value (error " + Marshal.GetLastWin32Error() + ")."));
    }

    /// <summary>False when <paramref name="stored"/> is damaged or was protected by another Windows user or on another computer.</summary>
    public static bool TryUnprotect(string stored, out string plain)
    {
        plain = "";
        if (stored.Length == 0) return true;
        byte[] blob;
        try { blob = Convert.FromBase64String(stored.Trim()); }
        catch (FormatException) { return false; }
        var bytes = Transform(blob, protect: false);
        if (bytes == null) return false;
        plain = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static unsafe byte[]? Transform(byte[] input, bool protect)
    {
        fixed (byte* pIn = input)
        fixed (byte* pEntropy = Entropy)
        {
            var dataIn = new DataBlob { Size = input.Length, Data = (IntPtr)pIn };
            var entropy = new DataBlob { Size = Entropy.Length, Data = (IntPtr)pEntropy };
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out dataOut);
            if (!ok) return null;
            try
            {
                var result = new byte[dataOut.Size];
                Marshal.Copy(dataOut.Data, result, 0, dataOut.Size);
                return result;
            }
            finally
            {
                if (!protect) new Span<byte>((void*)dataOut.Data, dataOut.Size).Clear(); // the plain text does not linger in freed memory
                LocalFree(dataOut.Data);
            }
        }
    }

    private const int UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
