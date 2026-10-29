using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GameOfLife.Core;

/// <summary>
/// An immutable board state. Its <see cref="Id"/> is a hash of its content, so two equal boards
/// always share an ID and a board's ID never changes.
/// </summary>
public sealed class Board
{
    // Bumping this version gives every board a new ID, for example if the hashed layout changes.
    private const string IdVersion = "gol-v2";

    // Crockford base32, the alphabet ULIDs use. It has no i, l, o or u, so IDs are hard to misread.
    private const string IdAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>Length of every board ID.</summary>
    public const int IdLength = 26;

    // Row-major bitset. Cell (x, y) is bit (y * Width + x), least significant bit first.
    private readonly byte[] _cells;

    private Board(int width, int height, EdgeMode edgeMode, byte[] cells)
    {
        Width = width;
        Height = height;
        EdgeMode = edgeMode;
        _cells = cells;
        Id = ComputeId(width, height, edgeMode, cells);
    }

    /// <summary>
    /// The first 128 bits of a SHA-256 of the board's dimensions, edge rule and cells, as 26 lowercase
    /// base32 characters. 128 bits keeps accidental and deliberate collisions out of reach.
    /// </summary>
    public string Id { get; }

    public int Width { get; }

    public int Height { get; }

    public EdgeMode EdgeMode { get; }

    public ReadOnlySpan<byte> PackedCells => _cells;

    public bool this[int x, int y]
    {
        get
        {
            int bit = y * Width + x;
            return (_cells[bit >> 3] & (1 << (bit & 7))) != 0;
        }
    }

    public static int PackedLength(int width, int height) => (width * height + 7) / 8;

    /// <summary>Creates a board from a grid indexed as <c>cells[y, x]</c>.</summary>
    public static Board FromCells(bool[,] cells, EdgeMode edgeMode)
    {
        ArgumentNullException.ThrowIfNull(cells);
        int height = cells.GetLength(0);
        int width = cells.GetLength(1);
        if (width == 0 || height == 0)
        {
            throw new ArgumentException("A board needs at least one row and one column.", nameof(cells));
        }

        var packed = new byte[PackedLength(width, height)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (cells[y, x])
                {
                    SetBit(packed, y * width + x);
                }
            }
        }

        return new Board(width, height, edgeMode, packed);
    }

    /// <summary>Rebuilds a board from its packed form, for example when reading from storage.</summary>
    public static Board FromPacked(int width, int height, EdgeMode edgeMode, ReadOnlySpan<byte> packed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (packed.Length != PackedLength(width, height))
        {
            throw new ArgumentException($"Expected {PackedLength(width, height)} bytes for a {width}x{height} board.", nameof(packed));
        }

        // Unused bits in the last byte must be zero, or equal boards could hash differently.
        int usedBits = width * height % 8;
        if (usedBits != 0 && (packed[^1] >> usedBits) != 0)
        {
            throw new ArgumentException("Bits beyond the last cell must be zero.", nameof(packed));
        }

        return new Board(width, height, edgeMode, packed.ToArray());
    }

    /// <summary>For the engine, which builds the packed form itself and never sets unused bits.</summary>
    internal static Board FromTrustedPacked(int width, int height, EdgeMode edgeMode, byte[] packed) =>
        new(width, height, edgeMode, packed);

    /// <summary>Returns the cells as a grid indexed as <c>cells[y, x]</c>.</summary>
    public bool[,] ToCells()
    {
        var cells = new bool[Height, Width];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                cells[y, x] = this[x, y];
            }
        }

        return cells;
    }

    public bool ContentEquals(Board other) =>
        Width == other.Width
        && Height == other.Height
        && EdgeMode == other.EdgeMode
        && _cells.AsSpan().SequenceEqual(other._cells);

    internal static void SetBit(byte[] packed, int bit) => packed[bit >> 3] |= (byte)(1 << (bit & 7));

    private static string ComputeId(int width, int height, EdgeMode edgeMode, byte[] cells)
    {
        // Size and edge rule are part of the identity because the same live cells
        // evolve differently on a board of a different size or with different edges.
        byte[] header = Encoding.ASCII.GetBytes($"{IdVersion}|{edgeMode.ToKey()}|{width}x{height}|");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(cells);
        return EncodeId(hash.GetHashAndReset());
    }

    private static string EncodeId(byte[] digest)
    {
        // Same layout as a ULID. 128 bits fill 26 characters with two spare high bits, so the first character is 0 to 7.
        UInt128 value = BinaryPrimitives.ReadUInt128BigEndian(digest);
        return string.Create(IdLength, value, static (chars, remaining) =>
        {
            for (int i = chars.Length - 1; i >= 0; i--)
            {
                chars[i] = IdAlphabet[(int)(remaining & 31)];
                remaining >>= 5;
            }
        });
    }
}
