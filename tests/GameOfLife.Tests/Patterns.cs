using GameOfLife.Core;

namespace GameOfLife.Tests;

/// <summary>Builds boards from text, where '#' is alive and '.' is dead.</summary>
internal static class Patterns
{
    public static Board Parse(EdgeMode edgeMode, params string[] rows)
    {
        var cells = new bool[rows.Length, rows[0].Length];
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < rows[y].Length; x++)
            {
                cells[y, x] = rows[y][x] == '#';
            }
        }

        return Board.FromCells(cells, edgeMode);
    }

    public static Board Parse(params string[] rows) => Parse(EdgeMode.Dead, rows);

    public static int[][] ToGrid(params string[] rows) =>
        rows.Select(row => row.Select(c => c == '#' ? 1 : 0).ToArray()).ToArray();

    public static string[] ToText(Board board) =>
        Enumerable.Range(0, board.Height)
            .Select(y => new string(Enumerable.Range(0, board.Width).Select(x => board[x, y] ? '#' : '.').ToArray()))
            .ToArray();

    public static readonly string[] BlinkerHorizontal = [".....", ".....", ".###.", ".....", "....."];
    public static readonly string[] BlinkerVertical = [".....", "..#..", "..#..", "..#..", "....."];
    public static readonly string[] Block = ["....", ".##.", ".##.", "...."];

    public static string[] GliderOn(int size)
    {
        var rows = Enumerable.Range(0, size).Select(_ => new string('.', size).ToCharArray()).ToArray();
        rows[0][1] = '#';
        rows[1][2] = '#';
        rows[2][0] = rows[2][1] = rows[2][2] = '#';
        return rows.Select(r => new string(r)).ToArray();
    }
}
