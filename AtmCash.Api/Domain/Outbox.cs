namespace AtmCash.Api.Domain;

/// <summary>
/// Transactional outbox kaydı (B10): olay, onu doğuran işlemle AYNI veritabanı transaction'ında yazılır.
/// Arka plandaki hosted service sonradan okuyup loglar ve SentAt ile işaretler.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public Guid? AtmId { get; set; }
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public static class OutboxTypes
{
    public const string LowCash = "LOW_CASH";
}
