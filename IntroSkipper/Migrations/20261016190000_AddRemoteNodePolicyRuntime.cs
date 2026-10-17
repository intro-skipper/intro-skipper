using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntroSkipper.Migrations;

/// <summary>Adds isolated server policy and node runtime storage without changing legacy declarations.</summary>
public partial class AddRemoteNodePolicyRuntime : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "PolicyJson", table: "RemoteNodes", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "RuntimeJson", table: "RemoteNodes", type: "TEXT", nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PolicyJson", table: "RemoteNodes");
        migrationBuilder.DropColumn(name: "RuntimeJson", table: "RemoteNodes");
    }
}
