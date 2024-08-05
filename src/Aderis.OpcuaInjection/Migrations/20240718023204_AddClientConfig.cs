using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using System.Text.Json.Serialization;
using System.IO;
using System.Text.Json;
using Aderis.OpcuaInjection.Helpers;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;

#nullable disable

namespace Aderis.OpcuaInjection.Migrations
{
    public class LegacyClientList
    {
        [JsonPropertyName("connections")]
        public required List<LegacyClient> Connections { get; set; }
    }

    public class LegacyClient
    {
        [JsonPropertyName("connection_name")]
        public required string ConnectionName { get; set; }
        [JsonPropertyName("max_search")]
        public required int MaxSearch { get; set; }
        [JsonPropertyName("staleness_timeout_ms")]
        public required int TimeoutMs { get; set; }
        [JsonPropertyName("url")]
        public required string Url { get; set; }

        [JsonPropertyName("browse_exclusion_folders")]
        public required List<string> Folders { get; set; }
    }

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

            var options = new JsonSerializerOptions()
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string filePath = Path.Combine("/opt", "sos-config", "opcua_client_config.json");

            if (File.Exists(filePath))
            {
                Console.WriteLine("Client Config exists, needs to migrate");

                try
                {
                    string rawOutput = OpcuaHelperFunctions.GetFileContentsNoLock(filePath);
                    var legacyClients = JsonSerializer.Deserialize<LegacyClientList>(rawOutput, options) ?? throw new Exception();

                    foreach (var client in legacyClients.Connections)
                    {
                        // new client, start at index
                        int pgFirstId = 1;

                        // Add to ClientConnections
                        migrationBuilder.InsertData(
                            table: "OpcClientConnections",
                            columns: new[] { "Id", "ConnectionName", "Url", "MaxSearch", "TimeoutMs" },
                            values: new object[,]
                            {
                                { pgFirstId, client.ConnectionName, client.Url, client.MaxSearch, client.TimeoutMs }
                            });

                        foreach (var folder in client.Folders)
                        {
                            migrationBuilder.InsertData(
                            table: "BrowseExclusionFolders",
                            columns: new[] { "ExclusionFolder", "OpcClientConnectionId" },
                            values: new object[,]
                            {
                                { folder, pgFirstId }
                            });
                        }
                        pgFirstId += 1;
                    }

                    // Remove
                    // File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error when attempting to migrate opcua_client_config: {ex}");
                    throw new Exception("Failed!");
                }
            }


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
