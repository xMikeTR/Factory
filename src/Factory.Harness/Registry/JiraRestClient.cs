using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Factory.Harness.Registry;

/// Jira's REST API, only what the one-way mirror needs: read an issue's status and move it to a status by name.
/// Auth is a token the user provides (JIRA_URL, JIRA_TOKEN, and JIRA_USER for Cloud's email + API token basic auth;
/// without JIRA_USER the token is sent as a bearer PAT). The token is never logged and never put in an error message.
public sealed class JiraRestClient(HttpClient http, string baseUrl, string? user, string token)
{
    private readonly Uri _base = new(baseUrl.TrimEnd('/') + "/");

    public static JiraRestClient? FromEnvironment(IReadOnlyDictionary<string, string> env, HttpClient? http = null) =>
        env.GetValueOrDefault("JIRA_URL") is { Length: > 0 } url && env.GetValueOrDefault("JIRA_TOKEN") is { Length: > 0 } token
            ? new JiraRestClient(http ?? new HttpClient(), url, env.GetValueOrDefault("JIRA_USER"), token)
            : null;

    private HttpRequestMessage Request(HttpMethod method, string path, string? json = null)
    {
        var r = new HttpRequestMessage(method, new Uri(_base, path));
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        r.Headers.Authorization = string.IsNullOrEmpty(user)
            ? new AuthenticationHeaderValue("Bearer", token)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}")));
        if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return r;
    }

    /// The issue's status name, or null when Jira does not know the issue (or the token cannot see it).
    public async Task<string?> StatusAsync(string key, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, $"rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=status"), ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden) return null;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("fields").GetProperty("status").GetProperty("name").GetString();
    }

    /// Moves the issue through the workflow transition that ends in `status`. Null when done, otherwise why not
    /// (the workflow offers no such transition from the issue's current status).
    public async Task<string?> TransitionAsync(string key, string status, CancellationToken ct)
    {
        var path = $"rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions";
        using var list = await http.SendAsync(Request(HttpMethod.Get, path), ct);
        list.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.GetProperty("transitions").EnumerateArray()
            .Where(t => string.Equals(t.GetProperty("to").GetProperty("name").GetString(), status, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.GetProperty("id").GetString()).FirstOrDefault();
        if (id is null) return $"{key}: the workflow has no transition to '{status}' from its current status";
        using var post = await http.SendAsync(Request(HttpMethod.Post, path, JsonSerializer.Serialize(new { transition = new { id } })), ct);
        post.EnsureSuccessStatusCode();
        return null;
    }
}
