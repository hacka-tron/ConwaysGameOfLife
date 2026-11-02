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
/// <item><c>board:{id}</c> holds a board stored in full, in the form <see cref="BoardCodec"/> writes.</item>
/// <item><c>recipe:{id}</c> holds a recipe for a large board, the ID of a board stored in full and how many generations to step from it.</item>
/// <item><c>next:{id}</c> holds the ID of the next generation.</item>
/// <item><c>final:{id}</c> holds steps, period and the ID of the final board.</item>
/// </list>
/// Each board is stored either in full or as a recipe, as <see cref="BoardStoragePolicy"/> decides, and
/// the two kinds have separate read and write paths below. Each operation is one short synchronous
/// transaction. LMDB transactions must not span an <c>await</c>, and these calls take at most a few
/// milliseconds, so there is nothing to gain from doing so.
/// </remarks>
public sealed class LmdbBoardStore : IBoardStore, IDisposable
{
    private readonly LightningEnvironment _environment;
    private readonly LightningDatabase _database;
    private readonly ILifeEngine _engine;
    private readonly BoardStoragePolicy _policy;

    public LmdbBoardStore(IOptions<StorageOptions> options, ILifeEngine engine, BoardStoragePolicy policy)
    {
        _engine = engine;
        _policy = policy;
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
        if (ReadBoard(transaction, id, cancellationToken) is not { } stored)
        {
            return Task.FromResult<BoardRecord?>(null);
        }

        string? nextId = ReadNextId(transaction, id);
        StoredFinalState? final = transaction.TryGet(_database, Key("final", id), out byte[]? finalValue)
            ? DecodeFinal(finalValue!)
            : null;

        return Task.FromResult<BoardRecord?>(new BoardRecord(stored.Board, nextId, final, stored.StepsFromKey));
    }

    public Task<IReadOnlyList<StoredBoard>> GetChainAsync(string fromId, int maxLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chain = new List<StoredBoard>();
        using var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly);
        string id = fromId;
        while (chain.Count < maxLength && ReadNextId(transaction, id) is { } nextId)
        {
            // A recipe board right after a board already rebuilt is one step on from it, which is much
            // cheaper than replaying it from its key board.
            StoredBoard? stored = chain.Count > 0 && !transaction.ContainsKey(_database, Key("board", nextId))
                ? StepRecipeBoard(transaction, chain[^1].Board, nextId)
                : ReadBoard(transaction, nextId, cancellationToken);
            if (stored is null)
            {
                break;
            }

            chain.Add(stored);
            id = nextId;
        }

        return Task.FromResult<IReadOnlyList<StoredBoard>>(chain);
    }

    public Task<ChainWalk> WalkAsync(string fromId, int maxLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = new List<string>();
        var generationOf = new Dictionary<string, int> { [fromId] = 0 };
        using var transaction = _environment.BeginTransaction(TransactionBeginFlags.ReadOnly);
        string id = fromId;
        while (ids.Count < maxLength && ReadNextId(transaction, id) is { } nextId)
        {
            if (generationOf.TryGetValue(nextId, out int repeatsFrom))
            {
                return Task.FromResult(new ChainWalk(ids, repeatsFrom));
            }

            ids.Add(nextId);
            generationOf.Add(nextId, ids.Count);
            id = nextId;
        }

        return Task.FromResult(new ChainWalk(ids, null));
    }

    public Task<bool> AddAsync(Board board, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Write(transaction =>
            transaction.ContainsKey(_database, Key("recipe", board.Id))
                ? false
                : WriteFullBoard(transaction, board)));
    }

    public Task SaveChainAsync(string fromId, IReadOnlyList<ChainLink> links, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(transaction =>
        {
            // Recipes are written relative to the most recent board stored in full, starting from the
            // board before the chain. Each board is written before the link to it, all in one commit.
            var (keyId, stepsFromKey) = KeyOf(transaction, fromId);
            string previous = fromId;
            foreach (var link in links)
            {
                if (link.FullBoard is { } board)
                {
                    WriteFullBoard(transaction, board);
                }
                else
                {
                    WriteRecipe(transaction, link.BoardId, keyId, stepsFromKey + 1);
                }

                Put(transaction, Key("next", previous), Encoding.ASCII.GetBytes(link.BoardId));
                (keyId, stepsFromKey) = KeyOf(transaction, link.BoardId);
                previous = link.BoardId;
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
            // Final boards are always stored in full, because every jump past the final state starts
            // from them. The final board is written before anything points at it, in the same commit.
            WriteFullBoard(transaction, finalBoard);
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

    // ------------------------------------------------------------------ Reading either kind

    private StoredBoard? ReadBoard(LightningTransaction transaction, string id, CancellationToken cancellationToken)
    {
        if (transaction.TryGet(_database, Key("board", id), out byte[]? value))
        {
            return new StoredBoard(ReadFullBoard(value!), 0);
        }

        if (transaction.TryGet(_database, Key("recipe", id), out byte[]? recipe))
        {
            return ReadRecipeBoard(transaction, id, recipe!, cancellationToken);
        }

        return null;
    }

    private string? ReadNextId(LightningTransaction transaction, string id) =>
        transaction.TryGet(_database, Key("next", id), out byte[]? value) ? Encoding.ASCII.GetString(value!) : null;

    /// <summary>The board stored in full that later recipes in a chain replay from, and how far this board is from it.</summary>
    private (string KeyId, int StepsFromKey) KeyOf(LightningTransaction transaction, string id)
    {
        if (transaction.ContainsKey(_database, Key("board", id)))
        {
            return (id, 0);
        }

        if (transaction.TryGet(_database, Key("recipe", id), out byte[]? recipe))
        {
            var (keyId, steps) = DecodeRecipe(recipe!);
            return (keyId, steps);
        }

        throw new InvalidOperationException($"Board '{id}' is linked to but not stored.");
    }

    // ------------------------------------------------------------------ Boards stored in full

    private static Board ReadFullBoard(byte[] value) => BoardCodec.Decode(value);

    /// <returns><c>true</c> if the board was new.</returns>
    private bool WriteFullBoard(LightningTransaction transaction, Board board)
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

    // ------------------------------------------------------------------ Boards stored as recipes

    /// <summary>Rebuilds a recipe board by loading its key board and stepping it forward.</summary>
    private StoredBoard ReadRecipeBoard(LightningTransaction transaction, string id, byte[] recipe, CancellationToken cancellationToken)
    {
        var (keyId, steps) = DecodeRecipe(recipe);
        if (!transaction.TryGet(_database, Key("board", keyId), out byte[]? keyValue))
        {
            throw new InvalidDataException($"The key board '{keyId}' for recipe board '{id}' is not stored.");
        }

        var board = ReadFullBoard(keyValue!);
        for (int i = 0; i < steps; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            board = _engine.Step(board);
        }

        return new StoredBoard(Verified(board, id), steps);
    }

    /// <summary>Rebuilds a recipe board from the board just before it, which is one step instead of a replay.</summary>
    private StoredBoard? StepRecipeBoard(LightningTransaction transaction, Board previous, string id)
    {
        if (!transaction.TryGet(_database, Key("recipe", id), out byte[]? recipe))
        {
            return null;
        }

        return new StoredBoard(Verified(_engine.Step(previous), id), DecodeRecipe(recipe!).Steps);
    }

    /// <summary>
    /// Writes a recipe unless the board is already stored in full or has a recipe at least as short.
    /// Every recipe for an ID rebuilds the same board, so keeping the shortest only makes reads faster,
    /// and it means a recipe written after this one in the chain is never further from its key board
    /// than planned.
    /// </summary>
    private void WriteRecipe(LightningTransaction transaction, string id, string keyId, int steps)
    {
        if (steps >= _policy.KeyBoardInterval)
        {
            throw new InvalidOperationException(
                $"Recipe for '{id}' would replay {steps} generations, more than the key board interval allows.");
        }

        if (transaction.ContainsKey(_database, Key("board", id)))
        {
            return;
        }

        byte[] key = Key("recipe", id);
        if (transaction.TryGet(_database, key, out byte[]? existing) && DecodeRecipe(existing!).Steps <= steps)
        {
            return;
        }

        Put(transaction, key, EncodeRecipe(keyId, steps));
    }

    private static Board Verified(Board rebuilt, string id) =>
        rebuilt.Id == id
            ? rebuilt
            : throw new InvalidDataException($"The recipe for board '{id}' rebuilt a different board.");

    private static byte[] EncodeRecipe(string keyId, int steps)
    {
        var value = new byte[4 + keyId.Length];
        BinaryPrimitives.WriteInt32LittleEndian(value, steps);
        Encoding.ASCII.GetBytes(keyId, value.AsSpan(4));
        return value;
    }

    private static (string KeyId, int Steps) DecodeRecipe(byte[] value) =>
        (Encoding.ASCII.GetString(value, 4, value.Length - 4), BinaryPrimitives.ReadInt32LittleEndian(value));

    // ------------------------------------------------------------------ Shared helpers

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
