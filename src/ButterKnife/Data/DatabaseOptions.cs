namespace ButterKnife.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Microsoft.Data.Sqlite connection string. A relative Data Source resolves against the content root.</summary>
    public string ConnectionString { get; set; } = "Data Source=data/butterknife.db";
}
