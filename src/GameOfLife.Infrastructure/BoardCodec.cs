using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GameOfLife.Core;

namespace GameOfLife.Infrastructure;

/// <summary>
/// The stored form of a board. A 13-byte header holds the format, width, height and edge rule, and the
/// packed cells follow, Brotli-compressed when that makes them smaller.
/// </summary>
/// <remarks>
/// Most boards are mostly dead cells, so they compress well. A settled 256 x 256 board drops from 8,192
/// bytes to about 1.6 KB and a lone glider to under 100 bytes. Random boards barely compress, so they are
/// kept raw and nothing is ever stored larger than before. Board IDs hash the uncompressed cells, so
/// compression never changes an ID. Format 1 is the raw form written before compression was added, and
/// it is still read.
/// </remarks>
public static class BoardCodec
{
    public const byte RawFormat = 1;
    public const byte CompressedFormat = 2;
    public const int HeaderLength = 1 + 4 + 4 + 4;

    // Quality 4 compresses an 8 KB board in tens of microseconds with most of the gain of higher levels.
    private const int BrotliQuality = 4;
    private const int BrotliWindow = 22;

    public static byte[] Encode(Board board)
    {
        ReadOnlySpan<byte> cells = board.PackedCells;
        var compressed = new byte[BrotliEncoder.GetMaxCompressedLength(cells.Length)];
        bool smaller = BrotliEncoder.TryCompress(cells, compressed, out int compressedLength, BrotliQuality, BrotliWindow)
            && compressedLength < cells.Length;

        var value = new byte[HeaderLength + (smaller ? compressedLength : cells.Length)];
        value[0] = smaller ? CompressedFormat : RawFormat;
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(1), board.Width);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(5), board.Height);
        Encoding.ASCII.GetBytes(board.EdgeMode.ToKey(), value.AsSpan(9, 4));
        if (smaller)
        {
            compressed.AsSpan(0, compressedLength).CopyTo(value.AsSpan(HeaderLength));
        }
        else
        {
            cells.CopyTo(value.AsSpan(HeaderLength));
        }

        return value;
    }

    public static Board Decode(ReadOnlySpan<byte> value)
    {
        byte format = value[0];
        int width = BinaryPrimitives.ReadInt32LittleEndian(value[1..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(value[5..]);
        var edgeMode = EdgeModeKeys.Parse(Encoding.ASCII.GetString(value.Slice(9, 4)));
        ReadOnlySpan<byte> payload = value[HeaderLength..];

        switch (format)
        {
            case RawFormat:
                return Board.FromPacked(width, height, edgeMode, payload);
            case CompressedFormat:
                var cells = new byte[Board.PackedLength(width, height)];
                if (!BrotliDecoder.TryDecompress(payload, cells, out int written) || written != cells.Length)
                {
                    throw new InvalidDataException($"A stored {width}x{height} board could not be decompressed.");
                }

                return Board.FromPacked(width, height, edgeMode, cells);
            default:
                throw new InvalidDataException($"Unsupported board format version {format}.");
        }
    }
}
