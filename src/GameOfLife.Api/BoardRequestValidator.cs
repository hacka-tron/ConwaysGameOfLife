using GameOfLife.Core;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace GameOfLife.Api;

public static class BoardRequestValidator
{
    /// <summary>Checks an uploaded grid against the configured limits and converts it to cells.</summary>
    /// <returns><c>false</c> with errors added to <paramref name="errors"/> if the grid is invalid.</returns>
    public static bool TryParse(int[][]? rows, GameOfLifeOptions limits, ModelStateDictionary errors, out bool[,] cells)
    {
        const string key = nameof(UploadBoardRequest.Cells);
        cells = new bool[0, 0];

        if (rows is null || rows.Length == 0)
        {
            errors.AddModelError(key, "The board needs at least one row.");
            return false;
        }

        if (rows.Length > limits.MaxHeight)
        {
            errors.AddModelError(key, $"The board has {rows.Length} rows. The maximum is {limits.MaxHeight}.");
            return false;
        }

        if (rows.Any(row => row is null))
        {
            errors.AddModelError(key, "Rows cannot be null.");
            return false;
        }

        int width = rows[0].Length;
        if (width == 0)
        {
            errors.AddModelError(key, "Rows need at least one cell.");
            return false;
        }

        if (width > limits.MaxWidth)
        {
            errors.AddModelError(key, $"The board has {width} columns. The maximum is {limits.MaxWidth}.");
            return false;
        }

        if (rows.Any(row => row.Length != width))
        {
            errors.AddModelError(key, "Every row must have the same number of cells.");
            return false;
        }

        cells = new bool[rows.Length, width];
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < width; x++)
            {
                switch (rows[y][x])
                {
                    case 0:
                        break;
                    case 1:
                        cells[y, x] = true;
                        break;
                    default:
                        errors.AddModelError(key, $"Cell at row {y}, column {x} is {rows[y][x]}. Cells must be 0 or 1.");
                        return false;
                }
            }
        }

        return true;
    }
}
