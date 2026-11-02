namespace GameOfLife.Core.Storage;

/// <summary>The two ways a board can be stored.</summary>
public enum BoardStorageKind
{
    /// <summary>
    /// Small boards. Every board is stored with its full cells, compressed. They are only a few hundred
    /// bytes, so a recipe would save little, and reading one is a single lookup.
    /// </summary>
    Full,

    /// <summary>
    /// Large boards. Only key boards keep their full cells: uploads, final boards, and one board every
    /// <see cref="BoardStoragePolicy.KeyBoardInterval"/> generations along a chain. Every other board is a
    /// recipe that names a key board and how many generations to step from it.
    /// </summary>
    Recipe,
}

/// <summary>Decides how each board is stored. This is the only place the two paths are chosen.</summary>
/// <param name="KeyBoardInterval">
/// For recipe boards, the most generations between key boards. A recipe replays at most one less than
/// this many generations, about 15 ms at 256 x 256 with the default of 64.
/// </param>
/// <param name="FullBoardMaxBytes">
/// Boards whose packed cells fit in this many bytes are stored in full. The default of 1,024 bytes
/// covers boards of up to 8,192 cells, such as 32 x 48 or 64 x 64.
/// </param>
public sealed record BoardStoragePolicy(int KeyBoardInterval, int FullBoardMaxBytes)
{
    public static readonly BoardStoragePolicy Default = new(KeyBoardInterval: 64, FullBoardMaxBytes: 1024);

    /// <summary>Small boards are stored in full and large boards as recipes, decided by size alone.</summary>
    public BoardStorageKind KindOf(Board board) =>
        board.PackedCells.Length <= FullBoardMaxBytes ? BoardStorageKind.Full : BoardStorageKind.Recipe;

    /// <summary>Starts planning how to store the generations after a board.</summary>
    /// <param name="stepsFromKey">How far the starting board is from its key board. 0 when it is stored in full.</param>
    public ChainBuilder StartChain(int stepsFromKey) => new(this, stepsFromKey);
}

/// <summary>One generation in a chain to save, linked from the generation before it.</summary>
/// <param name="BoardId">The board's ID.</param>
/// <param name="FullBoard">The board, when it is stored in full. <c>null</c> when it is stored as a recipe.</param>
public sealed record ChainLink(string BoardId, Board? FullBoard);

/// <summary>A stored board and how far it is from the key board it is rebuilt from.</summary>
/// <param name="StepsFromKey">0 when the board is stored in full, otherwise the generations its recipe replays.</param>
public sealed record StoredBoard(Board Board, int StepsFromKey);

/// <summary>IDs found by following saved next links, without rebuilding any boards.</summary>
/// <param name="Ids">Where <c>Ids[i]</c> is generation <c>i + 1</c> after the starting board.</param>
/// <param name="RepeatsFrom">
/// Set when the links loop back. The generation after the last ID is this one, counting the starting board as 0.
/// </param>
public sealed record ChainWalk(IReadOnlyList<string> Ids, int? RepeatsFrom);

/// <summary>
/// Collects consecutive generations as they are computed and decides, one by one, which are stored in
/// full and which as recipes. Only boards stored in full are kept in memory.
/// </summary>
public sealed class ChainBuilder
{
    /// <summary>Generations beyond this are not saved, so one request never holds more than this many links.</summary>
    public const int MaxLinks = 20_000;

    private readonly BoardStoragePolicy _policy;
    private readonly List<ChainLink> _links = [];
    private int _stepsFromKey;

    internal ChainBuilder(BoardStoragePolicy policy, int stepsFromKey)
    {
        _policy = policy;
        _stepsFromKey = stepsFromKey;
    }

    public IReadOnlyList<ChainLink> Links => _links;

    /// <summary>Adds the next generation after the last one added, or after the starting board.</summary>
    public void Add(Board board)
    {
        if (_links.Count >= MaxLinks)
        {
            return;
        }

        bool storeInFull = _policy.KindOf(board) == BoardStorageKind.Full
            || _stepsFromKey + 1 >= _policy.KeyBoardInterval;
        if (storeInFull)
        {
            _links.Add(new ChainLink(board.Id, board));
            _stepsFromKey = 0;
        }
        else
        {
            _links.Add(new ChainLink(board.Id, null));
            _stepsFromKey++;
        }
    }
}
