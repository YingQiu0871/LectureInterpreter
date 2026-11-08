using System.Net;
using System.Net.Http;

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

        // `turns` alternate user / assistant and end with a user message (no prefill).
        // Temperature is never sent: current Claude models reject it.
        public static async Task<string> Complete(OpenAIConfig config, string system,
            IReadOnlyList<(string role, string content)> turns, int maxTokens, TimeSpan timeout,
            CancellationToken token = default)
        {
            var messages = new List<MessageParam>();
            foreach (var (role, content) in turns)
            {
                if (role == "assistant")
                    messages.Add(new AssistantMessageParam { Content = content });
                else
                    messages.Add(new UserMessageParam { Content = content });
            }

            AnthropicClient client = new()
            {
                ApiKey = config.ApiKey.Trim(),
                Timeout = timeout,
                MaxRetries = 0,
            };
            try
            {
                var response = await client.Messages.Create(new MessageCreateParams
                {
                    Model = config.ModelName,
                    MaxTokens = Math.Max(1024, maxTokens + TOKEN_HEADROOM),
                    System = system,
                    Messages = messages,
                    OutputConfig = new OutputConfig { Effort = Effort.Low },
                }, token);

                if (response.StopReason?.ToString() == "refusal")
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
                throw new HttpRequestException($"连接 Claude 失败：{ex.Message}", ex);
            }
        }

        private static HttpRequestException Map(AnthropicApiException ex, string model)
        {
            int code = (int)ex.StatusCode;
            string message = ex switch
            {
                AnthropicUnauthorizedException => "API Key 无效（HTTP 401），请重新粘贴 Claude 的 API Key（以 sk-ant- 开头）。",
                AnthropicForbiddenException => "Claude 拒绝访问（HTTP 403）：所在地区可能不支持，或 Key 没有权限。",
                AnthropicNotFoundException => $"找不到模型 {model}，请检查模型名。",
                AnthropicRateLimitException => "Claude 请求太频繁或额度用完（HTTP 429），稍后再试。",
                _ when code == 402 || ex.Message.Contains("billing_error") => "Claude 账户余额不足，请到 console.anthropic.com 充值。",
                _ when code >= 500 || ex.Message.Contains("overloaded") => "Claude 服务暂时繁忙，稍后再试。",
                _ => $"Claude 接口错误：{ex.Message}",
            };
            return new HttpRequestException(message, ex, ex.StatusCode);
        }
    }
}
