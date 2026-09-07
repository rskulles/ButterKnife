namespace ButterKnife.Data;

public sealed class ConversationStoreOptions
{
    public const string SectionName = "ConversationStore";

    /// <summary>Microsoft.Data.Sqlite connection string. A relative Data Source resolves against the content root.</summary>
    public string ConnectionString { get; set; } = "Data Source=data/butterknife.db";
}
