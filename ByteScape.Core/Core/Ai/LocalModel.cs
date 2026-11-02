using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByteScape.Core.Ai;

/// <summary>
/// Optional local language model behind an HTTP endpoint the user configures: Ollama (native /api/chat)
/// or any OpenAI compatible server (/v1/chat/completions, e.g. LM Studio, llama.cpp, vLLM). Off by default;
/// nothing is contacted unless <see cref="Config.AppConfig.AiEnabled"/> is on. Only names and sizes are ever sent.
/// </summary>
public sealed class LocalModel
{
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public string Endpoint { get; }
    public string Model { get; }
    public string ApiKey { get; }
    public TimeSpan Timeout { get; }

    /// <summary>OpenAI style when the endpoint path ends with /v1, otherwise Ollama's native API.</summary>
    public bool OpenAiStyle => Endpoint.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase);

    public LocalModel(string endpoint, string model, string apiKey = "", int timeoutSeconds = 60)
    {
        Endpoint = string.IsNullOrWhiteSpace(endpoint) ? "http://localhost:11434" : endpoint.Trim().TrimEnd('/');
        Model = model.Trim();
        ApiKey = apiKey.Trim();
        Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600));
    }

    public static LocalModel FromConfig(Config.AppConfig c) => new(c.AiEndpoint, c.AiModel, c.AiApiKey, c.AiTimeoutSeconds);

    /// <summary>Lists the models the endpoint offers (also serves as the connection test).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var url = OpenAiStyle ? Endpoint + "/models" : Endpoint + "/api/tags";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Auth(req);
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        var arr = OpenAiStyle ? json?["data"]?.AsArray() : json?["models"]?.AsArray();
        var list = new List<string>();
        if (arr != null)
            foreach (var m in arr)
            {
                var name = OpenAiStyle ? m?["id"]?.GetValue<string>() : (m?["name"]?.GetValue<string>() ?? m?["model"]?.GetValue<string>());
                if (!string.IsNullOrEmpty(name)) list.Add(name);
            }
        return list;
    }

    /// <summary>One chat completion; returns the assistant text (trimmed). Throws on transport / HTTP errors.</summary>
    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct = default, double temperature = 0.1, int maxTokens = 400)
    {
        if (Model.Length == 0) throw new InvalidOperationException("No model name configured (Settings > Advanced > Local AI).");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        string url; JsonObject body;
        if (OpenAiStyle)
        {
            url = Endpoint + "/chat/completions";
            body = new JsonObject
            {
                ["model"] = Model, ["temperature"] = temperature, ["max_tokens"] = maxTokens, ["stream"] = false,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = user }),
            };
        }
        else
        {
            url = Endpoint + "/api/chat";
            body = new JsonObject
            {
                ["model"] = Model, ["stream"] = false,
                ["options"] = new JsonObject { ["temperature"] = temperature, ["num_predict"] = maxTokens },
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = user }),
            };
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        Auth(req);
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(text, 300)}");
        var json = JsonNode.Parse(text);
        string? answer = OpenAiStyle
            ? json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            : json?["message"]?["content"]?.GetValue<string>();
        return (answer ?? "").Trim();
    }

    private void Auth(HttpRequestMessage req)
    {
        if (ApiKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
}
