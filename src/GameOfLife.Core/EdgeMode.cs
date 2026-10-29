namespace GameOfLife.Core;

/// <summary>How cells beyond the border of a board are treated.</summary>
public enum EdgeMode
{
    /// <summary>Cells outside the board are always dead.</summary>
    Dead,

    /// <summary>The board wraps around like a torus, so each edge neighbours the opposite edge.</summary>
    Wrap,
}

public static class EdgeModeKeys
{
    // These keys are part of every board ID and stored row, so they must never change.
    // Renaming an enum member must not change IDs, which is why this is not ToString().
    public static string ToKey(this EdgeMode mode) => mode switch
    {
        EdgeMode.Dead => "dead",
        EdgeMode.Wrap => "wrap",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown edge mode."),
    };

    public static EdgeMode Parse(string key) => key switch
    {
        "dead" => EdgeMode.Dead,
        "wrap" => EdgeMode.Wrap,
        _ => throw new FormatException($"Unknown edge mode key '{key}'."),
    };
}
