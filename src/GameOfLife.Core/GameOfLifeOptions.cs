using System.ComponentModel.DataAnnotations;

namespace GameOfLife.Core;

/// <summary>Server-side limits and rules, bound from the "GameOfLife" configuration section.</summary>
public sealed class GameOfLifeOptions
{
    public const string SectionName = "GameOfLife";

    /// <summary>Widest board accepted on upload.</summary>
    [Range(1, 4096)]
    public int MaxWidth { get; set; } = 256;

    /// <summary>Tallest board accepted on upload.</summary>
    [Range(1, 4096)]
    public int MaxHeight { get; set; } = 256;

    /// <summary>Most generations computed for a single request before giving up.</summary>
    [Range(1, 1_000_000)]
    public int MaxSteps { get; set; } = 10_000;

    /// <summary>
    /// Edge rule for newly uploaded boards. The rule is part of each board's identity,
    /// so boards already stored keep the rule they were uploaded with.
    /// </summary>
    [EnumDataType(typeof(EdgeMode))]
    public EdgeMode EdgeMode { get; set; } = EdgeMode.Dead;
}
