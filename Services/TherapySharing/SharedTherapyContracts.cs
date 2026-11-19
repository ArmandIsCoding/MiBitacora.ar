namespace MiBitacora.Services;

public sealed record SharedDocument(string Name, string? Text, string Status);
public sealed record SharedTheme(string Title, string Detail, string? Quote);
public sealed record SharedReflection(string Title, string Text, DateTime? GeneratedUtc,
    IReadOnlyList<SharedTheme> Themes, IReadOnlyList<string> Questions, IReadOnlyList<string> SessionTopics, string? NextStep, bool LimitedMemory = false);
public sealed record SharedEntry(string Id, string Text, DateTime Date, string Notebook, bool ForSession,
    IReadOnlyList<SharedDocument> Documents, SharedReflection? Reflection);
public sealed record SharedNote(string Id, string Text, DateTime Date, bool ForSession);
public sealed record SharedEvidence(string? SourceId, string Label, string Text, DateTime? Date);
public sealed record SharedReportPoint(string Kind, string Title, string Text, string Question, IReadOnlyList<SharedEvidence> Evidence);
public sealed record SharedReportSection(string Id, string Title, string Introduction, IReadOnlyList<SharedReportPoint> Points);
public sealed record SharedReport(DateTime GeneratedUtc, IReadOnlyList<SharedReportSection> Sections);
public sealed record SharedActivity(string Month, int Entries);
public sealed record SharedThemeSummary(string NodeId, string Title, int Entries);
public sealed record SharedTherapySnapshot(int Version, string Title, DateTime PreparedUtc,
    ThoughtMapData Map, IReadOnlyList<SharedEntry> Entries, IReadOnlyList<SharedNote> Notes, string PersonalContext,
    SharedReport? Report, bool PreviousReportOutdated, IReadOnlyList<SharedActivity> Activity,
    IReadOnlyList<SharedThemeSummary> Themes);

// Prepared material stays in the authenticated server circuit, never in a public URL or form payload.
public sealed record PreparedTherapyShare(string UserId, string Fingerprint, SharedTherapySnapshot Snapshot);
public sealed record TherapyShareInfo(Guid Id, string Title, DateTime CreatedUtc, DateTime UpdatedUtc,
    DateTime? ExpiresUtc, bool Revoked, int Revision, string? Token);
public sealed record OpenTherapyShare(SharedTherapySnapshot Snapshot, DateTime? ExpiresUtc, int Revision);
