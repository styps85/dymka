using System.Runtime.InteropServices;
using System.Text;
using Dymka.Core;

namespace Dymka;

/// <summary>Защита ключа хранилища средствами Windows (DPAPI, текущий пользователь).</summary>
sealed class Dpapi : IKeyProtector
{
    [StructLayout(LayoutKind.Sequential)]
    struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved,
        IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved,
        IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr h);

    const int UiForbidden = 0x1;
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Dymka vault key v1");

    public byte[] Protect(byte[] data) => Run(data, protect: true);
    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    static byte[] Run(byte[] data, bool protect)
    {
        var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var inBlob = new DATA_BLOB { cbData = data.Length, pbData = hIn.AddrOfPinnedObject() };
        var entBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = hEnt.AddrOfPinnedObject() };
        var outBlob = new DATA_BLOB();
        try
        {
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Dymka", ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
            if (!ok) throw new System.Security.Cryptography.CryptographicException("DPAPI: ошибка " + Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (outBlob.pbData != IntPtr.Zero)
            {
                // стираем копию ключа в памяти Windows перед освобождением
                for (int i = 0; i < outBlob.cbData; i++) Marshal.WriteByte(outBlob.pbData, i, 0);
                LocalFree(outBlob.pbData);
            }
            hIn.Free();
            hEnt.Free();
        }
    }
}
