namespace ButterKnife.Data;

/// <summary>In-process signal that personas were added, edited or removed, so open chat pages refresh their persona picker.</summary>
public sealed class PersonaEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
