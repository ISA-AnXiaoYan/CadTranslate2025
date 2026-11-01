using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CadTranslate2025;

public sealed class DeepSeekClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;

    public DeepSeekClient(string model = "deepseek-v4-flash")
    {
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("未找到 DEEPSEEK_API_KEY。请先在当前 Windows 用户环境中配置密钥。工程不会读取明文配置文件中的密钥。");
        _model = model;
        _http = new HttpClient { BaseAddress = new Uri("https://api.deepseek.com/"), Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CadTranslate2025/0.1");
    }

    public async Task<IReadOnlyList<TranslationResult>> TranslateAsync(
        IReadOnlyList<TranslationRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) return [];
        var payload = new
        {
            model = _model,
            thinking = new { type = "disabled" },
            temperature = 0.0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = "你是建筑及机电图纸术语翻译器。只翻译文本，不改动编号、尺寸、日期、单位、代码和占位符。必须输出JSON对象，字段为translations；每项包含id、target、confidence、warning。" },
                new { role = "user", content = BuildPrompt(requests) }
            }
        };

        var body = JsonSerializer.Serialize(payload);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var response = await _http.PostAsync("chat/completions", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var parsed = ParseResponse(content, requests);
                if (parsed.Count > 0) return parsed;
                throw new InvalidOperationException("DeepSeek返回了空翻译结果。");
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
                continue;
            }
            throw new InvalidOperationException($"DeepSeek API错误 {(int)response.StatusCode}: {Redact(content)}");
        }
        throw new InvalidOperationException("DeepSeek重试三次后仍未成功。");
    }

    private static string BuildPrompt(IReadOnlyList<TranslationRequest> requests)
    {
        var direction = requests[0].Direction == TranslationDirection.ChineseToEnglish ? "中文翻译为英文" : "英文翻译为中文";
        var rows = requests.Select(r => new
        {
            id = r.Id,
            source = r.ProtectedSource,
            glossary = r.RelevantTerms.Select(t => new { zh = t.Zh, en = t.En, aliases = t.Aliases }).ToArray()
        });
        return $"方向：{direction}。严格遵守项目术语。请输出JSON。\n" + JsonSerializer.Serialize(rows);
    }

    private static List<TranslationResult> ParseResponse(string content, IReadOnlyList<TranslationRequest> requests)
    {
        using var root = JsonDocument.Parse(content);
        var message = root.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(message)) return [];
        message = StripCodeFence(message);
        using var json = JsonDocument.Parse(message);
        var array = json.RootElement.TryGetProperty("translations", out var items) ? items : json.RootElement;
        var sourceById = requests.ToDictionary(x => x.Id);
        var output = new List<TranslationResult>();
        foreach (var item in array.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            if (!sourceById.TryGetValue(id, out var request)) continue;
            var target = item.GetProperty("target").GetString() ?? request.Source;
            output.Add(new TranslationResult(id, request.Source, target,
                ReadOptionalScalar(item, "confidence"),
                ReadOptionalScalar(item, "warning"), false, false));
        }
        return output;
    }

    private static string? ReadOptionalScalar(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw new InvalidOperationException($"DeepSeek字段 {propertyName} 必须是标量。")
        };
    }

    private static string Redact(string value) => value.Length > 400 ? value[..400] : value;
    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                return trimmed[(firstNewline + 1)..lastFence].Trim();
        }
        return trimmed;
    }
    public void Dispose() => _http.Dispose();
}

