namespace MyASR.Asr;

public sealed record AsrResult(string Text, TimeSpan AudioDuration, TimeSpan RecognitionTime);
