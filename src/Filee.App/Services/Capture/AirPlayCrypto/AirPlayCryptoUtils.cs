using System.Security.Cryptography;

namespace Filee.App.Services.Capture.AirPlayCrypto;

public static class AirPlayCryptoUtils
{
    public const string PairVerifyAesKey = "Pair-Verify-AES-Key";
    public const string PairVerifyAesIv = "Pair-Verify-AES-IV";

    public static byte[] CopyOfRange(byte[] src, int start, int end)
    {
        if (start < 0) start = 0;
        if (end > src.Length) end = src.Length;
        int len = Math.Max(0, end - start);
        byte[] dest = new byte[len];
        if (len > 0)
        {
            Array.Copy(src, start, dest, 0, len);
        }
        return dest;
    }

    public static byte[] Hash(byte[] first, byte[] last)
    {
        byte[] combined = new byte[first.Length + last.Length];
        Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        Buffer.BlockCopy(last, 0, combined, first.Length, last.Length);
        return SHA512.HashData(combined);
    }

    public static void Swap(byte[] arr, int idxA, int idxB)
    {
        using var mem = new MemoryStream(arr);
        using var reader = new BinaryReader(mem);
        using var writer = new BinaryWriter(mem);

        mem.Position = idxA;
        var a = reader.ReadInt32();

        mem.Position = idxB;
        var b = reader.ReadInt32();

        mem.Position = idxB;
        writer.Write(a);

        mem.Position = idxA;
        writer.Write(b);
    }
}
