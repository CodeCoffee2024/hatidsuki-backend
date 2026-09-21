using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HatidSuki.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "delivery_location_id",
                schema: "public",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_location_name",
                schema: "public",
                table: "orders",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_note",
                schema: "public",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "delivery_locations",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_locations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_locations_workspace_id_sort_order",
                schema: "public",
                table: "delivery_locations",
                columns: new[] { "workspace_id", "sort_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_locations",
                schema: "public");

            migrationBuilder.DropColumn(
                name: "delivery_location_id",
                schema: "public",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "delivery_location_name",
                schema: "public",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "delivery_note",
                schema: "public",
                table: "orders");
        }
    }
}
