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
    /// Most boards one client may receive per second through playback, which is <c>next</c> and
    /// <c>sequence</c>. The browser UI's top speed is this value.
    /// </summary>
    [Range(1, 1000)]
    public int MaxBoardsPerSecond { get; set; } = 30;

    /// <summary>
    /// Most boards returned by one <c>sequence</c> request. A client may also burst up to twice this
    /// many boards before the playback rate limit applies.
    /// </summary>
    [Range(1, 1000)]
    public int MaxBatchSize { get; set; } = 120;

    /// <summary>
    /// Edge rule for uploads that do not name one. The rule is part of each board's identity,
    /// so boards already stored keep the rule they were uploaded with.
    /// </summary>
    [EnumDataType(typeof(EdgeMode))]
    public EdgeMode DefaultEdgeMode { get; set; } = EdgeMode.Dead;
}
