using System.Text;

namespace WebViewHub.Helpers;

/// <summary>
/// Minimal parser for the Apple <c>.icns</c> container format. macOSicons
/// hands these out as one of three URL flavors per hit; the .icns file
/// holds PNG frames at 16, 32, 64, 128, 256, 512, and 1024 pixels (subset
/// per hit). We pick the largest PNG frame and treat it as the canonical
/// source — better than the single-size iOSUrl/lowResPngUrl flavors when
/// those land on the dead <c>parsefiles.back4app.com</c> CDN.
///
/// Format (big-endian):
///   "icns" (4) | total length (4) | repeating chunks:
///     type (4 ASCII) | chunk length incl. header (4) | data
///
/// Newer types store PNG bytes directly in the data block: ic07/ic08/ic09/
/// ic10 (128/256/512/1024 px) and ic11/ic12/ic13/ic14 (Retina 32/64/256/512).
/// Older types (is32, il32, …) hold raw RGBA — we skip those.
/// </summary>
public static class IcnsParser
{
    public static bool IsIcns(byte[]? bytes) =>
        bytes != null && bytes.Length >= 8
        && bytes[0] == 'i' && bytes[1] == 'c' && bytes[2] == 'n' && bytes[3] == 's';

    /// <summary>
    /// Walks the .icns container and returns the bytes of the largest
    /// PNG-encoded frame. Returns null when the container is malformed
    /// or contains no PNG-flavored chunks.
    /// </summary>
    public static byte[]? TryExtractLargestPng(byte[] icnsBytes)
    {
        if (!IsIcns(icnsBytes)) return null;

        var totalLen = ReadUInt32BE(icnsBytes, 4);
        if (totalLen > (uint)icnsBytes.Length) totalLen = (uint)icnsBytes.Length;
        if (totalLen < 8) return null;

        byte[]? bestPng = null;
        int bestWidth = 0;
        int offset = 8;

        while (offset + 8 <= totalLen)
        {
            var chunkLen = ReadUInt32BE(icnsBytes, offset + 4);
            if (chunkLen < 8 || (uint)offset + chunkLen > totalLen) break;

            var dataOffset = offset + 8;
            var dataLen = (int)chunkLen - 8;

            if (dataLen >= 24 && IsPng(icnsBytes, dataOffset))
            {
                // PNG IHDR width field starts at byte 16 of the PNG.
                var w = (int)ReadUInt32BE(icnsBytes, dataOffset + 16);
                if (w > bestWidth)
                {
                    bestWidth = w;
                    bestPng = new byte[dataLen];
                    Array.Copy(icnsBytes, dataOffset, bestPng, 0, dataLen);
                }
            }

            offset += (int)chunkLen;
        }

        return bestPng;
    }

    private static bool IsPng(byte[] b, int off) =>
        b.Length >= off + 8
        && b[off] == 0x89 && b[off + 1] == 0x50 && b[off + 2] == 0x4E && b[off + 3] == 0x47
        && b[off + 4] == 0x0D && b[off + 5] == 0x0A && b[off + 6] == 0x1A && b[off + 7] == 0x0A;

    private static uint ReadUInt32BE(byte[] b, int off)
    {
        return ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) | ((uint)b[off + 2] << 8) | b[off + 3];
    }
}
