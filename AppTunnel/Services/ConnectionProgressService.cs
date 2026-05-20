namespace AppTunnel.Services;

public enum ConnectionProgressPhase
{
    Begin,
    Active,
    Complete,
    Fail,
    Skip
}

public readonly record struct ConnectionProgressEvent(
    string StepId,
    ConnectionProgressPhase Phase,
    string MessageKey,
    string? Detail = null,
    string? DetailFormatArg = null);

/// <summary>
/// Lightweight progress bus for connection stages (tunnel, router, cleanup).
/// MainViewModel subscribes and updates the connecting UI.
/// </summary>
public static class ConnectionProgressService
{
    public static event Action<ConnectionProgressEvent>? Changed;

    public static void Report(string stepId, ConnectionProgressPhase phase, string messageKey, string? detail = null, string? detailFormatArg = null)
        => Changed?.Invoke(new ConnectionProgressEvent(stepId, phase, messageKey, detail, detailFormatArg));
}
