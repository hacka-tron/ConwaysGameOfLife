using GameOfLife.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameOfLife.Core;

/// <param name="Boards">Consecutive generations, where <c>Boards[i]</c> is generation <c>i + 1</c> after the requested board.</param>
/// <param name="RepeatsFrom">
/// When the sequence repeats, the generation that follows the last board, counted from the requested board
/// as generation 0. A still life repeats from its own generation, so playback can loop with no more requests.
/// </param>
public sealed record BoardSequence(IReadOnlyList<Board> Boards, int? RepeatsFrom);

/// <summary>
/// The four operations of the API. Results are stored as they are computed, so asking
/// the same question about the same board twice only computes the answer once. Every generation
/// that gets computed is saved, small boards in full and large boards as recipes, as
/// <see cref="BoardStoragePolicy"/> decides.
/// </summary>
public sealed class BoardService(
    IBoardStore store,
    LifeSimulator simulator,
    BoardStoragePolicy storagePolicy,
    IOptions<GameOfLifeOptions> options,
    ILogger<BoardService> logger)
{
    private int MaxSteps => options.Value.MaxSteps;

    /// <returns>The board and whether it was new. Uploading an existing board returns the same ID.</returns>
    public async Task<(Board Board, bool Created)> UploadAsync(bool[,] cells, EdgeMode edgeMode, CancellationToken cancellationToken)
    {
        var board = Board.FromCells(cells, edgeMode);
        bool created = await store.AddAsync(board, cancellationToken);
        return (board, created);
    }

    public async Task<Board> GetAsync(string id, CancellationToken cancellationToken) =>
        (await GetRecordAsync(id, cancellationToken)).Board;

    public async Task<Board> GetNextAsync(string id, CancellationToken cancellationToken)
    {
        var record = await GetRecordAsync(id, cancellationToken);
        if (record.NextId is not null)
        {
            return await GetAsync(record.NextId, cancellationToken);
        }

        var next = simulator.Advance(record.Board, 1, cancellationToken);
        var chain = storagePolicy.StartChain(record.StepsFromKey);
        chain.Add(next);
        await store.SaveChainAsync(id, chain.Links, cancellationToken);
        return next;
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> consecutive generations after the board, for playback.
    /// Saved next links are read in one read and the rest are computed, then saved in one write.
    /// The batch ends early when a generation repeats one already in it, or the starting board.
    /// </summary>
    public async Task<BoardSequence> GetSequenceAsync(string id, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var record = await GetRecordAsync(id, cancellationToken);
        var saved = await store.GetChainAsync(id, count, cancellationToken);
        var generationOf = new Dictionary<string, int> { [id] = 0 };
        var boards = new List<Board>(count);
        ChainBuilder? computed = null;
        string computedFrom = id;
        Board current = record.Board;
        int currentStepsFromKey = record.StepsFromKey;

        for (int generation = 1; generation <= count; generation++)
        {
            Board next;
            if (generation <= saved.Count)
            {
                next = saved[generation - 1].Board;
                currentStepsFromKey = saved[generation - 1].StepsFromKey;
            }
            else
            {
                // The saved chain ended here, so everything from this board onwards is computed.
                if (computed is null)
                {
                    computed = storagePolicy.StartChain(currentStepsFromKey);
                    computedFrom = current.Id;
                }

                next = simulator.Advance(current, 1, cancellationToken);
                computed.Add(next);
            }

            if (generationOf.TryGetValue(next.Id, out int repeatsFrom))
            {
                await SaveComputedAsync();
                return new BoardSequence(boards, repeatsFrom);
            }

            generationOf.Add(next.Id, generation);
            boards.Add(next);
            current = next;
        }

        await SaveComputedAsync();
        return new BoardSequence(boards, null);

        async Task SaveComputedAsync()
        {
            if (computed is { Links.Count: > 0 })
            {
                await store.SaveChainAsync(computedFrom, computed.Links, cancellationToken);
            }
        }
    }

    public async Task<Board> GetGenerationAsync(string id, long generations, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generations);
        if (generations <= 1)
        {
            return generations == 0
                ? await GetAsync(id, cancellationToken)
                : await GetNextAsync(id, cancellationToken);
        }

        // Only the final state record is needed here, so the starting board is not rebuilt yet. For a large
        // board stored as a recipe, that saves replaying it when the answer comes from saved links.
        if (await store.GetFinalAsync(id, cancellationToken) is { } final && generations >= final.Steps)
        {
            // Once a board has settled, only its position within the cycle matters.
            var finalBoard = await GetAsync(final.BoardId, cancellationToken);
            int offset = (int)((generations - final.Steps) % final.Period);
            var settled = simulator.Advance(finalBoard, offset, cancellationToken);
            await store.AddAsync(settled, cancellationToken);
            return settled;
        }

        // Saved next links lead straight to boards that are already stored, so follow them first.
        // Every board on them is stored in full or as a recipe, so nothing needs computing on the way.
        var walk = await store.WalkAsync(id, (int)Math.Min(generations, MaxSteps), cancellationToken);
        if (walk.Ids.Count >= generations)
        {
            return await GetAsync(walk.Ids[(int)generations - 1], cancellationToken);
        }

        if (walk.RepeatsFrom is int loopStart)
        {
            int period = walk.Ids.Count + 1 - loopStart;
            long position = loopStart + (generations - loopStart) % period;
            return await GetAsync(position == 0 ? id : walk.Ids[(int)position - 1], cancellationToken);
        }

        // Compute the rest from where the saved links end. The generations walked count against the step
        // limit, so whether a request succeeds never depends on what earlier requests happened to save.
        // If the shortcut cannot finish within what is left, run from the start exactly as if nothing
        // were saved, which gives the same answer the endpoint has always given.
        SimulationResult? run = null;
        if (walk.Ids.Count > 0)
        {
            string fromId = walk.Ids[^1];
            var from = await GetRecordAsync(fromId, cancellationToken);
            run = await RunAndSaveAsync(fromId, from, generations - walk.Ids.Count, MaxSteps - walk.Ids.Count, cancellationToken);
        }

        if (run is null)
        {
            var record = await GetRecordAsync(id, cancellationToken);
            run = await RunAndSaveAsync(id, record, generations, MaxSteps, cancellationToken) ?? throw LimitExceeded(id);
        }

        var result = run.BoardAtTarget!;

        // Stored so the returned ID can be used in later requests, unless the run already saved it.
        await store.AddAsync(result, cancellationToken);
        return result;
    }

    public async Task<FinalState> GetFinalAsync(string id, CancellationToken cancellationToken)
    {
        var record = await GetRecordAsync(id, cancellationToken);
        if (record.Final is { } known)
        {
            var board = await GetAsync(known.BoardId, cancellationToken);
            return new FinalState(board, known.Steps, known.Period);
        }

        var run = await RunAndSaveAsync(id, record, target: null, MaxSteps, cancellationToken) ?? throw LimitExceeded(id);
        return run.Final!;
    }

    /// <summary>
    /// Runs the simulator from a stored board for at most <paramref name="maxSteps"/> generations and saves
    /// every generation it computed, with the next links between them, even when the run stops at the
    /// limit. Later requests can then load any of those boards, or walk the links, instead of computing them.
    /// </summary>
    private async Task<SimulationResult?> RunAndSaveAsync(
        string id, BoardRecord record, long? target, int maxSteps, CancellationToken cancellationToken)
    {
        var chain = storagePolicy.StartChain(record.StepsFromKey);
        var run = await simulator.RunAsync(record.Board, target, maxSteps, FindKnownFinalAsync, cancellationToken, chain.Add);
        if (chain.Links.Count > 0)
        {
            await store.SaveChainAsync(id, chain.Links, cancellationToken);
        }

        if (run?.Final is not null)
        {
            await SaveFinalsAsync(id, run, cancellationToken);
        }

        return run;
    }

    /// <summary>
    /// Saves the final state of every board the run passed through, not just the one asked about.
    /// A later run that reaches any of them can then stop there.
    /// </summary>
    private async Task SaveFinalsAsync(string id, SimulationResult run, CancellationToken cancellationToken)
    {
        var final = run.Final!;
        var boards = run.FinalStepsAlongPath().ToList();
        if (boards.Count == 0)
        {
            // The run started on a board with a known final state, so there is nothing new to save.
            return;
        }

        logger.LogInformation(
            "Board {BoardId} settles after {Steps} generations into a cycle of period {Period}. Saving the final state of {Count} boards",
            id, final.Steps, final.Period, boards.Count);
        await store.SaveFinalsAsync(final.Board, final.Period, boards, cancellationToken);
    }

    private async ValueTask<FinalState?> FindKnownFinalAsync(string id, CancellationToken cancellationToken)
    {
        if (await store.GetFinalAsync(id, cancellationToken) is not { } known)
        {
            return null;
        }

        var board = await GetAsync(known.BoardId, cancellationToken);
        return new FinalState(board, known.Steps, known.Period);
    }

    private async Task<BoardRecord> GetRecordAsync(string id, CancellationToken cancellationToken) =>
        await store.GetAsync(id, cancellationToken) ?? throw new BoardNotFoundException(id);

    private StepLimitExceededException LimitExceeded(string id)
    {
        logger.LogWarning("Board {BoardId} did not settle within {MaxSteps} generations", id, MaxSteps);
        return new StepLimitExceededException(MaxSteps);
    }
}
