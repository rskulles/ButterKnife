namespace ButterKnife.Data;

/// <summary>In-process signal that the conversation list changed, so the sidebar can refresh across circuits.</summary>
public sealed class ConversationEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
