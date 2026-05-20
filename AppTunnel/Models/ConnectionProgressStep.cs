using CommunityToolkit.Mvvm.ComponentModel;

namespace AppTunnel.Models;

public enum ConnectionStepPhase
{
    Pending,
    Active,
    Completed,
    Failed,
    Skipped
}

public sealed partial class ConnectionProgressStep : ObservableObject
{
    public required string StepId { get; init; }
    public required string TitleKey { get; init; }

    [ObservableProperty]
    private ConnectionStepPhase _phase = ConnectionStepPhase.Pending;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _detail = "";

    /// <summary>Persian source key for <see cref="Detail"/> (re-localized on language change).</summary>
    public string? DetailKey { get; set; }

    public string? DetailFormatArg { get; set; }

    public string StateGlyph => Phase switch
    {
        ConnectionStepPhase.Completed => "✓",
        ConnectionStepPhase.Failed => "✗",
        ConnectionStepPhase.Active => "●",
        ConnectionStepPhase.Skipped => "–",
        _ => "○"
    };

    public string StateColor => Phase switch
    {
        ConnectionStepPhase.Completed => "#6CCB5F",
        ConnectionStepPhase.Failed => "#E05252",
        ConnectionStepPhase.Active => "#FFC107",
        ConnectionStepPhase.Skipped => "#666666",
        _ => "#888888"
    };

    partial void OnPhaseChanged(ConnectionStepPhase value)
    {
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(StateColor));
    }
}
