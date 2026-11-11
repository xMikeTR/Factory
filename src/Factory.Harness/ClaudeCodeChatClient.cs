using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Factory.Harness;

/// FACTORY_PROVIDER=claude-code: model calls go through the local `claude` CLI, so they run on the Claude subscription
/// (after `claude login`) instead of an API key. Each call is one `claude -p` process with the CLI's own tools off.
/// The factory's tools are offered to the model as text and it asks for them with a JSON reply
/// ({"tool_calls":[{"name":..,"arguments":{..}}]}); that is parsed back into FunctionCallContent, so the
/// function-invocation layer above runs them exactly as with any other provider. ANTHROPIC_API_KEY and
/// ANTHROPIC_AUTH_TOKEN are removed from the child's environment so a stray key cannot override the subscription.
public sealed class ClaudeCodeChatClient(string model, string command = "claude",
    Func<IReadOnlyList<string>, string, CancellationToken, Task<string>>? run = null) : IChatClient
{
    private const string ToolRules = "You can call these tools. To call one or more, reply with ONLY this JSON and nothing else: " +
        "{\"tool_calls\":[{\"name\":\"<tool>\",\"arguments\":{...}}]}. You will get the results in the next message. " +
        "When you have what you need, answer in plain text. Never invent tool results.";

    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task<string>> _run = run ?? ((args, stdin, ct) => RunProcess(command, args, stdin, ct));
    private int _seq;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var tools = (options?.Tools ?? []).OfType<AIFunctionDeclaration>().ToList();
        var (system, transcript) = ToPrompt(options?.Instructions, messages, tools);
        var args = new List<string> { "-p", "--output-format", "json", "--tools", "", "--no-session-persistence" };
        var m = options?.ModelId ?? model;
        if (!string.IsNullOrEmpty(m)) { args.Add("--model"); args.Add(m); }
        if (system.Length > 0) { args.Add("--system-prompt"); args.Add(system); }

        var stdout = await _run(args, transcript, cancellationToken);
        JsonNode? j;
        try { j = JsonNode.Parse(stdout); } catch (JsonException) { throw new InvalidOperationException("claude printed something that is not JSON: " + Trim(stdout)); }
        if (j?["is_error"]?.GetValue<bool>() == true) throw new InvalidOperationException("claude reported an error: " + Trim(j["result"]?.ToString() ?? stdout));

        var seq = Interlocked.Increment(ref _seq);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, FromReply(j?["result"]?.ToString() ?? "", tools, seq))) { ModelId = m };
        if (j?["usage"] is JsonObject u)
        {
            long In(string k) => u[k]?.GetValue<long>() ?? 0;
            var input = In("input_tokens") + In("cache_creation_input_tokens") + In("cache_read_input_tokens");
            response.Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = In("output_tokens"), TotalTokenCount = input + In("output_tokens") };
        }
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("claude-code", null, model)
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    /// The system string (instructions, system messages, tool list) and the plain transcript sent on stdin.
    public static (string System, string Transcript) ToPrompt(string? instructions, IEnumerable<ChatMessage> messages, IReadOnlyList<AIFunctionDeclaration> tools)
    {
        var sys = new StringBuilder(instructions ?? "");
        var t = new StringBuilder();
        var names = new Dictionary<string, string>();
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System) { if (sys.Length > 0) sys.Append("\n\n"); sys.Append(msg.Text); continue; }
            foreach (var c in msg.Contents)
                switch (c)
                {
                    case FunctionCallContent f:
                        names[f.CallId] = f.Name;
                        t.Append("Assistant: ").Append(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["name"] = f.Name,
                            ["arguments"] = JsonSerializer.SerializeToNode(f.Arguments ?? new Dictionary<string, object?>()) }) }.ToJsonString()).Append("\n\n"); break;
                    case FunctionResultContent r:
                        t.Append("Tool result (").Append(names.GetValueOrDefault(r.CallId, "tool")).Append("): ")
                            .Append(r.Result is string s ? s : JsonSerializer.Serialize(r.Result)).Append("\n\n"); break;
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        t.Append(msg.Role == ChatRole.Assistant ? "Assistant: " : "User: ").Append(tc.Text).Append("\n\n"); break;
                }
        }
        if (tools.Count > 0)
        {
            if (sys.Length > 0) sys.Append("\n\n");
            sys.Append(ToolRules).Append("\n\nTools:\n");
            foreach (var f in tools) sys.Append("- ").Append(f.Name).Append(": ").Append(f.Description).Append("\n  parameters (JSON Schema): ").Append(f.JsonSchema.GetRawText()).Append('\n');
        }
        return (sys.ToString(), t.ToString().TrimEnd());
    }

    /// A reply that is a {"tool_calls":[..]} object with known tool names becomes function calls; anything else is text.
    public static List<AIContent> FromReply(string text, IReadOnlyList<AIFunctionDeclaration> tools, int seq = 0)
    {
        if (tools.Count > 0 && FindToolCalls(text) is { } calls)
        {
            var known = tools.Select(t => t.Name).ToHashSet();
            var list = new List<AIContent>();
            var n = 0;
            foreach (var c in calls)
            {
                var name = c?["name"]?.GetValue<string>();
                if (name is null || !known.Contains(name)) continue;
                var args = c?["arguments"] is JsonObject o ? o.Deserialize<Dictionary<string, object?>>() : new();
                list.Add(new FunctionCallContent($"call_cc{seq}_{n++}", name, args));
            }
            if (list.Count > 0) return list;
        }
        return [new TextContent(text)];
    }

    private static JsonArray? FindToolCalls(string text)
    {
        for (var i = text.IndexOf('{'); i >= 0; i = text.IndexOf('{', i + 1))
        {
            int depth = 0, end = -1; var inStr = false;
            for (var k = i; k < text.Length && end < 0; k++)
            {
                var ch = text[k];
                if (inStr) { if (ch == '\\') k++; else if (ch == '"') inStr = false; }
                else if (ch == '"') inStr = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0) end = k;
            }
            if (end < 0) return null;
            try { if (JsonNode.Parse(text[i..(end + 1)]) is JsonObject o && o["tool_calls"] is JsonArray a) return a; } catch (JsonException) { /* not JSON, keep scanning */ }
        }
        return null;
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

    private static async Task<string> RunProcess(string command, IReadOnlyList<string> args, string stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("the claude CLI was not found; install Claude Code and run `claude login`"); }
        using (p)
        {
            using var reg = ct.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
            var err = p.StandardError.ReadToEndAsync(ct);
            await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close();
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException($"claude exited {p.ExitCode}: {Trim(await err)}");
            return output;
        }
    }
}
