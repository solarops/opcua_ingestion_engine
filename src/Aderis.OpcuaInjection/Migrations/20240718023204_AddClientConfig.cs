using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Aderis.OpcuaInjection.Migrations
{
    /// <inheritdoc />
    public partial class AddClientConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpcClientConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectionName = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    MaxSearch = table.Column<int>(type: "integer", nullable: false),
                    TimeoutMs = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    EncryptedPassword = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpcClientConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrowseExclusionFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConnectionOpcClientConnectionId = table.Column<int>(type: "integer", nullable: false),
                    OpcClientConnectionId = table.Column<int>(type: "integer", nullable: false),
                    ExclusionFolder = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrowseExclusionFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BrowseExclusionFolders_OpcClientConnections_OpcClientConnec~",
                        column: x => x.OpcClientConnectionId,
                        principalTable: "OpcClientConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrowseExclusionFolders_OpcClientConnectionId",
                table: "BrowseExclusionFolders",
                column: "OpcClientConnectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrowseExclusionFolders");

            migrationBuilder.DropTable(
                name: "OpcClientConnections");
        }
    }
}
