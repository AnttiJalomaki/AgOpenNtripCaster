using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgOpenNtripCaster.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPassiveRoverDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientSessions_UserId",
                table: "ClientSessions");

            migrationBuilder.AddColumn<string>(
                name: "DisconnectReason",
                table: "SourceConnections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRtcmAt",
                table: "SourceConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RtcmChunkCount",
                table: "SourceConnections",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ClientUserAgent",
                table: "ClientSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisconnectReason",
                table: "ClientSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstGgaAt",
                table: "ClientSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GgaFrameCount",
                table: "ClientSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InvalidGgaFrameCount",
                table: "ClientSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "LastAltitudeMeters",
                table: "ClientSessions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastDifferentialAgeSeconds",
                table: "ClientSessions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastDifferentialStationId",
                table: "ClientSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastFixQuality",
                table: "ClientSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastGeoidSeparationMeters",
                table: "ClientSessions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastHdop",
                table: "ClientSessions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastSatelliteCount",
                table: "ClientSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeakPendingBufferCount",
                table: "ClientSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StalePositionPeriods",
                table: "ClientSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "StreamPausedSeconds",
                table: "ClientSessions",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "DiagnosticEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    ClientSessionId = table.Column<string>(type: "text", nullable: true),
                    SourceConnectionId = table.Column<int>(type: "integer", nullable: true),
                    MountPointId = table.Column<int>(type: "integer", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticEvents_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DiagnosticEvents_ClientSessions_ClientSessionId",
                        column: x => x.ClientSessionId,
                        principalTable: "ClientSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DiagnosticEvents_MountPoints_MountPointId",
                        column: x => x.MountPointId,
                        principalTable: "MountPoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DiagnosticEvents_SourceConnections_SourceConnectionId",
                        column: x => x.SourceConnectionId,
                        principalTable: "SourceConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceConnections_ConnectedAt",
                table: "SourceConnections",
                column: "ConnectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSessions_ConnectedAt",
                table: "ClientSessions",
                column: "ConnectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSessions_UserId_ConnectedAt",
                table: "ClientSessions",
                columns: new[] { "UserId", "ConnectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticEvents_ClientSessionId",
                table: "DiagnosticEvents",
                column: "ClientSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticEvents_Kind",
                table: "DiagnosticEvents",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticEvents_MountPointId",
                table: "DiagnosticEvents",
                column: "MountPointId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticEvents_SourceConnectionId",
                table: "DiagnosticEvents",
                column: "SourceConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticEvents_Timestamp",
                table: "DiagnosticEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticEvents_UserId",
                table: "DiagnosticEvents",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiagnosticEvents");

            migrationBuilder.DropIndex(
                name: "IX_SourceConnections_ConnectedAt",
                table: "SourceConnections");

            migrationBuilder.DropIndex(
                name: "IX_ClientSessions_ConnectedAt",
                table: "ClientSessions");

            migrationBuilder.DropIndex(
                name: "IX_ClientSessions_UserId_ConnectedAt",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "DisconnectReason",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "LastRtcmAt",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "RtcmChunkCount",
                table: "SourceConnections");

            migrationBuilder.DropColumn(
                name: "ClientUserAgent",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "DisconnectReason",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "FirstGgaAt",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "GgaFrameCount",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "InvalidGgaFrameCount",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "LastAltitudeMeters",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "LastDifferentialAgeSeconds",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "LastDifferentialStationId",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "LastFixQuality",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "LastGeoidSeparationMeters",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "LastHdop",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "LastSatelliteCount",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "PeakPendingBufferCount",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "StalePositionPeriods",
                table: "ClientSessions");

            migrationBuilder.DropColumn(
                name: "StreamPausedSeconds",
                table: "ClientSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSessions_UserId",
                table: "ClientSessions",
                column: "UserId");
        }
    }
}
