using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    // What the settings page of each platform knows about the LLM providers of card ① and the translation APIs.
    public static class ApiProviders
    {
        public record Provider(string Name, string ApiUrl, string[] Models);

        // Index matches the items of the provider box. The last one is "any OpenAI-compatible API".
        public static readonly Provider[] All =
        {
            new("DeepSeek", "https://api.deepseek.com/chat/completions",
                new[] { "deepseek-chat" }),
            new("Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                new[] { "qwen-plus", "qwen-turbo", "qwen-max", "qwen-mt-turbo", "qwen-mt-plus" }),
            new("Claude", "https://api.anthropic.com/v1/messages",
                new[] { "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5" }),
            new("Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                new[] { "gemini-3.6-flash", "gemini-3.8-flash", "gemini-3.5-flash-lite", "gemini-3.1-pro-preview" }),
            new("Custom", string.Empty, Array.Empty<string>()),
        };

        // Display names for the translation APIs. DeepSeek / Qwen go through the OpenAI-compatible config of card ①.
        public static readonly Dictionary<string, string> ApiNames = new()
        {
            [Summarizer.SUMMARY_API] = "DeepSeek / 通义千问 / Claude / Gemini（用 ① 的设置）",
            ["Google"] = "Google 翻译（免费）",
            ["Google2"] = "Google 翻译 2（免费）",
        };

        // Claude goes through the Anthropic SDK instead of the OpenAI-compatible request.
        public static bool IsClaude(string? apiUrl)
        {
            if (string.IsNullOrWhiteSpace(apiUrl))
                return false;
            // Relays keep the path of the Messages API.
            return apiUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase) ||
                   apiUrl.TrimEnd('/').EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase);
        }

        // Gemini uses its OpenAI-compatible endpoint, with an extra `reasoning_effort` field.
        public static bool IsGemini(string? apiUrl) =>
            !IsClaude(apiUrl) && !string.IsNullOrWhiteSpace(apiUrl) &&
            (apiUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
             apiUrl.Contains("/v1beta/openai", StringComparison.OrdinalIgnoreCase));

        // The provider the configured URL belongs to: the first one when nothing is configured yet, the custom
        // one when the URL is on none of their hosts.
        public static int IndexOf(string? apiUrl)
        {
            for (int i = 0; i < All.Length - 1; i++)
            {
                if (string.IsNullOrEmpty(apiUrl) ||
                    apiUrl.Contains(new Uri(All[i].ApiUrl).Host, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return All.Length - 1;
        }

        public static void Apply(Provider provider, OpenAIConfig config)
        {
            if (!string.IsNullOrEmpty(provider.ApiUrl))
            {
                config.ApiUrl = provider.ApiUrl;
                config.ApiStyle = "standard";
            }
            // Custom: do not stay on the URL of a known provider (it would keep routing to it).
            else if (IsClaude(config.ApiUrl) ||
                     All.Take(All.Length - 1).Any(p => config.ApiUrl.Contains(new Uri(p.ApiUrl).Host,
                         StringComparison.OrdinalIgnoreCase)))
                config.ApiUrl = string.Empty;
            if (provider.Models.Length > 0 && !provider.Models.Contains(config.ModelName))
                config.ModelName = provider.Models[0];
        }

        // Config names must be non-empty and unique within an API; null when the name is fine. `except` is the slot
        // being renamed.
        public static string? ValidateConfigName(string apiName, string? name, TranslateAPIConfig? except = null)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0)
                return "配置名称不能为空。";
            if (Translator.Setting.Configs.TryGetValue(apiName, out var configs) &&
                configs.Any(c => !ReferenceEquals(c, except) &&
                                 string.Equals(c.ConfigName?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                return "已有同名配置，请换一个名称。";
            return null;
        }

        // Pasted keys sometimes carry spaces, line breaks or invisible characters.
        public static string CleanKey(string text)
        {
            return new string(text.Where(c => c > ' ' && c < 0x7F).ToArray());
        }

        // Shows that a key is stored without revealing it.
        public static string MaskKey(string key)
        {
            return $"{key[..Math.Min(3, key.Length)]}…{key[Math.Max(0, key.Length - 4)..]}（{key.Length} 位）";
        }
    }
}
