namespace LampaWin.Desktop;

public sealed record MediaRequest(Uri Url, string Title, double StartSeconds, string SessionId);
public enum PlaybackState { Opening, Playing, Paused, Stopped, Ended, Error }
public sealed record PlaybackProgress(string SessionId, double PositionSeconds, double DurationSeconds, PlaybackState State, bool IsBuffering = false);
public sealed record PlaybackDiagnostic(string Category, string NativeState, string Severity);
