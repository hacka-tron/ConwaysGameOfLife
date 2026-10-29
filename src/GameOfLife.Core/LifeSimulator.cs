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

/// <param name="BoardAtTarget">The board at the requested generation, when one was requested.</param>
/// <param name="Final">The final state, when a cycle was found on the way.</param>
public sealed record SimulationResult(Board? BoardAtTarget, FinalState? Final);

public sealed class LifeSimulator(ILifeEngine engine)
{
    /// <summary>
    /// Steps forward from <paramref name="start"/> until generation <paramref name="target"/> is reached
    /// or the board repeats, whichever comes first. Once it repeats, any later generation is found by
    /// arithmetic on the cycle instead of by stepping. Without a target, it runs until the board repeats.
    /// </summary>
    /// <returns>The result, or <c>null</c> if neither happened within <paramref name="maxSteps"/> generations.</returns>
    public SimulationResult? Run(Board start, long? target, int maxSteps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (target < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Generation cannot be negative.");
        }

        // Only IDs are kept, not boards, so memory stays small even at the step limit.
        var firstSeenAt = new Dictionary<string, int> { [start.Id] = 0 };
        var ids = new List<string> { start.Id };
        Board current = start;

        for (int generation = 0; ; )
        {
            if (generation == target)
            {
                return new SimulationResult(current, null);
            }

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

            firstSeenAt.Add(current.Id, generation);
            ids.Add(current.Id);
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

        return new SimulationResult(atTarget, final);
    }
}
