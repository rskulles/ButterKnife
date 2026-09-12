namespace ButterKnife.Data;

/// <summary>In-process signal that an app setting changed, so open pages in other circuits pick it up.</summary>
public sealed class SettingsEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
