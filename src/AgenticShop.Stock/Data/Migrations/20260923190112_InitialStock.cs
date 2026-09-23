using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenticShop.Stock.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity_on_hand = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<int>(type: "integer", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_items", x => x.id);
                    table.CheckConstraint("ck_stock_items_quantity_on_hand_non_negative", "quantity_on_hand >= 0");
                    table.CheckConstraint("ck_stock_items_reserved_non_negative", "reserved >= 0");
                    table.CheckConstraint("ck_stock_items_reserved_within_on_hand", "reserved <= quantity_on_hand");
                });

            migrationBuilder.CreateTable(
                name: "stock_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_reservations", x => x.id);
                    table.CheckConstraint("ck_stock_reservations_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_stock_reservations_status_known", "status IN ('Pending', 'Confirmed', 'Released')");
                    table.ForeignKey(
                        name: "fk_stock_reservations_stock_items",
                        column: x => x.stock_item_id,
                        principalTable: "stock_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_items_product_id",
                table: "stock_items",
                column: "product_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_order_id",
                table: "stock_reservations",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_order_id_stock_item_id",
                table: "stock_reservations",
                columns: new[] { "order_id", "stock_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_stock_item_id",
                table: "stock_reservations",
                column: "stock_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_reservations");

            migrationBuilder.DropTable(
                name: "stock_items");
        }
    }
}
