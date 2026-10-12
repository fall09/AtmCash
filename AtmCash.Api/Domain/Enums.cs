namespace AtmCash.Api.Domain;

public enum Currency { TRY, USD, EUR }

public enum AtmStatus { Unconfigured, Configured, Loaded, InService, Maintenance }

public enum CassetteType { Deposit, Recycling }

public enum MovementType
{
    Load,
    Unload,
    Withdrawal,
    Deposit,
    Reservation,
    ReservationRelease,
    ReconciliationAdjustment
}

public static class Denominations
{
    public static readonly int[] Allowed = [20, 50, 100, 500];
}
