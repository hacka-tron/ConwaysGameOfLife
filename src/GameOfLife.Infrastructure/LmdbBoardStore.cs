using System.Buffers.Binary;
using System.Text;
using GameOfLife.Core;
using GameOfLife.Core.Storage;
using LightningDB;
using Microsoft.Extensions.Options;

namespace GameOfLife.Infrastructure;

/// <summary>
/// Stores boards in LMDB, an embedded key-value store. Every commit is flushed to disk before it
/// returns, and LMDB never overwrites live pages, so a crash cannot lose or corrupt committed data.
/// </summary>
/// <remarks>
/// Keys and values:
/// <list type="bullet">
/// <item><c>board:{id}</c> holds the board in the form <see cref="BoardCodec"/> writes, compressed when that is smaller.</item>
/// <item><c>next:{id}</c> holds the ID of the next generation.</item>
/// <item><c>final:{id}</c> holds steps, period and the ID of the final board.</item>
/// </list>
/// Each operation is one short synchronous transaction. LMDB transactions must not span an
/// <c>await</c>, and these calls take microseconds, so there is nothing to gain from doing so.
/// </remarks>
public sealed class LmdbBoardStore : IBoardStore, IDisposable
{
    private readonly LightningEnvironment _environment;
    private readonly LightningDatabase _database;

    public LmdbBoardStore(IOptions<StorageOptions> options)
    {
        var settings = options.Value;
        Directory.CreateDirectory(settings.Path);
        _environment = new LightningEnvironment(settings.Path, new EnvironmentConfiguration { MapSize = settings.MaxSizeBytes });

        // Reader slots belong to transactions rather than threads, because ASP.NET runs requests on a thread pool.
        // Sync flags are left at their defaults, so every commit is durable.
        _environment.Open(EnvironmentOpenFlags.NoThreadLocalStorage);

        using var transaction = _environment.BeginTransaction();
        _database = transaction.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
        transaction.Commit().ThrowOnError();
    }

    public Task<BoardRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly);
        if (!transaction.TryGet(_database, Key("board", id), out byte[]? boardValue))
        {
            return Task.FromResult<BoardRecord?>(null);
        }

        string? nextId = transaction.TryGet(_database, Key("next", id), out byte[]? nextValue)
            ? Encoding.ASCII.GetString(nextValue!)
            : null;
        StoredFinalState? final = transaction.TryGet(_database, Key("final", id), out byte[]? finalValue)
            ? DecodeFinal(finalValue!)
            : null;

        return Task.FromResult<BoardRecord?>(new BoardRecord(BoardCodec.Decode(boardValue!), nextId, final));
    }

    public Task<IReadOnlyList<Board>> GetChainAsync(string fromId, int maxLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chain = new List<Board>();
        using var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly);
        string id = fromId;
        while (chain.Count < maxLength
            && transaction.TryGet(_database, Key("next", id), out byte[]? nextValue)
            && transaction.TryGet(_database, Key("board", id = Encoding.ASCII.GetString(nextValue!)), out byte[]? boardValue))
        {
            chain.Add(BoardCodec.Decode(boardValue!));
        }

        return Task.FromResult<IReadOnlyList<Board>>(chain);
    }

    public Task<bool> AddAsync(Board board, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Write(transaction => PutBoard(transaction, board)));
    }

    public Task SaveChainAsync(string fromId, IReadOnlyList<Board> chain, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(transaction =>
        {
            // Each board is written before the link to it, all in one commit.
            string previous = fromId;
            foreach (var board in chain)
            {
                PutBoard(transaction, board);
                Put(transaction, Key("next", previous), Encoding.ASCII.GetBytes(board.Id));
                previous = board.Id;
            }

            return true;
        });
        return Task.CompletedTask;
    }

    public Task<StoredFinalState?> GetFinalAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly);
        return Task.FromResult(transaction.TryGet(_database, Key("final", id), out byte[]? value) ? DecodeFinal(value!) : null);
    }

    public Task SaveFinalsAsync(Board finalBoard, int period, IReadOnlyCollection<BoardSteps> boards, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(transaction =>
        {
            // The final board is written before anything points at it, in the same commit.
            PutBoard(transaction, finalBoard);
            Put(transaction, Key("final", finalBoard.Id), EncodeFinal(finalBoard.Id, 0, period));
            foreach (var board in boards)
            {
                Put(transaction, Key("final", board.BoardId), EncodeFinal(finalBoard.Id, board.Steps, period));
            }

            return true;
        });
        return Task.CompletedTask;
    }

    /// <summary>Opens and closes a read transaction, to check the store is usable.</summary>
    public void Ping()
    {
        using var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly);
    }

    public void Dispose()
    {
        _database.Dispose();
        _environment.Dispose();
    }

    private T Write<T>(Func<LightningTransaction, T> work)
    {
        try
        {
            using var transaction = _environment.BeginTransaction();
            T result = work(transaction);
            transaction.Commit().ThrowOnError();
            return result;
        }
        catch (LightningException ex) when (ex.StatusCode == (int)MDBResultCode.MapFull)
        {
            throw new StorageFullException(ex);
        }
    }

    /// <returns><c>true</c> if the board was new.</returns>
    private bool PutBoard(LightningTransaction transaction, Board board)
    {
        byte[] key = Key("board", board.Id);
        byte[] value = BoardCodec.Encode(board);
        var result = transaction.Put(_database, key, value, PutOptions.NoOverwrite);
        if (result == MDBResultCode.KeyExist)
        {
            // A SHA-256 collision is practically impossible, but checking is cheap and turns
            // silent corruption into a loud failure. The boards are compared rather than the bytes,
            // because a board stored in an older format has different bytes for the same cells.
            transaction.TryGet(_database, key, out byte[]? stored);
            if (!BoardCodec.Decode(stored).ContentEquals(board))
            {
                throw new InvalidOperationException($"Two different boards hashed to the same ID '{board.Id}'.");
            }

            return false;
        }

        result.ThrowOnError();
        return true;
    }

    private void Put(LightningTransaction transaction, byte[] key, byte[] value) =>
        transaction.Put(_database, key, value).ThrowOnError();

    private static byte[] Key(string kind, string id) => Encoding.ASCII.GetBytes($"{kind}:{id}");

    private static byte[] EncodeFinal(string boardId, int steps, int period)
    {
        var value = new byte[8 + boardId.Length];
        BinaryPrimitives.WriteInt32LittleEndian(value, steps);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), period);
        Encoding.ASCII.GetBytes(boardId, value.AsSpan(8));
        return value;
    }

    private static StoredFinalState DecodeFinal(byte[] value) => new(
        Encoding.ASCII.GetString(value, 8, value.Length - 8),
        BinaryPrimitives.ReadInt32LittleEndian(value),
        BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(4)));
}
