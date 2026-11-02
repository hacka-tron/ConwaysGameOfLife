namespace GameOfLife.Core.Storage;

/// <summary>The stored final state of a board, pointing at the canonical cycle member by ID.</summary>
public sealed record StoredFinalState(string BoardId, int Steps, int Period);

/// <summary>A stored board plus whatever has been learned about its future so far.</summary>
/// <param name="NextId">ID of the next generation, once it has been computed.</param>
/// <param name="Final">Final state, once it has been computed.</param>
public sealed record BoardRecord(Board Board, string? NextId, StoredFinalState? Final);

/// <summary>
/// Durable key-value storage of boards and their links. Every board whose ID is returned to a
/// client must be stored, because an ID cannot be turned back into a board.
/// </summary>
/// <remarks>
/// Every value is fully determined by its key, so writing the same key twice always writes the
/// same value. That keeps concurrent requests safe without locking and lets implementations
/// replicate or cache freely. The methods are asynchronous so a networked store can implement them.
/// </remarks>
public interface IBoardStore
{
    Task<BoardRecord?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Follows saved next links from <paramref name="fromId"/> and returns up to <paramref name="maxLength"/>
    /// boards, in one read. It stops early at the first board with no saved next link.
    /// </summary>
    Task<IReadOnlyList<Board>> GetChainAsync(string fromId, int maxLength, CancellationToken cancellationToken);

    /// <summary>Stores the board unless it already exists.</summary>
    /// <returns><c>true</c> if the board was new.</returns>
    Task<bool> AddAsync(Board board, CancellationToken cancellationToken);

    /// <summary>
    /// Stores consecutive generations and the next links between them, atomically. <paramref name="chain"/>
    /// starts with the generation after <paramref name="fromId"/>, and each board is the next of the one before.
    /// </summary>
    Task SaveChainAsync(string fromId, IReadOnlyList<Board> chain, CancellationToken cancellationToken);

    /// <summary>
    /// The final state saved for <paramref name="id"/>, or <c>null</c> if none is known. This works for
    /// boards that were passed through on the way to a final state but never stored themselves.
    /// </summary>
    Task<StoredFinalState?> GetFinalAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the final board and records a final state for each of <paramref name="boards"/>, atomically.
    /// They all lead to <paramref name="finalBoard"/> with the same <paramref name="period"/>, and only their
    /// steps differ. The final board is also recorded as its own final state.
    /// </summary>
    /// <remarks>Only the final board itself is stored. The other boards get a final state record only.</remarks>
    Task SaveFinalsAsync(Board finalBoard, int period, IReadOnlyCollection<BoardSteps> boards, CancellationToken cancellationToken);
}

/// <summary>The store has reached its configured maximum size.</summary>
public sealed class StorageFullException(Exception inner)
    : Exception("Board storage is full. Raise Storage:MaxSizeBytes to allow more boards.", inner);
