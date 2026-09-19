using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YALA.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdHocShoppingListItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "CatalogItemId",
                table: "ShoppingListItems",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "CustomName",
                table: "ShoppingListItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomName",
                table: "ShoppingListItems");

            migrationBuilder.AlterColumn<Guid>(
                name: "CatalogItemId",
                table: "ShoppingListItems",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
