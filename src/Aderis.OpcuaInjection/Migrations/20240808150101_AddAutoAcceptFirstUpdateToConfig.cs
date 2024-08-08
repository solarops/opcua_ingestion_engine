using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aderis.OpcuaInjection.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoAcceptFirstUpdateToConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoAcceptFirstUpdate",
                table: "OpcClientConnections",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoAcceptFirstUpdate",
                table: "OpcClientConnections");
        }
    }
}
