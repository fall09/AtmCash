namespace AtmCash.Api.Domain;

public enum ReconciliationStatus
{
    /// <summary>Fark eşiğin altında: düzeltme uygulandı, onay gerekmez.</summary>
    Completed,
    /// <summary>Fark eşiği aştı: düzeltme uygulandı ama ATM, başka bir kullanıcı onaylayana kadar servise dönemez.</summary>
    AwaitingApproval,
    Approved,
}

/// <summary>TRY karşılığı hesabı için döviz kuru tablosu (1 birim = RateToTry TRY).</summary>
public class ExchangeRate
{
    public Currency Currency { get; set; }
    public decimal RateToTry { get; set; }
}

public class Reconciliation
{
    public Guid Id { get; set; }
    public Guid AtmId { get; set; }
    public ReconciliationStatus Status { get; set; }
    public string StartedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>Fark satırlarının TRY karşılıklarının MUTLAK değerlerinin toplamı (eksik ve fazla birbirini götürmesin).</summary>
    public decimal TotalAbsoluteDifferenceTry { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public List<ReconciliationLine> Lines { get; set; } = [];
}

public class ReconciliationLine
{
    public Guid Id { get; set; }
    public Guid ReconciliationId { get; set; }
    public int Slot { get; set; }
    public Guid CassetteId { get; set; }
    public Currency? Currency { get; set; }
    public int? Denomination { get; set; }
    public int SystemCount { get; set; }
    public int CountedCount { get; set; }
    public int Difference => CountedCount - SystemCount;
    /// <summary>(sayılan - sistem) x nominal x kur. Eksik negatif, fazla pozitif.</summary>
    public decimal ValueTry { get; set; }
}
