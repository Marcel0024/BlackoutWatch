using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlackoutWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OutageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeliveredUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_Outages_OutageId",
                        column: x => x.OutageId,
                        principalTable: "Outages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_DeliveredUtc_NextAttemptUtc",
                table: "NotificationDeliveries",
                columns: new[] { "DeliveredUtc", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_OutageId_Channel",
                table: "NotificationDeliveries",
                columns: new[] { "OutageId", "Channel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDeliveries");
        }
    }
}
