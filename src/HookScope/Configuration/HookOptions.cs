using System.ComponentModel.DataAnnotations;

namespace HookScope.Configuration;

public sealed class HookOptions
{
    public const string SectionName = "Hooks";

    [Required(
        ErrorMessage = "Hooks:Secret is required. Set it through configuration, such as the Hooks__Secret environment variable.")]
    [MinLength(16, ErrorMessage = "Hooks:Secret must contain at least 16 characters.")]
    public string Secret { get; init; } = string.Empty;

    [Range(1, 10_485_760)]
    public int MaximumPayloadBytes { get; init; } = 1_048_576;

    [Range(10, 60_000)]
    public int WorkerPollIntervalMilliseconds { get; init; } = 500;
}
