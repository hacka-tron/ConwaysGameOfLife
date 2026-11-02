using System.Buffers.Binary;
using System.Text;
using GameOfLife.Core;
using GameOfLife.Infrastructure;

namespace GameOfLife.Tests.Unit;

public class BoardCodecTests
{
    public static TheoryData<string> Boards => new() { "glider-256", "random-256", "single-1x1", "odd-3x5-wrap" };

    [Theory]
    [MemberData(nameof(Boards))]
    public void EncodedBoardsDecodeToTheSameBoard(string name)
    {
        var board = Make(name);

        var decoded = BoardCodec.Decode(BoardCodec.Encode(board));

        Assert.Equal(board.Id, decoded.Id);
        Assert.True(board.ContentEquals(decoded));
    }

    [Fact]
    public void SparseBoardsAreStoredCompressed()
    {
        var encoded = BoardCodec.Encode(Make("glider-256"));

        Assert.Equal(BoardCodec.CompressedFormat, encoded[0]);
        Assert.True(encoded.Length < 100, $"A glider on a 256 x 256 board took {encoded.Length} bytes.");
    }

    [Fact]
    public void BoardsThatDoNotCompressAreStoredRaw()
    {
        var board = Make("random-256");

        var encoded = BoardCodec.Encode(board);

        // Random cells barely compress, so the raw form is kept and nothing is ever larger than it.
        Assert.True(encoded.Length <= BoardCodec.HeaderLength + board.PackedCells.Length);
    }

    [Fact]
    public void DecodesTheOriginalUncompressedFormat()
    {
        var board = Make("odd-3x5-wrap");

        var decoded = BoardCodec.Decode(RawFormat(board));

        Assert.True(board.ContentEquals(decoded));
    }

    [Fact]
    public void DamagedRecordsFailWithAClearError()
    {
        var value = BoardCodec.Encode(Make("glider-256"));
        var truncated = value.AsSpan(0, 5).ToArray();
        var hugeSize = value.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(hugeSize.AsSpan(1), int.MaxValue);

        Assert.Throws<InvalidDataException>(() => BoardCodec.Decode(truncated));
        Assert.Throws<InvalidDataException>(() => BoardCodec.Decode(hugeSize));
    }

    /// <summary>Format 1, written before compression was added. The header is followed by the packed cells as is.</summary>
    internal static byte[] RawFormat(Board board)
    {
        var value = new byte[13 + board.PackedCells.Length];
        value[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(1), board.Width);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(5), board.Height);
        Encoding.ASCII.GetBytes(board.EdgeMode.ToKey(), value.AsSpan(9, 4));
        board.PackedCells.CopyTo(value.AsSpan(13));
        return value;
    }

    internal static Board Make(string name)
    {
        switch (name)
        {
            case "glider-256":
                return Patterns.Parse(Patterns.GliderOn(256));
            case "random-256":
                var random = new Random(5);
                var cells = new bool[256, 256];
                for (int y = 0; y < 256; y++)
                {
                    for (int x = 0; x < 256; x++)
                    {
                        cells[y, x] = random.Next(2) == 1;
                    }
                }

                return Board.FromCells(cells, EdgeMode.Dead);
            case "single-1x1":
                return Patterns.Parse("#");
            default:
                return Patterns.Parse(EdgeMode.Wrap, "#..", ".#.", "..#", "##.", "..#");
        }
    }
}
