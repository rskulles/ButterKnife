namespace ButterKnife.Data;

/// <summary>In-process signal that connections were added, edited or removed, so open chat pages refresh their model lists.</summary>
public sealed class ConnectionEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
