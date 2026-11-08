using System.Net;
using System.Net.Http;
using System.Text.Json;

using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    // Calls to Claude (Anthropic Messages API) through the official SDK.
    public static class ClaudeApi
    {
        // Thinking tokens count toward max_tokens, so every request gets some room on top of the answer.
        private const int TOKEN_HEADROOM = 2048;
        private const string DEFAULT_BASE_URL = "https://api.anthropic.com";

        // One client (and its HttpClient) is shared, rebuilt only when the key or base URL changes.
        private static readonly object clientLock = new();
        private static AnthropicClient? sharedClient;
        private static string sharedKey = string.Empty;
        private static string sharedBaseUrl = string.Empty;

        // `turns` alternate user / assistant and end with a user message (no prefill).
        // Temperature is never sent: current Claude models reject it.
        public static async Task<string> Complete(OpenAIConfig config, string system,
            IReadOnlyList<(string role, string content)> turns, int maxTokens, TimeSpan timeout,
            Effort effort = default, int maxRetries = 2, CancellationToken token = default)
        {
            var messages = new List<MessageParam>();
            foreach (var (role, content) in turns)
            {
                if (role == "assistant")
                    messages.Add(new AssistantMessageParam { Content = content });
                else
                    messages.Add(new UserMessageParam { Content = content });
            }

            var client = GetClient(config.ApiKey.Trim(), BaseUrl(config.ApiUrl))
                .WithOptions(o => o with { Timeout = timeout, MaxRetries = maxRetries });
            try
            {
                var response = await client.Messages.Create(new MessageCreateParams
                {
                    Model = config.ModelName,
                    MaxTokens = Math.Max(1024, maxTokens + TOKEN_HEADROOM),
                    System = system,
                    Messages = messages,
                    OutputConfig = new OutputConfig { Effort = effort == default ? Effort.Low : effort },
                }, token);

                if (response.StopReason == StopReason.Refusal)
                    throw new InvalidOperationException("Claude 拒绝回答了这段内容。");
                string text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>()
                    .Select(b => b.Text));
                text = RegexPatterns.ModelThinking().Replace(text.Trim(), string.Empty).Trim();
                if (string.IsNullOrEmpty(text))
                    throw new InvalidOperationException("模型返回了空内容。");
                return text;
            }
            catch (AnthropicApiException ex)
            {
                throw Map(ex, config.ModelName);
            }
            catch (AnthropicIOException ex)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
                throw new HttpRequestException($"连接 Claude 失败：{ex.InnerException?.Message ?? ex.Message}", ex);
            }
        }

        // The configured URL is the full Messages endpoint; the SDK wants the host only.
        private static string BaseUrl(string? apiUrl)
        {
            string url = (apiUrl ?? string.Empty).Trim().TrimEnd('/');
            foreach (var suffix in new[] { "/v1/messages", "/v1" })
            {
                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    url = url[..^suffix.Length];
                    break;
                }
            }
            return url.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : DEFAULT_BASE_URL;
        }

        private static AnthropicClient GetClient(string key, string baseUrl)
        {
            lock (clientLock)
            {
                if (sharedClient == null || sharedKey != key || sharedBaseUrl != baseUrl)
                {
                    // Requests still running on the old client finish on their own handler.
                    sharedClient?.Dispose();
                    sharedClient = new AnthropicClient { ApiKey = key, BaseUrl = baseUrl };
                    sharedKey = key;
                    sharedBaseUrl = baseUrl;
                }
                return sharedClient;
            }
        }

        private static HttpRequestException Map(AnthropicApiException ex, string model)
        {
            int code = (int)ex.StatusCode;
            string detail = ErrorText(ex);
            string message = ex switch
            {
                AnthropicUnauthorizedException => "API Key 无效（HTTP 401），请重新粘贴 Claude 的 API Key（以 sk-ant- 开头）。",
                AnthropicForbiddenException => "Claude 拒绝访问（HTTP 403）：所在地区可能不支持，或 Key 没有权限。",
                AnthropicNotFoundException => $"找不到模型 {model}，请检查模型名。",
                AnthropicRateLimitException => "Claude 请求太频繁或额度用完（HTTP 429），稍后再试。",
                _ when code == 402 || ex.Message.Contains("billing_error") ||
                       ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase) =>
                    "Claude 账户余额不足，请到 console.anthropic.com 充值。",
                _ when code >= 500 || ex.Message.Contains("overloaded") => "Claude 服务暂时繁忙，稍后再试。",
                _ => $"Claude 接口错误：{detail}",
            };
            return new HttpRequestException(message, ex, ex.StatusCode);
        }

        // Only error.message of the JSON body, on one line.
        private static string ErrorText(Exception ex)
        {
            string text = ex.Message;
            int start = text.IndexOf('{');
            if (start >= 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(text[start..]);
                    if (doc.RootElement.TryGetProperty("error", out var error) &&
                        error.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } parsed)
                        text = parsed;
                }
                catch (JsonException) { }
            }
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? text;
        }
    }
}
