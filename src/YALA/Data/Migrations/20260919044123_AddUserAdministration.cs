using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace YALA.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAdministrator",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Existing installations predate administrator roles. Preserve management
            // access for their current trusted, locally-created accounts.
            migrationBuilder.Sql("UPDATE AspNetUsers SET IsAdministrator = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAdministrator",
                table: "AspNetUsers");
        }
    }
}
