using AtmCash.Api.Domain;

namespace AtmCash.Tests;

/// <summary>Ödevdeki B3 kabul testleri (1-8) + birkaç ek kenar durum.</summary>
public class NoteSelectorTests
{
    private static DenominationStock[] Stock(params (int denom, int count)[] s) =>
        s.Select(x => new DenominationStock(x.denom, x.count)).ToArray();

    private static void AssertBreakdown(NoteBreakdown? actual, int amount, params (int denom, int count)[] expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(amount, actual!.Amount);
        var a = actual.Notes.OrderByDescending(n => n.Denomination).Select(n => $"{n.Denomination}x{n.Count}");
        var e = expected.Where(x => x.count > 0).OrderByDescending(x => x.denom).Select(x => $"{x.denom}x{x.count}");
        Assert.Equal(e, a);
    }

    // #1 TRY 100x0, 50x3, 20x10 -> 160 : tam eşleşme 50x2 + 20x3
    [Fact]
    public void Acceptance1_Exact_50x2_20x3()
    {
        var plan = NoteSelector.Select(Stock((100, 0), (50, 3), (20, 10)), 160);
        AssertBreakdown(plan.Exact, 160, (50, 2), (20, 3));
        Assert.Null(plan.Lower);
        Assert.Null(plan.Upper);
    }

    // #2 TRY 50x1, 20x2 -> 80 : alt 70, üst 90
    [Fact]
    public void Acceptance2_NoExact_Lower70_Upper90()
    {
        var plan = NoteSelector.Select(Stock((50, 1), (20, 2)), 80);
        Assert.Null(plan.Exact);
        AssertBreakdown(plan.Lower, 70, (50, 1), (20, 1));
        AssertBreakdown(plan.Upper, 90, (50, 1), (20, 2));
    }

    // #3 TRY 100x5, 20x10 -> 130 : alt 120, üst 140 (greedy burada da yanılır: 140'ı 7x20 ile değil 100+20x2 ile verir)
    [Fact]
    public void Acceptance3_NoExact_Lower120_Upper140()
    {
        var plan = NoteSelector.Select(Stock((100, 5), (20, 10)), 130);
        Assert.Null(plan.Exact);
        AssertBreakdown(plan.Lower, 120, (100, 1), (20, 1));
        AssertBreakdown(plan.Upper, 140, (100, 1), (20, 2));
    }

    // #4 USD 100x2 -> 250 : alt 200, üst yok
    [Fact]
    public void Acceptance4_NoExact_Lower200_NoUpper()
    {
        var plan = NoteSelector.Select(Stock((100, 2)), 250);
        Assert.Null(plan.Exact);
        AssertBreakdown(plan.Lower, 200, (100, 2));
        Assert.Null(plan.Upper);
    }

    // #5 TRY 500x1, 100x0, 20x5 -> 600 : 500x1 + 20x5
    [Fact]
    public void Acceptance5_Exact_500x1_20x5()
    {
        var plan = NoteSelector.Select(Stock((500, 1), (100, 0), (20, 5)), 600);
        AssertBreakdown(plan.Exact, 600, (500, 1), (20, 5));
    }

    // #6 TRY 100x1, 50x4, 20x5 -> 200 : 100x1 + 50x2 (3 not; 4x50 ya da 100+5x20 değil)
    [Fact]
    public void Acceptance6_Exact_FewestNotes_100x1_50x2()
    {
        var plan = NoteSelector.Select(Stock((100, 1), (50, 4), (20, 5)), 200);
        AssertBreakdown(plan.Exact, 200, (100, 1), (50, 2));
        Assert.Equal(3, plan.Exact!.TotalNotes);
    }

    // #7 TRY 500x1, 100x3, 20x20 -> 1000 : 500x1 + 100x3 + 20x10
    [Fact]
    public void Acceptance7_Exact_500x1_100x3_20x10()
    {
        var plan = NoteSelector.Select(Stock((500, 1), (100, 3), (20, 20)), 1000);
        AssertBreakdown(plan.Exact, 1000, (500, 1), (100, 3), (20, 10));
    }

    // #8 TRY 100x3, 50x1, 20x1 -> 110 : alt 100, üst 120
    [Fact]
    public void Acceptance8_NoExact_Lower100_Upper120()
    {
        var plan = NoteSelector.Select(Stock((100, 3), (50, 1), (20, 1)), 110);
        Assert.Null(plan.Exact);
        AssertBreakdown(plan.Lower, 100, (100, 1));
        AssertBreakdown(plan.Upper, 120, (100, 1), (20, 1));
    }

    // ---- ek kenar durumlar ----

    [Fact]
    public void Upper_IsOmitted_WhenItExceedsDailyLimit()
    {
        // 80 istenir; üst alternatif 90 ama kalan limit 85 => üst yok
        var plan = NoteSelector.Select(Stock((50, 1), (20, 2)), 80, maxUpperAmount: 85);
        AssertBreakdown(plan.Lower, 70, (50, 1), (20, 1));
        Assert.Null(plan.Upper);
    }

    [Fact]
    public void Upper_IsOffered_WhenExactlyAtDailyLimit()
    {
        var plan = NoteSelector.Select(Stock((50, 1), (20, 2)), 80, maxUpperAmount: 90);
        AssertBreakdown(plan.Upper, 90, (50, 1), (20, 2));
    }

    [Fact]
    public void SameDenominationInTwoCassettes_IsMerged()
    {
        // 100'lük iki kaset (2 + 3 not) birleşik 5 not gibi davranır
        var plan = NoteSelector.Select(Stock((100, 2), (100, 3)), 500);
        AssertBreakdown(plan.Exact, 500, (100, 5));
    }

    [Fact]
    public void AmountNotMultipleOfTen_HasNoExact_ButOffersAlternatives()
    {
        var plan = NoteSelector.Select(Stock((100, 3), (20, 5)), 105);
        Assert.Null(plan.Exact);
        AssertBreakdown(plan.Lower, 100, (100, 1));
        AssertBreakdown(plan.Upper, 120, (100, 1), (20, 1));
    }

    [Fact]
    public void EmptyStock_HasNoPlan()
    {
        var plan = NoteSelector.Select(Stock((100, 0)), 100);
        Assert.Null(plan.Exact);
        Assert.Null(plan.Lower);
        Assert.Null(plan.Upper);
    }

    [Fact]
    public void RequestedAboveEverything_OffersOnlyLower()
    {
        var plan = NoteSelector.Select(Stock((100, 2)), 1000);
        Assert.Null(plan.Exact);
        AssertBreakdown(plan.Lower, 200, (100, 2));
        Assert.Null(plan.Upper);
    }
}
