using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlackoutWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationAttemptLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FailedUtc",
                table: "NotificationDeliveries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedUtc",
                table: "NotificationDeliveries");
        }
    }
}
