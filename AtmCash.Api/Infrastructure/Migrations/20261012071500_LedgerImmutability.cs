using AtmCash.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Api.Infrastructure.Migrations;

/// <summary>
/// B8: Hareket defteri değiştirilemez olmalı. Uygulama zaten hiç UPDATE/DELETE yapmaz; bu trigger,
/// birisi veritabanına doğrudan bağlansa bile kayıtların değiştirilmesini/silinmesini engeller.
/// (Model değişmediği için snapshot'a dokunmaz; salt SQL migration'ıdır.)
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261012071500_LedgerImmutability")]
public partial class LedgerImmutability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION prevent_movement_change() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'CassetteMovements is an append-only ledger: % is not allowed', TG_OP;
            END;
            $$ LANGUAGE plpgsql;
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER trg_movements_immutable
            BEFORE UPDATE OR DELETE ON "CassetteMovements"
            FOR EACH ROW EXECUTE FUNCTION prevent_movement_change();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TRIGGER IF EXISTS trg_movements_immutable ON "CassetteMovements";""");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS prevent_movement_change();");
    }
}
