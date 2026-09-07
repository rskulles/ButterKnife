namespace ButterKnife.Data;

public static class ConversationServiceCollectionExtensions
{
    public static IServiceCollection AddConversationStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ConversationStoreOptions>()
            .Bind(configuration.GetSection(ConversationStoreOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "ConversationStore:ConnectionString is required.")
            .ValidateOnStart();

        services.AddSingleton<IConversationStore, SqliteConversationStore>();
        services.AddSingleton<ConversationEvents>();
        return services;
    }
}
