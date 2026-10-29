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

    /// <summary>Stores the board unless it already exists.</summary>
    /// <returns><c>true</c> if the board was new.</returns>
    Task<bool> AddAsync(Board board, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="next"/> and records it as the successor of <paramref name="fromId"/>, atomically.</summary>
    Task SaveNextAsync(string fromId, Board next, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the final board and records the final state of <paramref name="fromId"/>, atomically.
    /// The final board is also recorded as its own final state.
    /// </summary>
    Task SaveFinalAsync(string fromId, FinalState final, CancellationToken cancellationToken);
}

/// <summary>The store has reached its configured maximum size.</summary>
public sealed class StorageFullException(Exception inner)
    : Exception("Board storage is full. Raise Storage:MaxSizeBytes to allow more boards.", inner);
