using GameOfLife.Core;
using GameOfLife.Core.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace GameOfLife.Infrastructure;

/// <summary>
/// Keeps recently read boards in process memory in front of another store.
/// Boards never change, and their next and final links only go from unknown to known.
/// So a stale entry can at worst cause one recomputation that writes identical data.
/// </summary>
public sealed class CachedBoardStore(IBoardStore inner, long sizeLimitBytes) : IBoardStore, IDisposable
{
    // Rough per-entry cost of the record, strings and cache bookkeeping, on top of the cells.
    private const int EntryOverheadBytes = 512;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = sizeLimitBytes });

    public async Task<BoardRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(id, out BoardRecord? cached))
        {
            return cached;
        }

        var record = await inner.GetAsync(id, cancellationToken);
        if (record is not null)
        {
            _cache.Set(id, record, new MemoryCacheEntryOptions
            {
                Size = record.Board.PackedCells.Length + EntryOverheadBytes,
            });
        }

        return record;
    }

    public Task<bool> AddAsync(Board board, CancellationToken cancellationToken) =>
        inner.AddAsync(board, cancellationToken);

    public async Task SaveNextAsync(string fromId, Board next, CancellationToken cancellationToken)
    {
        await inner.SaveNextAsync(fromId, next, cancellationToken);
        _cache.Remove(fromId);
    }

    public async Task SaveFinalAsync(string fromId, FinalState final, CancellationToken cancellationToken)
    {
        await inner.SaveFinalAsync(fromId, final, cancellationToken);
        _cache.Remove(fromId);
        _cache.Remove(final.Board.Id);
    }

    public void Dispose() => _cache.Dispose();
}
