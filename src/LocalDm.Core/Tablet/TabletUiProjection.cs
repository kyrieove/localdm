using LocalDm.Core.Domain;

namespace LocalDm.Core.Tablet;

public enum TabletUiPhaseStatus { Done = 0, Current = 1, Upcoming = 2 }

public enum TabletUiControlKind { Start = 0, Advance = 1, ReleaseMaterials = 2, SubmitVote = 3, Reveal = 4, Complete = 5, PauseResume = 6 }

public sealed record TabletUiPhaseItem
{
    public required string PhaseId { get; init; }
    public required string Title { get; init; }
    public required TabletUiPhaseStatus Status { get; init; }
}

public sealed record TabletUiControl
{
    public required TabletUiControlKind Kind { get; init; }
    public required string Label { get; init; }
    public required bool Visible { get; init; }
    public required bool Enabled { get; init; }
}

public sealed record TabletUiProjection
{
    public required string SessionId { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
    public required int Revision { get; init; }
    public required string PhaseId { get; init; }
    public required string PhaseTitle { get; init; }
    public required int PhaseIndex { get; init; }
    public required int PhaseCount { get; init; }
    public string? Hint { get; init; }
    public required IReadOnlyList<TabletUiPhaseItem> Phases { get; init; }
    public required IReadOnlyList<TabletUiControl> Controls { get; init; }
    public required string ControlsNote { get; init; }
    public required int PendingMaterialCount { get; init; }
    public required int ReleasedMaterialCount { get; init; }
    public PublicVoteSummaryView? VoteSummary { get; init; }
    public IReadOnlyList<string>? RevealedTexts { get; init; }
    public required IReadOnlyList<PublicMessage> Messages { get; init; }
    public required long NextSequence { get; init; }
    public required bool HostBusy { get; init; }
    public required bool HostSpeaking { get; init; }
}
