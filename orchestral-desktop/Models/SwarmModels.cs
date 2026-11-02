using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Orchestral.Desktop.Models;

public record AgentItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("current_task")] string? CurrentTask,
    [property: JsonPropertyName("tokens_used")] long TokensUsed,
    [property: JsonPropertyName("cost_usd")] double CostUsd,
    [property: JsonPropertyName("last_seen")] string LastSeen
);

public record FindingItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("cvss")] double? Cvss,
    [property: JsonPropertyName("cve")] string? Cve,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] string CreatedAt
)
{
    // Compatibility aliases used by the existing XAML bindings.
    public string DiscoveredBy => AgentId;
    public double CvssScore => Cvss ?? 0.0;
    public bool Verified => string.Equals(Status, "confirmed", StringComparison.OrdinalIgnoreCase);
}

public record HitlItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("tool_name")] string ToolName,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("risk_tier")] string RiskTier,
    [property: JsonPropertyName("command_preview")] string CommandPreview,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] string CreatedAt
)
{
    // Compatibility aliases used by the existing XAML bindings.
    public string RequestId => Id;
    public string ActionType => ToolName;
    public string CommandSummary => CommandPreview;
    public string Reasoning => string.Empty;
}

public record BlackboardData(
    [property: JsonPropertyName("total_services")] int TotalServices,
    [property: JsonPropertyName("total_endpoints")] int TotalEndpoints,
    [property: JsonPropertyName("candidates")] int Candidates,
    [property: JsonPropertyName("confirmed_vulnerabilities")] int ConfirmedVulnerabilities
)
{
    // Compatibility aliases used by the existing XAML bindings.
    public int ServicesCount => TotalServices;
    public int EndpointsCount => TotalEndpoints;
    public int CandidatesCount => Candidates;
    public int VerifiedFindingsCount => ConfirmedVulnerabilities;
}

public record TerminalExecResponse(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("container")] string Container,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("exit_code")] int ExitCode,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("duration_sec")] double DurationSec,
    [property: JsonPropertyName("risk_tier")] string RiskTier
);
