namespace GameOfLife.Core;

public sealed class BoardNotFoundException(string boardId)
    : Exception($"No board with ID '{boardId}' exists.")
{
    public string BoardId { get; } = boardId;
}

/// <summary>The board did not reach the requested generation or settle within the step limit.</summary>
public sealed class StepLimitExceededException(int maxSteps)
    : Exception($"The board did not settle within {maxSteps} generations.")
{
    public int MaxSteps { get; } = maxSteps;
}
