using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HatidSuki.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddItemOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "options_json",
                schema: "public",
                table: "order_lines",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "options_json",
                schema: "public",
                table: "items",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "options_json",
                schema: "public",
                table: "order_lines");

            migrationBuilder.DropColumn(
                name: "options_json",
                schema: "public",
                table: "items");
        }
    }
}
