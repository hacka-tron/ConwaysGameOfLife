using System.Text;
using GameOfLife.Core;
using GameOfLife.Core.Storage;
using GameOfLife.Infrastructure;
using GameOfLife.Tests.Unit;
using LightningDB;
using Microsoft.Extensions.Options;

namespace GameOfLife.Tests.Integration;

public sealed class LmdbBoardStoreTests : IDisposable
{
    private readonly TempStorage _storage = new();

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task BoardsStoredBeforeCompressionStillLoadAndUploadAgain()
    {
        var board = BoardCodecTests.Make("glider-256");
        WriteRaw($"board:{board.Id}", BoardCodecTests.RawFormat(board));

        using var store = new LmdbBoardStore(Options.Create(new StorageOptions { Path = _storage.Path }), new LifeEngine(), BoardStoragePolicy.Default);
        bool created = await store.AddAsync(board, CancellationToken.None);
        var loaded = await store.GetAsync(board.Id, CancellationToken.None);

        // The stored bytes differ from the new compressed form, but they are the same board, so this is not a clash.
        Assert.False(created);
        Assert.True(board.ContentEquals(loaded!.Board));
    }

    [Fact]
    public async Task ADifferentBoardUnderAnExistingIdIsStillRefused()
    {
        var board = BoardCodecTests.Make("glider-256");
        var other = BoardCodecTests.Make("single-1x1");
        WriteRaw($"board:{board.Id}", BoardCodecTests.RawFormat(other));

        using var store = new LmdbBoardStore(Options.Create(new StorageOptions { Path = _storage.Path }), new LifeEngine(), BoardStoragePolicy.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(board, CancellationToken.None));
    }

    private void WriteRaw(string key, byte[] value)
    {
        Directory.CreateDirectory(_storage.Path);
        using var environment = new LightningEnvironment(_storage.Path, new EnvironmentConfiguration { MapSize = 64L * 1024 * 1024 });
        environment.Open(EnvironmentOpenFlags.NoThreadLocalStorage);
        using var transaction = environment.BeginTransaction();
        using var database = transaction.OpenDatabase(configuration: new DatabaseConfiguration { Flags = DatabaseOpenFlags.Create });
        transaction.Put(database, Encoding.ASCII.GetBytes(key), value).ThrowOnError();
        transaction.Commit().ThrowOnError();
    }
}
