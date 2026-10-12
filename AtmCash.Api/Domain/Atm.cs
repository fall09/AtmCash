using System.Text.Json;

namespace AtmCash.Api.Domain;

public class Atm
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public AtmStatus Status { get; set; } = AtmStatus.Unconfigured;
    public DateTime CreatedAt { get; set; }
    public List<Cassette> Cassettes { get; set; } = [];

    /// <summary>Yanlış durumda yapılan işlemi anlamlı bir hatayla reddeder (B2).</summary>
    public void EnsureState(string operation, params AtmStatus[] allowed)
    {
        if (allowed.Contains(Status)) return;
        throw new DomainException(
            "INVALID_ATM_STATE",
            $"Operation '{operation}' is not allowed while the ATM is {StatusName(Status)}. Allowed states: {string.Join(", ", allowed.Select(StatusName))}.",
            StatusCodes.Status409Conflict);
    }

    public static string StatusName(AtmStatus s) => JsonNamingPolicy.SnakeCaseUpper.ConvertName(s.ToString());
}

public class Cassette
{
    public const int CapacityNotes = 100;

    public Guid Id { get; set; }
    public Guid AtmId { get; set; }
    /// <summary>1 = deposit kaseti, 2..5 = geri dönüşümlü kasetler.</summary>
    public int Slot { get; set; }
    public CassetteType Type { get; set; }
    public Currency? Currency { get; set; }
    public int? Denomination { get; set; }
    public int Capacity { get; set; } = CapacityNotes;
    /// <summary>Fiziksel olarak kasetteki toplam not sayısı (rezerve edilenler dahil).</summary>
    public int Count { get; set; }
    /// <summary>Açık quote'lar için ayrılmış not sayısı.</summary>
    public int ReservedCount { get; set; }

    public int AvailableCount => Count - ReservedCount;
    public bool IsConfigured => Type == CassetteType.Deposit || (Currency is not null && Denomination is not null);
}

/// <summary>Değiştirilemez kaset hareket defteri kaydı (B8). Sadece eklenir, asla güncellenmez.</summary>
public class CassetteMovement
{
    public Guid Id { get; init; }
    public Guid AtmId { get; init; }
    public Guid CassetteId { get; init; }
    public MovementType Type { get; init; }
    public int CountDelta { get; init; }
    public int ReservedDelta { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Reference { get; init; }
    public string? CreatedBy { get; init; }
}
