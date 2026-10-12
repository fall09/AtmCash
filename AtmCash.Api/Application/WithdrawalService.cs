using AtmCash.Api.Domain;
using AtmCash.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AtmCash.Api.Application;

public class WithdrawalService(AppDbContext db, TimeProvider clock, CardHasher hasher, IConfiguration config)
{
    // Ödev gereği varsayılan 60 sn. Sadece elle test kolaylığı için yapılandırmayla uzatılabilir (Withdrawal:QuoteTtlSeconds).
    private readonly TimeSpan _quoteTtl = TimeSpan.FromSeconds(config.GetValue("Withdrawal:QuoteTtlSeconds", 60));

    // Gün sınırı Türkiye saatine göre (UTC+3, 2016'dan beri sabit). tzdata'ya bağımlı olmamak için sabit ofset.
    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(3);

    // ------------------------------------------------------------------ QUOTE
    public async Task<QuoteResponse> CreateQuoteAsync(Guid atmId, CreateQuoteRequest req, CancellationToken ct)
    {
        var currency = req.Currency!.Value;
        var cardHash = hasher.Hash(req.CardNumber);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1) ATM kilidi: aynı ATM'deki quote/confirm işlemleri sıraya girer (B6).
        var atm = await db.LoadAtmForUpdateAsync(atmId, ct);
        atm.EnsureState("withdraw", AtmStatus.InService);
        var now = clock.GetUtcNow().UtcDateTime;
        var availableBefore = LowCashAlerts.Snapshot(atm);

        // 2) Kart kilidi: aynı kart iki ayrı ATM'de aynı anda limiti aşamasın (kilit sırası hep ATM -> kart).
        var lockKey = $"card:{cardHash}:{currency}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct);

        // 3) Günlük limit (B5): onaylananlar + süresi dolmamış bekleyen quote'lar.
        var limit = DailyLimits.For(currency);
        var used = await UsedTodayAsync(cardHash, currency, now, ct);
        var remaining = Math.Max(0, limit - used);
        if (req.Amount > remaining)
            throw new DomainException("DAILY_LIMIT_EXCEEDED",
                $"Daily {currency} limit is {limit}; {used} already used or reserved today, {remaining} remaining.");

        // 4) En az notla seçim (B3). Sadece rezerve edilmemiş notlar, üst alternatif kalan limiti geçemez.
        var cassettes = atm.Cassettes
            .Where(c => c.Type == CassetteType.Recycling && c.Currency == currency && c.Denomination is not null)
            .ToList();
        var stock = cassettes.Select(c => new DenominationStock(c.Denomination!.Value, c.AvailableCount));
        var plan = NoteSelector.Select(stock, req.Amount, remaining);

        var quoteId = Guid.NewGuid();
        var lines = new List<QuoteLine>();
        if (plan.Exact is not null)
        {
            lines.AddRange(Allocate(quoteId, QuoteOptionType.Exact, plan.Exact, cassettes));
        }
        else
        {
            if (plan.Lower is not null) lines.AddRange(Allocate(quoteId, QuoteOptionType.Lower, plan.Lower, cassettes));
            if (plan.Upper is not null) lines.AddRange(Allocate(quoteId, QuoteOptionType.Upper, plan.Upper, cassettes));
        }

        if (lines.Count == 0)
            throw new DomainException("NO_PAYABLE_AMOUNT",
                $"The ATM cannot pay {req.Amount} {currency} or any nearby amount with the notes currently available.");

        var quote = new WithdrawalQuote
        {
            Id = quoteId, AtmId = atm.Id, CardHash = cardHash, Currency = currency,
            RequestedAmount = req.Amount, Status = QuoteStatus.Pending,
            CreatedAt = now, ExpiresAt = now + _quoteTtl,
            Lines = lines,
        };
        quote.ReservedAmount = lines.GroupBy(l => l.Option).Max(g => g.Sum(l => l.Denomination * l.Count));

        // 5) Rezervasyon: sunulan seçeneklerin birleşimi (kaset başına en büyük ihtiyaç) ayrılır.
        foreach (var (cassetteId, n) in ReservationPerCassette(lines))
        {
            atm.Cassettes.Single(c => c.Id == cassetteId).ReservedCount += n;
            db.CassetteMovements.Add(Movement(atm.Id, cassetteId, MovementType.Reservation, 0, n, now, quote.Id));
        }

        // Düşük nakit uyarısı, rezervasyonla AYNI transaction'da outbox'a yazılır (B10).
        LowCashAlerts.Record(db, atm, availableBefore, now);

        db.WithdrawalQuotes.Add(quote);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToResponse(quote);
    }

    public async Task<QuoteResponse> GetQuoteAsync(Guid quoteId, CancellationToken ct)
    {
        var quote = await db.WithdrawalQuotes.AsNoTracking().Include(q => q.Lines)
                        .SingleOrDefaultAsync(q => q.Id == quoteId, ct)
                    ?? throw QuoteNotFound(quoteId);
        return ToResponse(quote);
    }

    // ---------------------------------------------------------------- CONFIRM
    public async Task<(ConfirmResponse Response, bool Replayed)> ConfirmAsync(
        Guid quoteId, string? idempotencyKey, QuoteOptionType? requestedOption, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException("IDEMPOTENCY_KEY_REQUIRED", "The Idempotency-Key header is required.", StatusCodes.Status400BadRequest);
        if (idempotencyKey.Length > 100)
            throw new DomainException("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key must be at most 100 characters.", StatusCodes.Status400BadRequest);

        var atmId = await FindAtmIdAsync(quoteId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LoadAtmForUpdateAsync(atmId, ct);
        var quote = await db.WithdrawalQuotes.Include(q => q.Lines).SingleAsync(q => q.Id == quoteId, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        // Idempotency: kilit altında kontrol edilir, eşzamanlı iki aynı-key isteği sıraya girer.
        var existing = await db.IdempotencyRecords.SingleOrDefaultAsync(r => r.Key == idempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.QuoteId != quoteId || (requestedOption is not null && requestedOption != existing.Option))
                throw new DomainException("IDEMPOTENCY_KEY_REUSED",
                    "This Idempotency-Key was already used for a different request.");
            return (ToConfirmResponse(quote, existing.Option), true);
        }

        switch (quote.Status)
        {
            case QuoteStatus.Confirmed:
                throw new DomainException("QUOTE_ALREADY_CONFIRMED", "This quote was already confirmed.", StatusCodes.Status409Conflict);
            case QuoteStatus.Cancelled:
                throw new DomainException("QUOTE_CANCELLED", "This quote was cancelled and cannot be confirmed.", StatusCodes.Status409Conflict);
            case QuoteStatus.Expired:
                throw QuoteExpired();
        }

        if (quote.ExpiresAt <= now)
        {
            // Arka plan servisi henüz süpürmediyse burada serbest bırak; sonra hatayı fırlat.
            ExpireQuote(atm, quote, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw QuoteExpired();
        }

        atm.EnsureState("confirm withdrawal", AtmStatus.InService);

        var offered = quote.Lines.Select(l => l.Option).Distinct().ToList();
        var chosen = requestedOption ?? (offered.Contains(QuoteOptionType.Exact)
            ? QuoteOptionType.Exact
            : throw new DomainException("OPTION_REQUIRED",
                "This quote has alternatives; choose one with the 'option' field (LOWER or UPPER)."));
        if (!offered.Contains(chosen))
            throw new DomainException("OPTION_NOT_AVAILABLE",
                $"Option {chosen} is not part of this quote. Offered: {string.Join(", ", offered)}.");

        // Borçlandır: seçilen seçeneğin notları kasetlerden düşer, rezervasyonun tamamı serbest kalır.
        var chosenLines = quote.Lines.Where(l => l.Option == chosen).ToList();
        foreach (var g in chosenLines.GroupBy(l => l.CassetteId))
        {
            var n = g.Sum(l => l.Count);
            atm.Cassettes.Single(c => c.Id == g.Key).Count -= n;
            db.CassetteMovements.Add(Movement(atm.Id, g.Key, MovementType.Withdrawal, -n, 0, now, quote.Id));
        }

        ReleaseReservation(atm, quote, now);

        quote.Status = QuoteStatus.Confirmed;
        quote.ResolvedAt = now;
        quote.ChosenOption = chosen;
        quote.DispensedAmount = chosenLines.Sum(l => l.Denomination * l.Count);
        db.IdempotencyRecords.Add(new IdempotencyRecord { Key = idempotencyKey, QuoteId = quoteId, Option = chosen, CreatedAt = now });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (ToConfirmResponse(quote, chosen), false);
    }

    // ----------------------------------------------------------------- CANCEL
    public async Task<QuoteResponse> CancelAsync(Guid quoteId, CancellationToken ct)
    {
        var atmId = await FindAtmIdAsync(quoteId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var atm = await db.LoadAtmForUpdateAsync(atmId, ct);
        var quote = await db.WithdrawalQuotes.Include(q => q.Lines).SingleAsync(q => q.Id == quoteId, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        switch (quote.Status)
        {
            case QuoteStatus.Cancelled:
                return ToResponse(quote); // tekrar iptal zararsız
            case QuoteStatus.Confirmed:
                throw new DomainException("QUOTE_ALREADY_CONFIRMED", "A confirmed quote cannot be cancelled.", StatusCodes.Status409Conflict);
            case QuoteStatus.Expired:
                throw QuoteExpired();
        }

        if (quote.ExpiresAt <= now)
        {
            ExpireQuote(atm, quote, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw QuoteExpired();
        }

        ReleaseReservation(atm, quote, now);
        quote.Status = QuoteStatus.Cancelled;
        quote.ResolvedAt = now;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToResponse(quote);
    }

    // ----------------------------------------------------------------- EXPIRY
    /// <summary>Süresi dolmuş bekleyen quote'ları kapatır ve rezervasyonlarını serbest bırakır (hosted service çağırır).</summary>
    public async Task<int> ExpireDueQuotesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.WithdrawalQuotes.AsNoTracking()
            .Where(q => q.Status == QuoteStatus.Pending && q.ExpiresAt <= now)
            .Select(q => q.AtmId).Distinct().ToListAsync(ct);

        var expired = 0;
        foreach (var atmId in due)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var atm = await db.LoadAtmForUpdateAsync(atmId, ct);
            var quotes = await db.WithdrawalQuotes.Include(q => q.Lines)
                .Where(q => q.AtmId == atmId && q.Status == QuoteStatus.Pending && q.ExpiresAt <= now)
                .ToListAsync(ct);
            foreach (var q in quotes) ExpireQuote(atm, q, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            expired += quotes.Count;
        }

        return expired;
    }

    // ---------------------------------------------------------------- helpers
    private void ExpireQuote(Atm atm, WithdrawalQuote quote, DateTime now)
    {
        ReleaseReservation(atm, quote, now);
        quote.Status = QuoteStatus.Expired;
        quote.ResolvedAt = now;
    }

    private void ReleaseReservation(Atm atm, WithdrawalQuote quote, DateTime now)
    {
        foreach (var (cassetteId, n) in ReservationPerCassette(quote.Lines))
        {
            atm.Cassettes.Single(c => c.Id == cassetteId).ReservedCount -= n;
            db.CassetteMovements.Add(Movement(atm.Id, cassetteId, MovementType.ReservationRelease, 0, -n, now, quote.Id));
        }
    }

    /// <summary>Seçenekler birbirinin alternatifi olduğundan, bir kasette ayrılacak not = seçenekler arası en büyük ihtiyaç.</summary>
    private static Dictionary<Guid, int> ReservationPerCassette(IEnumerable<QuoteLine> lines) =>
        lines.GroupBy(l => new { l.Option, l.CassetteId })
            .Select(g => new { g.Key.CassetteId, N = g.Sum(l => l.Count) })
            .GroupBy(x => x.CassetteId)
            .ToDictionary(g => g.Key, g => g.Max(x => x.N));

    /// <summary>Nominal bazındaki dağılımı kasetlere böler: aynı nominalde doluluk oranı yüksek kasetten önce verilir (B2).</summary>
    private static List<QuoteLine> Allocate(Guid quoteId, QuoteOptionType option, NoteBreakdown breakdown, List<Cassette> cassettes)
    {
        var lines = new List<QuoteLine>();
        foreach (var note in breakdown.Notes)
        {
            var left = note.Count;
            var candidates = cassettes.Where(c => c.Denomination == note.Denomination)
                .OrderByDescending(c => (double)c.Count / c.Capacity)
                .ThenBy(c => c.Slot);
            foreach (var c in candidates)
            {
                if (left == 0) break;
                var take = Math.Min(left, c.AvailableCount);
                if (take <= 0) continue;
                lines.Add(new QuoteLine
                {
                    Id = Guid.NewGuid(), QuoteId = quoteId, Option = option,
                    CassetteId = c.Id, Denomination = note.Denomination, Count = take,
                });
                left -= take;
            }

            if (left != 0)
                throw new InvalidOperationException($"Allocation mismatch for denomination {note.Denomination}.");
        }

        return lines;
    }

    private async Task<int> UsedTodayAsync(string cardHash, Currency currency, DateTime nowUtc, CancellationToken ct)
    {
        var localNow = nowUtc + LocalOffset;
        var dayStartUtc = localNow.Date - LocalOffset;
        dayStartUtc = DateTime.SpecifyKind(dayStartUtc, DateTimeKind.Utc);

        var confirmed = await db.WithdrawalQuotes
            .Where(q => q.CardHash == cardHash && q.Currency == currency
                        && q.Status == QuoteStatus.Confirmed && q.ResolvedAt >= dayStartUtc)
            .SumAsync(q => q.DispensedAmount ?? 0, ct);

        var pending = await db.WithdrawalQuotes
            .Where(q => q.CardHash == cardHash && q.Currency == currency
                        && q.Status == QuoteStatus.Pending && q.ExpiresAt > nowUtc)
            .SumAsync(q => q.ReservedAmount, ct);

        return confirmed + pending;
    }

    private async Task<Guid> FindAtmIdAsync(Guid quoteId, CancellationToken ct) =>
        await db.WithdrawalQuotes.AsNoTracking().Where(q => q.Id == quoteId)
            .Select(q => (Guid?)q.AtmId).SingleOrDefaultAsync(ct)
        ?? throw QuoteNotFound(quoteId);

    private static CassetteMovement Movement(Guid atmId, Guid cassetteId, MovementType type, int countDelta, int reservedDelta, DateTime now, Guid quoteId) =>
        new()
        {
            Id = Guid.NewGuid(), AtmId = atmId, CassetteId = cassetteId, Type = type,
            CountDelta = countDelta, ReservedDelta = reservedDelta, CreatedAt = now, Reference = quoteId.ToString(),
        };

    private static DomainException QuoteNotFound(Guid id) =>
        new("QUOTE_NOT_FOUND", $"Quote {id} was not found.", StatusCodes.Status404NotFound);

    private static DomainException QuoteExpired() =>
        new("QUOTE_EXPIRED", "This quote has expired; request a new one.", StatusCodes.Status409Conflict);

    private static QuoteOptionDto OptionDto(QuoteOptionType type, IEnumerable<QuoteLine> lines)
    {
        var notes = lines.GroupBy(l => l.Denomination).OrderByDescending(g => g.Key)
            .Select(g => new QuoteNoteDto(g.Key, g.Sum(l => l.Count))).ToList();
        return new QuoteOptionDto(type, notes.Sum(n => n.Denomination * n.Count), notes.Sum(n => n.Count), notes);
    }

    private static QuoteResponse ToResponse(WithdrawalQuote q)
    {
        var options = q.Lines.GroupBy(l => l.Option).OrderBy(g => g.Key).Select(g => OptionDto(g.Key, g)).ToList();
        return new QuoteResponse(q.Id, q.AtmId, q.Status, q.Currency, q.RequestedAmount,
            options.Any(o => o.Option == QuoteOptionType.Exact), q.ExpiresAt, options);
    }

    private static ConfirmResponse ToConfirmResponse(WithdrawalQuote q, QuoteOptionType option)
    {
        var dto = OptionDto(option, q.Lines.Where(l => l.Option == option));
        return new ConfirmResponse(q.Id, q.AtmId, q.Status, q.Currency, option, dto.Amount, dto.Notes);
    }
}
