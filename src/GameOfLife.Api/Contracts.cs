using GameOfLife.Core;

namespace GameOfLife.Api;

public sealed record UploadBoardRequest
{
    /// <summary>Rows of cells from top to bottom. 1 is a live cell, 0 is a dead cell.</summary>
    /// <example>[[0,1,0],[0,1,0],[0,1,0]]</example>
    public int[][]? Cells { get; init; }

    /// <summary>
    /// "dead" if cells beyond the border are dead, "wrap" if edges wrap around. Optional, and the
    /// server's default is used when it is left out. The rule is part of the board's ID.
    /// </summary>
    /// <example>wrap</example>
    public string? EdgeMode { get; init; }
}

/// <param name="Id">Content-derived ID. The same board always has the same ID.</param>
/// <param name="Width">Number of columns.</param>
/// <param name="Height">Number of rows.</param>
/// <param name="EdgeMode">"dead" if cells beyond the border are dead, "wrap" if edges wrap around.</param>
/// <param name="Cells">Rows of cells from top to bottom. 1 is a live cell, 0 is a dead cell.</param>
public sealed record BoardResponse(string Id, int Width, int Height, string EdgeMode, int[][] Cells)
{
    public static BoardResponse From(Board board)
    {
        var rows = new int[board.Height][];
        for (int y = 0; y < board.Height; y++)
        {
            rows[y] = new int[board.Width];
            for (int x = 0; x < board.Width; x++)
            {
                rows[y][x] = board[x, y] ? 1 : 0;
            }
        }

        return new BoardResponse(board.Id, board.Width, board.Height, board.EdgeMode.ToKey(), rows);
    }
}

/// <param name="Board">The settled board. For an oscillator, the cycle member with the smallest ID.</param>
/// <param name="Steps">Generations from the requested board until it first reaches <paramref name="Board"/>.</param>
/// <param name="Period">1 if the board no longer changes, otherwise the length of the cycle it repeats.</param>
/// <param name="Kind">"still-life" or "oscillator".</param>
public sealed record FinalStateResponse(BoardResponse Board, int Steps, int Period, string Kind)
{
    public static FinalStateResponse From(FinalState final) => new(
        BoardResponse.From(final.Board),
        final.Steps,
        final.Period,
        final.Period == 1 ? "still-life" : "oscillator");
}

/// <param name="MaxWidth">Widest board accepted.</param>
/// <param name="MaxHeight">Tallest board accepted.</param>
/// <param name="MaxSteps">Most generations computed for one request.</param>
/// <param name="DefaultEdgeMode">Edge rule given to uploads that do not name one. It is part of each board's ID.</param>
public sealed record SettingsResponse(int MaxWidth, int MaxHeight, int MaxSteps, string DefaultEdgeMode);
