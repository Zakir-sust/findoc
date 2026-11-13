using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinDoc.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameArticleToItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Article",
                table: "Chunks",
                newName: "Item");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Item",
                table: "Chunks",
                newName: "Article");
        }
    }
}
