using System.ComponentModel.DataAnnotations;

namespace HookScope.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required(
        ErrorMessage = "Database:ConnectionString is required and must point to a writable SQLite database.")]
    public string ConnectionString { get; init; } = string.Empty;
}
