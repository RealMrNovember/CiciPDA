using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CiciPDA;

/// <summary>
/// Reads the signing certificate(s) out of an APK's v2/v3 signing block so the Windows app can pin the signer identity
/// before anything is installed. This identifies the signer; the signature itself is verified by apksigner at build time
/// and again by Android's package installer (an update signed by a different key is refused by the phone).
/// </summary>
public static class ApkSigning
{
    const uint IdV2 = 0x7109871a, IdV3 = 0xf05368c0;
    static readonly byte[] Magic = "APK Sig Block 42"u8.ToArray();

    public static IReadOnlyList<string> SignerCertSha256(string apkPath) => SignerCertSha256(File.ReadAllBytes(apkPath));

    public static IReadOnlyList<string> SignerCertSha256(byte[] apk)
    {
        int eocd = FindEocd(apk);
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(eocd + 16));
        if (cdOffset < 32 || cdOffset > apk.Length) throw new InvalidDataException("Not a valid APK (central directory).");
        if (!apk.AsSpan((int)cdOffset - 16, 16).SequenceEqual(Magic)) throw new InvalidDataException("APK has no v2/v3 signing block (v1-only signatures are not accepted).");
        long blockSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(apk.AsSpan((int)cdOffset - 24));
        long blockStart = cdOffset - blockSize - 8;
        if (blockStart < 0) throw new InvalidDataException("Corrupt APK signing block.");

        var certs = new List<string>();
        long p = blockStart + 8, end = cdOffset - 24;
        while (p + 12 <= end)
        {
            long len = (long)BinaryPrimitives.ReadUInt64LittleEndian(apk.AsSpan((int)p));
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan((int)p + 8));
            if (len < 4 || p + 8 + len > end + 24) throw new InvalidDataException("Corrupt signing block entry.");
            if (id == IdV2 || id == IdV3) ParseSigners(apk, (int)p + 12, (int)(p + 8 + len), certs);
            p += 8 + len;
        }
        if (certs.Count == 0) throw new InvalidDataException("No signer certificate found in the APK.");
        return certs.Distinct().ToList();
    }

    static void ParseSigners(byte[] a, int start, int end, List<string> certs)
    {
        int signersLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(start));
        int p = start + 4, sEnd = p + signersLen;
        while (p < sEnd && p < end)
        {
            int signerLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(p)); p += 4;
            int signedLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(p));
            int q = p + 4;                                         // signed data: digests, certificates, ...
            int digestsLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(q)); q += 4 + digestsLen;
            int certsLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(q)); q += 4;
            int cEnd = q + certsLen;
            while (q < cEnd)
            {
                int cl = (int)BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(q)); q += 4;
                certs.Add(Convert.ToHexString(SHA256.HashData(a.AsSpan(q, cl))).ToLowerInvariant());
                q += cl;
            }
            p += signerLen;
        }
    }

    static int FindEocd(byte[] a)
    {
        for (int i = a.Length - 22; i >= Math.Max(0, a.Length - 65557); i--)
            if (a[i] == 0x50 && a[i + 1] == 0x4b && a[i + 2] == 5 && a[i + 3] == 6) return i;
        throw new InvalidDataException("Not a ZIP/APK file.");
    }
}
