using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Orchestral.Desktop.Models;

namespace Orchestral.Desktop.Services;

public class CitadelService
{
    private readonly HttpClient _http;
    private string _apiKey;

    public CitadelService(string? baseUrl = null, string? apiKey = null)
    {
        baseUrl ??= Environment.GetEnvironmentVariable("ORCHESTRAL_CORE_URL") ?? "http://127.0.0.1:8088";
        // Never ship a hard-coded production credential. Operators provide the
        // key via ORCHESTRAL_API_KEY or the desktop settings.
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("ORCHESTRAL_API_KEY") ?? string.Empty;

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _http.DefaultRequestHeaders.Add("X-Orchestral-API-Key", _apiKey);
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            _apiKey = value;
            _http.DefaultRequestHeaders.Remove("X-Orchestral-API-Key");
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _http.DefaultRequestHeaders.Add("X-Orchestral-API-Key", _apiKey);
            }
        }
    }

    public async Task<bool> CheckHealthAsync()
    {
        try
        {
            var res = await _http.GetAsync("/health");
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<AgentItem>> GetAgentsAsync()
    {
        try
        {
            var items = await _http.GetFromJsonAsync<List<AgentItem>>("/api/v1/swarm/agents");
            return items ?? new List<AgentItem>();
        }
        catch
        {
            return new List<AgentItem>();
        }
    }

    public async Task<List<FindingItem>> GetFindingsAsync()
    {
        try
        {
            var items = await _http.GetFromJsonAsync<List<FindingItem>>("/api/v1/findings");
            return items ?? new List<FindingItem>();
        }
        catch
        {
            return new List<FindingItem>();
        }
    }

    public async Task<List<HitlItem>> GetPendingHitlAsync()
    {
        try
        {
            var items = await _http.GetFromJsonAsync<List<HitlItem>>("/api/v1/hitl/pending");
            return items ?? new List<HitlItem>();
        }
        catch
        {
            return new List<HitlItem>();
        }
    }

    public async Task<bool> SendHitlDecisionAsync(string requestId, bool approved, string rationale)
    {
        try
        {
            // The core exposes separate approve/deny endpoints keyed by id.
            string action = approved ? "approve" : "deny";
            var res = await _http.PostAsync($"/api/v1/hitl/{Uri.EscapeDataString(requestId)}/{action}", null);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<BlackboardData?> GetBlackboardSnapshotAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<BlackboardData>("/api/v1/blackboard/summary");
        }
        catch
        {
            return null;
        }
    }
}
