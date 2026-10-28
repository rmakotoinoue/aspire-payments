using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AspirePayments.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixProcessingRecordIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_payment_processing_records_message",
                table: "payment_processing_records");

            migrationBuilder.CreateIndex(
                name: "ux_payment_processing_records_message",
                table: "payment_processing_records",
                columns: new[] { "payment_transaction_id", "message_id", "processing_status" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_payment_processing_records_message",
                table: "payment_processing_records");

            migrationBuilder.CreateIndex(
                name: "ux_payment_processing_records_message",
                table: "payment_processing_records",
                columns: new[] { "payment_transaction_id", "message_id" },
                unique: true);
        }
    }
}
