using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aderis.OpcuaInjection.Migrations
{
    /// <inheritdoc />
    public partial class RemovedErroneousExclusionForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConnectionOpcClientConnectionId",
                table: "BrowseExclusionFolders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConnectionOpcClientConnectionId",
                table: "BrowseExclusionFolders",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
