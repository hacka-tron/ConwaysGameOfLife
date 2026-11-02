namespace GameOfLife.Core;

/// <summary>
/// Where a board ends up. Every board eventually repeats, either as a still life (period 1)
/// or an oscillator. <see cref="Board"/> is the cycle member with the smallest ID, so every
/// board that leads into the same cycle reports the same final state.
/// </summary>
/// <param name="Board">The canonical member of the cycle.</param>
/// <param name="Steps">Generations from the starting board until it first reaches <paramref name="Board"/>.</param>
/// <param name="Period">Length of the cycle. 1 means the board no longer changes.</param>
public sealed record FinalState(Board Board, int Steps, int Period);

/// <summary>How many generations a board takes to first reach its final board.</summary>
public readonly record struct BoardSteps(string BoardId, int Steps);

/// <summary>Looks up a final state saved by an earlier run, or returns <c>null</c> if none is known.</summary>
public delegate ValueTask<FinalState?> FinalStateLookup(string boardId, CancellationToken cancellationToken);

/// <param name="BoardAtTarget">The board at the requested generation, when one was requested.</param>
/// <param name="Final">The final state of the starting board, when it was found on the way.</param>
/// <param name="Path">IDs of the boards the run stepped through, where <c>Path[g]</c> is generation <c>g</c>.</param>
public sealed record SimulationResult(Board? BoardAtTarget, FinalState? Final, IReadOnlyList<string> Path)
{
    /// <summary>
    /// The final state of every board on <see cref="Path"/>. They all share the starting board's final
    /// board and period, so only the steps differ. Empty if the final state was not found.
    /// </summary>
    public IEnumerable<BoardSteps> FinalStepsAlongPath()
    {
        if (Final is not { } final)
        {
            yield break;
        }

        for (int generation = 0; generation < Path.Count; generation++)
        {
            // Boards up to the first visit of the final board count down to it. Cycle members past
            // that point go round the cycle once more to come back to it.
            int remaining = final.Steps - generation;
            int steps = remaining >= 0 ? remaining : remaining + final.Period;
            yield return new BoardSteps(Path[generation], steps);
        }
    }
}

public sealed class LifeSimulator(ILifeEngine engine)
{
    /// <summary>
    /// Steps forward from <paramref name="start"/> until generation <paramref name="target"/> is reached
    /// or the final state is known, whichever comes first. The final state is known when the board
    /// repeats, or when it reaches a board whose final state <paramref name="knownFinal"/> returns.
    /// After that, any later generation is found by arithmetic on the cycle instead of by stepping.
    /// Without a target, it runs until the final state is known.
    /// </summary>
    /// <returns>The result, or <c>null</c> if neither happened within <paramref name="maxSteps"/> generations.</returns>
    public async Task<SimulationResult?> RunAsync(
        Board start, long? target, int maxSteps, FinalStateLookup? knownFinal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (target < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Generation cannot be negative.");
        }

        // Only IDs are kept, not boards, so memory stays small even at the step limit.
        var firstSeenAt = new Dictionary<string, int>();
        var ids = new List<string>();
        Board current = start;

        for (int generation = 0; ; )
        {
            if (generation == target)
            {
                return new SimulationResult(current, null, ids);
            }

            if (knownFinal is not null && await knownFinal(current.Id, cancellationToken) is { } known)
            {
                var final = known with { Steps = checked(generation + known.Steps) };
                return ContinueFromKnown(current, generation, final, ids, target, maxSteps, cancellationToken);
            }

            firstSeenAt.Add(current.Id, generation);
            ids.Add(current.Id);

            if (generation == maxSteps)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            current = engine.Step(current);
            generation++;

            if (firstSeenAt.TryGetValue(current.Id, out int cycleStart))
            {
                return ResolveCycle(current, cycleStart, generation - cycleStart, ids, target, cancellationToken);
            }
        }
    }

    /// <summary>Steps <paramref name="board"/> forward a fixed number of generations.</summary>
    public Board Advance(Board board, int generations, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generations);
        for (int i = 0; i < generations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            board = engine.Step(board);
        }

        return board;
    }

    // `current` is the board at `generation`, whose final state was saved by an earlier run.
    // `final` is the starting board's final state, worked out from it.
    private SimulationResult? ContinueFromKnown(
        Board current, int generation, FinalState final, List<string> ids, long? target, int maxSteps,
        CancellationToken cancellationToken)
    {
        if (target is not long t)
        {
            return new SimulationResult(null, final, ids);
        }

        if (t >= final.Steps)
        {
            var atTarget = Advance(final.Board, (int)((t - final.Steps) % final.Period), cancellationToken);
            return new SimulationResult(atTarget, final, ids);
        }

        // The target comes before the final board, so step on to it. The boards from here on are not
        // added to the path, because the run that saved this board's final state saved theirs too.
        for (; generation < t; generation++)
        {
            if (generation == maxSteps)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            current = engine.Step(current);
        }

        return new SimulationResult(current, final, ids);
    }

    // `entry` is the board at generation `cycleStart`, which has just been seen again.
    private SimulationResult ResolveCycle(
        Board entry, int cycleStart, int period, List<string> ids, long? target, CancellationToken cancellationToken)
    {
        int canonicalOffset = 0;
        for (int k = 1; k < period; k++)
        {
            if (string.CompareOrdinal(ids[cycleStart + k], ids[cycleStart + canonicalOffset]) < 0)
            {
                canonicalOffset = k;
            }
        }

        var final = new FinalState(Advance(entry, canonicalOffset, cancellationToken), cycleStart + canonicalOffset, period);

        // The target was not reached before the repeat, so it lies at or beyond `cycleStart`.
        Board? atTarget = target is long t
            ? Advance(entry, (int)((t - cycleStart) % period), cancellationToken)
            : null;

        return new SimulationResult(atTarget, final, ids);
    }
}
