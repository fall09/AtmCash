namespace AtmCash.Api.Domain;

/// <summary>Bir nominalden eldeki (kullanılabilir) not sayısı. Aynı nominal birden fazla kasette olabilir.</summary>
public sealed record DenominationStock(int Denomination, int Count);

public sealed record NoteCount(int Denomination, int Count);

public sealed record NoteBreakdown(int Amount, IReadOnlyList<NoteCount> Notes)
{
    public int TotalNotes => Notes.Sum(n => n.Count);
}

/// <summary>Tam eşleşme varsa sadece Exact dolu; yoksa Lower/Upper alternatifleri (olabilirse) dolu.</summary>
public sealed record WithdrawalPlan(NoteBreakdown? Exact, NoteBreakdown? Lower, NoteBreakdown? Upper);

/// <summary>
/// B3: Sınırlı not adetiyle, istenen tutarı EN AZ NOTLA ödeyen dağılımı bulur.
///
/// Yöntem: sınırlı (bounded) knapsack DP. Tüm nominaller 10'un katı olduğundan tutarlar 10'a bölünür
/// (en fazla ~20.000 durum). minNotes[i][a] = ilk i nominali kullanarak a (x10) tutarına ulaşmanın en az not sayısı.
/// Greedy ("en büyükten başla") bazı durumlarda yanlış sonuç verir; DP, ulaşılabilen TÜM tutarları
/// bildiği için hem tam eşleşmeyi hem de alt/üst alternatifleri aynı tablodan çıkarır.
///
/// Saf bir sınıftır: veritabanı, EF veya zamana bağımlı değildir, bu yüzden birim testlenir.
/// </summary>
public static class NoteSelector
{
    private const int Unit = 10;
    private const int Unreachable = int.MaxValue / 2;

    /// <param name="stock">Sadece İSTENEN para birimine ait, rezerve edilmemiş (kullanılabilir) notlar.</param>
    /// <param name="requested">İstenen tutar (&gt; 0).</param>
    /// <param name="maxUpperAmount">Üst alternatifin aşamayacağı tutar (müşterinin kalan günlük limiti). Null = sınırsız.</param>
    public static WithdrawalPlan Select(IEnumerable<DenominationStock> stock, int requested, int? maxUpperAmount = null)
    {
        if (requested <= 0)
            throw new ArgumentOutOfRangeException(nameof(requested), "Requested amount must be positive.");

        // Aynı nominal iki kasette olabilir: DP için adetleri birleştir. Büyükten küçüğe sırala.
        var denoms = stock
            .Where(s => s.Count > 0)
            .GroupBy(s => s.Denomination)
            .Select(g => (Denomination: g.Key, Count: g.Sum(s => s.Count)))
            .OrderByDescending(d => d.Denomination)
            .ToArray();

        if (denoms.Any(d => d.Denomination <= 0 || d.Denomination % Unit != 0))
            throw new ArgumentException($"Denominations must be positive multiples of {Unit}.", nameof(stock));

        var maxUnits = denoms.Sum(d => d.Denomination / Unit * d.Count);

        var minNotes = new int[denoms.Length + 1][];
        var picks = new int[denoms.Length][];
        minNotes[0] = Enumerable.Repeat(Unreachable, maxUnits + 1).ToArray();
        minNotes[0][0] = 0;

        for (var i = 0; i < denoms.Length; i++)
        {
            var unitsPerNote = denoms[i].Denomination / Unit;
            var cur = Enumerable.Repeat(Unreachable, maxUnits + 1).ToArray();
            var pick = new int[maxUnits + 1];
            var prev = minNotes[i];

            for (var a = 0; a <= maxUnits; a++)
            {
                for (var k = 0; k <= denoms[i].Count && k * unitsPerNote <= a; k++)
                {
                    var before = prev[a - k * unitsPerNote];
                    if (before >= Unreachable) continue;
                    if (before + k < cur[a])
                    {
                        cur[a] = before + k;
                        pick[a] = k;
                    }
                }
            }

            minNotes[i + 1] = cur;
            picks[i] = pick;
        }

        var final = minNotes[denoms.Length];

        NoteBreakdown Build(int units)
        {
            var notes = new List<NoteCount>();
            var a = units;
            for (var i = denoms.Length - 1; i >= 0; i--)
            {
                var k = picks[i][a];
                if (k > 0) notes.Add(new NoteCount(denoms[i].Denomination, k));
                a -= k * (denoms[i].Denomination / Unit);
            }

            notes.Reverse(); // büyük nominal önce
            return new NoteBreakdown(units * Unit, notes);
        }

        var requestedUnits = requested / Unit;
        var divisible = requested % Unit == 0;

        if (divisible && requestedUnits <= maxUnits && final[requestedUnits] < Unreachable)
            return new WithdrawalPlan(Build(requestedUnits), null, null);

        // Alt alternatif: istenenin altındaki ulaşılabilir en büyük tutar (0 hariç).
        NoteBreakdown? lower = null;
        for (var a = Math.Min(divisible ? requestedUnits - 1 : requestedUnits, maxUnits); a >= 1; a--)
        {
            if (final[a] >= Unreachable) continue;
            lower = Build(a);
            break;
        }

        // Üst alternatif: istenenin üstündeki ulaşılabilir en küçük tutar, müşterinin limitini aşmamak şartıyla.
        NoteBreakdown? upper = null;
        for (var a = requestedUnits + 1; a <= maxUnits; a++)
        {
            if (maxUpperAmount is { } cap && a * Unit > cap) break;
            if (final[a] >= Unreachable) continue;
            upper = Build(a);
            break;
        }

        return new WithdrawalPlan(null, lower, upper);
    }
}
