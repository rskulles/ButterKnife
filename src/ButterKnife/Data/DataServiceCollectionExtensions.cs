namespace ButterKnife.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDataStores(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "Database:ConnectionString is required.")
            .ValidateOnStart();

        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<IPersonaStore, SqlitePersonaStore>();
        services.AddSingleton<IConnectionStore, SqliteConnectionStore>();
        services.AddSingleton<ConversationEvents>();
        services.AddSingleton<ConnectionEvents>();
        return services;
    }
}
