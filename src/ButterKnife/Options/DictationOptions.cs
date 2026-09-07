namespace ButterKnife.Options;

/// <summary>Defaults for the microphone button, bound from the "Dictation" section. Each browser may override the two toggles locally.</summary>
public sealed class DictationOptions
{
    public const string SectionName = "Dictation";

    /// <summary>Stop recording automatically after a pause once speech has been heard.</summary>
    public bool AutoStopOnSilence { get; set; } = true;

    /// <summary>Send the message as soon as a transcription arrives, instead of leaving it in the box to review.</summary>
    public bool AutoSendAfterTranscription { get; set; }

    /// <summary>How long a pause ends the recording.</summary>
    public int SilenceDurationMs { get; set; } = 1500;

    /// <summary>Safety cap so a forgotten microphone does not record forever.</summary>
    public int MaxRecordingSeconds { get; set; } = 120;
}
