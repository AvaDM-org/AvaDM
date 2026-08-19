using System.Buffers.Binary;
using System.Text;

namespace AvaDM.Core;

/// <summary>Per-chunk progress as stored in a <c>.avadm</c> footer.</summary>
public sealed record ChunkFooterData(long Start, long End, long BytesDownloaded, ChunkStatus Status);

/// <summary>Everything needed to resume a download without any external state - deserialized
/// from the trailer appended after the payload bytes in a <c>.avadm</c> working file.</summary>
public sealed record DownloadFooterData(Uri SourceUri, long TotalBytes, bool Resumable, IReadOnlyList<ChunkFooterData> Chunks);

/// <summary>
/// Serializes/deserializes the binary trailer appended to a <c>.avadm</c> working file, after the
/// payload bytes at <c>[0, TotalBytes)</c>. Placed at the end (not the start) so payload chunks
/// keep writing at their natural offsets - no offset math anywhere else in the engine needs to
/// change - and finishing a download is just a truncate-to-<c>TotalBytes</c> + rename.
///
/// Layout is fixed-width per chunk and big-endian throughout, so the total size is known before
/// the download starts (from the chunk count and the URI's byte length alone) and never needs to
/// grow or shrink mid-download - only the final truncate removes it:
/// <code>
/// 0        4    Magic "AVDM" (0x4156444D)
/// 4        2    Version (1)
/// 6        2    Flags (bit0 = resumable; cleared for the no-Range whole-file fallback)
/// 8        8    TotalBytes (int64)
/// 16       4    Uri byte length N (uint32)
/// 20       N    Uri, UTF-8 (AbsoluteUri, not ToString())
/// 20+N     4    ChunkCount C (int32)
/// 24+N     32*C Per chunk: Start(8) End(8) BytesDownloaded(8) Status(4)
/// 24+N+32C 8    Footer length (int64) - self-describing so a reader who only knows the file's
///               total length can find where the footer begins.
/// </code>
/// </summary>
public static class DownloadFooter
{
    private const uint Magic = 0x4156444D; // "AVDM"
    private const ushort Version = 1;
    private const ushort ResumableFlag = 1 << 0;
    private const int ChunkEntrySize = 32; // Start(8) + End(8) + BytesDownloaded(8) + Status(4), padded to 32
    private const int HeaderSize = 20; // Magic(4) + Version(2) + Flags(2) + TotalBytes(8) + UriLength(4)
    private const int FooterLengthFieldSize = 8;

    /// <summary>Total byte size of the serialized footer for a download with the given source
    /// URI and chunk count. Computed once, before the download starts, so the working file can
    /// be preallocated to its final size (payload + footer) up front.</summary>
    public static int ComputeSize(Uri sourceUri, int chunkCount)
    {
        var uriByteCount = Encoding.UTF8.GetByteCount(sourceUri.AbsoluteUri);
        return HeaderSize + uriByteCount + 4 /* ChunkCount */ + ChunkEntrySize * chunkCount + FooterLengthFieldSize;
    }

    public static byte[] Serialize(DownloadFooterData data)
    {
        var uriBytes = Encoding.UTF8.GetBytes(data.SourceUri.AbsoluteUri);
        var size = HeaderSize + uriBytes.Length + 4 + ChunkEntrySize * data.Chunks.Count + FooterLengthFieldSize;
        var buffer = new byte[size];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(span, Magic);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], data.Resumable ? ResumableFlag : (ushort)0);
        BinaryPrimitives.WriteInt64BigEndian(span[8..], data.TotalBytes);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], (uint)uriBytes.Length);

        var offset = HeaderSize;
        uriBytes.CopyTo(span[offset..]);
        offset += uriBytes.Length;

        BinaryPrimitives.WriteInt32BigEndian(span[offset..], data.Chunks.Count);
        offset += 4;

        foreach (var chunk in data.Chunks)
        {
            BinaryPrimitives.WriteInt64BigEndian(span[offset..], chunk.Start);
            BinaryPrimitives.WriteInt64BigEndian(span[(offset + 8)..], chunk.End);
            BinaryPrimitives.WriteInt64BigEndian(span[(offset + 16)..], chunk.BytesDownloaded);
            BinaryPrimitives.WriteInt32BigEndian(span[(offset + 24)..], (int)chunk.Status);
            offset += ChunkEntrySize;
        }

        BinaryPrimitives.WriteInt64BigEndian(span[offset..], size);

        return buffer;
    }

    /// <summary>Parses a footer previously produced by <see cref="Serialize"/>. <paramref name="bytes"/>
    /// must be exactly the footer's own bytes (i.e. the last <c>footerLength</c> bytes of the
    /// working file, where <c>footerLength</c> was read from the file's trailing 8 bytes).</summary>
    /// <exception cref="FormatException">The magic, version, or embedded length don't match -
    /// treat as "not resumable, start fresh" rather than propagating.</exception>
    public static DownloadFooterData Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize + 4 + FooterLengthFieldSize)
            throw new FormatException("Footer is too short to contain a header.");

        var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (magic != Magic)
            throw new FormatException($"Bad footer magic 0x{magic:X8}.");

        var version = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (version != Version)
            throw new FormatException($"Unsupported footer version {version}.");

        var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes[6..]);
        var totalBytes = BinaryPrimitives.ReadInt64BigEndian(bytes[8..]);
        var uriLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);

        var offset = HeaderSize;
        if (uriLength > int.MaxValue || offset + uriLength + 4 + FooterLengthFieldSize > bytes.Length)
            throw new FormatException("Footer URI length is out of range for the supplied buffer.");

        var uriText = Encoding.UTF8.GetString(bytes.Slice(offset, (int)uriLength));
        offset += (int)uriLength;

        var chunkCount = BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]);
        offset += 4;
        if (chunkCount < 0 || offset + (long)chunkCount * ChunkEntrySize + FooterLengthFieldSize != bytes.Length)
            throw new FormatException("Footer chunk count is inconsistent with the supplied buffer length.");

        var chunks = new ChunkFooterData[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            var start = BinaryPrimitives.ReadInt64BigEndian(bytes[offset..]);
            var end = BinaryPrimitives.ReadInt64BigEndian(bytes[(offset + 8)..]);
            var bytesDownloaded = BinaryPrimitives.ReadInt64BigEndian(bytes[(offset + 16)..]);
            var status = BinaryPrimitives.ReadInt32BigEndian(bytes[(offset + 24)..]);
            chunks[i] = new ChunkFooterData(start, end, bytesDownloaded, (ChunkStatus)status);
            offset += ChunkEntrySize;
        }

        var declaredLength = BinaryPrimitives.ReadInt64BigEndian(bytes[offset..]);
        if (declaredLength != bytes.Length)
            throw new FormatException("Footer's self-declared length does not match the buffer it was read from.");

        Uri sourceUri;
        try
        {
            sourceUri = new Uri(uriText, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            throw new FormatException("Footer URI is not a valid absolute URI.", ex);
        }

        return new DownloadFooterData(sourceUri, totalBytes, (flags & ResumableFlag) != 0, chunks);
    }
}
