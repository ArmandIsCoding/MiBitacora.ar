namespace MiBitacora.Data;

internal sealed class TherapyShareRow
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public string ProtectedSnapshot { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    public bool Revoked { get; set; }
    public int Revision { get; set; } = 1;
}
