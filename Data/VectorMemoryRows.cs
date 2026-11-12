using MiBitacora.Services;

namespace MiBitacora.Data;

internal sealed class JournalVectorStateRow
{
    public string UserId { get; set; } = "";
    public long Revision { get; set; } = 1;
    public long CatalogRevision { get; set; }
    public long IndexedRevision { get; set; }
    public string ModelKey { get; set; } = "";
    public int Failures { get; set; }
    public long NextAttemptTicks { get; set; }
}

internal sealed class JournalVectorChunkRow
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public Guid? EntryId { get; set; }
    public Guid? NoteId { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public int Kind { get; set; }
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
    public string ModelKey { get; set; } = "";
    public byte[]? Embedding { get; set; }
    public TopicSource ToSource() => new(SourceId, EntryId, CreatedUtc, Label, Text, (TopicSourceKind)Kind, NoteId);
}

internal sealed class JournalVectorQueryRow
{
    public string UserId { get; set; } = "";
    public Guid EntryId { get; set; }
    public string QueryHash { get; set; } = "";
    public string ModelKey { get; set; } = "";
    public byte[] Embedding { get; set; } = [];
}
