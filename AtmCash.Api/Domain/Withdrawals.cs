namespace AtmCash.Api.Domain;

public enum QuoteStatus { Pending, Confirmed, Cancelled, Expired }

/// <summary>Bir quote'un sunduğu seçenekler: tam tutar, alt alternatif, üst alternatif.</summary>
public enum QuoteOptionType { Exact, Lower, Upper }

/// <summary>İki adımlı çekimin ilk adımı (B4). Sunulan notlar kasetlerde rezerve edilir.</summary>
public class WithdrawalQuote
{
    public Guid Id { get; set; }
    public Guid AtmId { get; set; }
    /// <summary>Kart numarasının HMAC-SHA256 özeti. Düz kart numarası asla saklanmaz (B5).</summary>
    public string CardHash { get; set; } = "";
    public Currency Currency { get; set; }
    public int RequestedAmount { get; set; }
    public QuoteStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    /// <summary>Günlük limite sayılan tutar: sunulan seçeneklerin en büyüğü (rezerve edilen en kötü durum).</summary>
    public int ReservedAmount { get; set; }
    public QuoteOptionType? ChosenOption { get; set; }
    public int? DispensedAmount { get; set; }
    public List<QuoteLine> Lines { get; set; } = [];
}

/// <summary>Bir seçeneğin bir kasetten kaç not vereceği.</summary>
public class QuoteLine
{
    public Guid Id { get; set; }
    public Guid QuoteId { get; set; }
    public QuoteOptionType Option { get; set; }
    public Guid CassetteId { get; set; }
    public int Denomination { get; set; }
    public int Count { get; set; }
}

/// <summary>Confirm isteğinde gelen Idempotency-Key kaydı (B4). Aynı key aynı sonucu döner.</summary>
public class IdempotencyRecord
{
    public string Key { get; set; } = "";
    public Guid QuoteId { get; set; }
    public QuoteOptionType Option { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class DailyLimits
{
    public static int For(Currency c) => c switch
    {
        Currency.TRY => 20_000,
        Currency.USD => 1_000,
        Currency.EUR => 1_000,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };
}
