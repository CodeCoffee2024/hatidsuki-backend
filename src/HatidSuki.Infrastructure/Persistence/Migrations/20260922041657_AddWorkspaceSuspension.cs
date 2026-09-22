using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HatidSuki.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceSuspension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_suspended",
                schema: "public",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "suspended_at_utc",
                schema: "public",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suspended_reason",
                schema: "public",
                table: "workspaces",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_suspended",
                schema: "public",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "suspended_at_utc",
                schema: "public",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "suspended_reason",
                schema: "public",
                table: "workspaces");
        }
    }
}
