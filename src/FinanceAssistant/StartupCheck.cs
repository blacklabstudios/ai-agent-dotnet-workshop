using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FinanceAssistant.Data;
using Microsoft.Extensions.Configuration;

namespace FinanceAssistant;

// Scaffolding. No exercise asks you to change this file.
//
// Runs before the REPL and tells you, in plain words, what's wrong with your setup:
// a missing or malformed secret, Postgres not running, a key from the wrong resource,
// a deployment that isn't where your endpoint points. Then it stops, so the first
// error you see names the fix instead of arriving as a stack trace mid-exercise.
//
// It talks to the model service with a plain HttpClient, not your IChatClient.
// So if this check passes and your code still fails, the problem is in your code.
//
// Skip it with the environment variable StartupCheck__Skip=true.
public static class StartupCheck
{
    private const string SecretsFolder = "src/FinanceAssistant";
    private const string ChatKey = "AzureOpenAI:Deployment";
    private const string EmbeddingKey = "AzureOpenAI:EmbeddingDeployment";
    private const string JudgeKey = "AzureOpenAI:JudgeDeployment";

    private static readonly string[] RequiredKeys =
    [
        "AzureOpenAI:Endpoint",
        "AzureOpenAI:ApiKey",
        ChatKey,
        "AzureOpenAI:ReasoningEffort",
    ];

    // The values Microsoft.Extensions.AI's ReasoningEffort enum takes, and what each
    // one is called on the wire. ExtraHigh has no wire value here: the check leaves it
    // out of its request rather than guess.
    private static readonly Dictionary<string, string?> ReasoningLevels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["None"] = "none",
            ["Low"] = "low",
            ["Medium"] = "medium",
            ["High"] = "high",
            ["ExtraHigh"] = null,
        };

    public static async Task<bool> RunAsync(IConfiguration config)
    {
        if (string.Equals(config["StartupCheck:Skip"], "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Console.WriteLine("Checking your setup...");
        var report = new Report();

        var settings = CheckSecrets(config, report);
        await CheckDatabaseAsync(report);
        if (settings is not null)
        {
            await CheckDeploymentsAsync(settings, report);
        }

        return report.Finish();
    }

    // ---- Secrets ------------------------------------------------------------------

    private sealed record Settings(Uri ApiBase, string ApiKey, string ResourceName, bool IsAzure,
        string? ReasoningWireValue, string ReasoningSetting, List<Probe> Probes);

    private static Settings? CheckSecrets(IConfiguration config, Report report)
    {
        var missing = RequiredKeys.Where(k => string.IsNullOrWhiteSpace(config[k])).ToList();

        if (missing.Count == RequiredKeys.Length)
        {
            report.Info("Azure: not set up yet. P1.01 does that. Skipping the model checks.");
            return null;
        }

        var ok = true;
        foreach (var key in missing)
        {
            ok = false;
            report.Problem($"Secret {key} is missing.",
                $"From {SecretsFolder}/, run: dotnet user-secrets set \"{key}\" \"<value>\"",
                "The other three are set, so you're in the right folder. This one didn't land.");
        }

        foreach (var key in RequiredKeys.Append(EmbeddingKey).Append(JudgeKey))
        {
            var value = config[key];
            if (value is not null && value.Length > 0 && (value != value.Trim() || value.Trim().StartsWith('"') || value.Trim().StartsWith('\'')))
            {
                ok = false;
                report.Problem($"Secret {key} starts or ends with a space or a quote.",
                    "Set it again, with only the value between the quotes of the set command.");
            }
        }

        var endpointText = config["AzureOpenAI:Endpoint"]?.Trim();
        var apiKey = config["AzureOpenAI:ApiKey"]?.Trim();
        var reasoningSetting = config["AzureOpenAI:ReasoningEffort"]?.Trim();

        Uri? apiBase = null;
        var isAzure = false;
        if (endpointText is not null)
        {
            apiBase = CheckEndpoint(endpointText, report, out isAzure);
            ok &= apiBase is not null;
        }

        if (apiKey is not null && (apiKey.Contains('<') || apiKey.Contains("KEY 1", StringComparison.OrdinalIgnoreCase)))
        {
            ok = false;
            report.Problem("Secret AzureOpenAI:ApiKey still holds the placeholder from the guide.",
                "Copy KEY 1 from your resource's Keys and Endpoint pane and set it again.");
        }

        string? reasoningWireValue = null;
        if (reasoningSetting is not null)
        {
            if (!ReasoningLevels.TryGetValue(reasoningSetting, out reasoningWireValue))
            {
                ok = false;
                report.Problem($"Secret AzureOpenAI:ReasoningEffort is \"{reasoningSetting}\", which isn't a level the app can use.",
                    "Use None on Path A (gpt-5.6-luna) or Low on Path B (gpt-5-mini).",
                    "The valid values are None, Low, Medium, High and ExtraHigh.");
            }
        }

        if (!ok || apiBase is null || apiKey is null || reasoningSetting is null)
        {
            return null;
        }

        report.Ok("Secrets: Endpoint, ApiKey, Deployment and ReasoningEffort are set.");

        var probes = new List<Probe>
        {
            new("Chat", ChatKey, config[ChatKey]!.Trim(), ProbeKind.Chat, SendReasoning: true),
        };
        if (!string.IsNullOrWhiteSpace(config[EmbeddingKey]))
        {
            probes.Add(new("Embeddings", EmbeddingKey, config[EmbeddingKey]!.Trim(), ProbeKind.Embedding, SendReasoning: false));
        }
        if (!string.IsNullOrWhiteSpace(config[JudgeKey]))
        {
            probes.Add(new("Judge", JudgeKey, config[JudgeKey]!.Trim(), ProbeKind.Chat, SendReasoning: false));
        }

        return new Settings(apiBase, apiKey, ResourceNameOf(apiBase, isAzure), isAzure,
            reasoningWireValue, reasoningSetting, probes);
    }

    private static Uri? CheckEndpoint(string endpoint, Report report, out bool isAzure)
    {
        isAzure = false;
        const string key = "AzureOpenAI:Endpoint";

        if (endpoint.Contains("your-resource", StringComparison.OrdinalIgnoreCase))
        {
            report.Problem($"Secret {key} still holds the placeholder from the guide.",
                "Copy \"Endpoint (OpenAI)\" from your resource's Keys and Endpoint pane, add openai/v1/, and set it again.");
            return null;
        }

        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            report.Problem($"Secret {key} isn't a full URL: \"{endpoint}\".",
                "It needs https:// at the start, like https://your-resource.openai.azure.com/openai/v1/");
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        isAzure = host.EndsWith(".openai.azure.com") || host.EndsWith(".cognitiveservices.azure.com")
            || host.EndsWith(".services.ai.azure.com");

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            report.Problem($"Secret {key} starts with http://.", "Use https://. Only a model running on this machine uses http.");
            return null;
        }

        if (isAzure)
        {
            if (path.Contains("/api/projects", StringComparison.OrdinalIgnoreCase))
            {
                report.Problem($"Secret {key} is the Foundry project endpoint, not the OpenAI one.",
                    "In the Azure portal, open your resource, then Keys and Endpoint. Copy \"Endpoint (OpenAI)\", the one ending in .openai.azure.com, and add openai/v1/.");
                return null;
            }
            if (path.EndsWith("/openai/v1/openai/v1", StringComparison.OrdinalIgnoreCase))
            {
                report.Problem($"Secret {key} has openai/v1/ twice.", $"Set it to https://{uri.Host}/openai/v1/");
                return null;
            }
            if (!path.Equals("/openai/v1", StringComparison.OrdinalIgnoreCase))
            {
                report.Problem(
                    path.Length == 0
                        ? $"Secret {key} is missing openai/v1/ at the end."
                        : $"Secret {key} ends in \"{path}/\". It should end in /openai/v1/ and nothing after it.",
                    $"Set it to https://{uri.Host}/openai/v1/");
                return null;
            }
        }
        else if (host == "api.openai.com" && !path.Equals("/v1", StringComparison.OrdinalIgnoreCase))
        {
            report.Problem($"Secret {key} should be exactly https://api.openai.com/v1/", "Set it again with that value.");
            return null;
        }

        return uri;
    }

    private static string ResourceNameOf(Uri apiBase, bool isAzure) =>
        isAzure ? apiBase.Host.Split('.')[0] : apiBase.Host;

    // ---- Database -----------------------------------------------------------------

    private static async Task CheckDatabaseAsync(Report report)
    {
        try
        {
            await using var db = new FinanceDbContext();
            if (await db.Database.CanConnectAsync())
            {
                report.Ok("Database: Postgres is answering on localhost:5432.");
                return;
            }
        }
        catch (Exception)
        {
            // Falls through to the same message. CanConnectAsync already swallows most failures.
        }

        report.Problem("Database: can't reach Postgres on localhost:5432.",
            "From the repo root, run: docker compose up -d",
            "Then docker compose ps should show finance-assistant-postgres running. Docker itself has to be running first.");
    }

    // ---- Deployments --------------------------------------------------------------

    private enum ProbeKind { Chat, Embedding }

    private sealed record Probe(string Label, string SecretKey, string Deployment, ProbeKind Kind, bool SendReasoning);

    private sealed record ProbeResult(HttpStatusCode? Status, string? ErrorCode, string? ErrorMessage,
        string? Model, Exception? Exception)
    {
        public bool Succeeded => Status is not null && (int)Status < 300;
    }

    private static async Task CheckDeploymentsAsync(Settings settings, Report report)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        // In parallel, so three deployments cost the time of the slowest, not the sum.
        var results = await Task.WhenAll(settings.Probes.Select(p => SendAsync(http, settings, p)));

        var chat = results[0];
        for (var i = 0; i < settings.Probes.Count; i++)
        {
            var probe = settings.Probes[i];
            var result = results[i];
            var title = $"{probe.Label} deployment \"{probe.Deployment}\"";

            if (result.Succeeded)
            {
                report.Ok(result.Model is null ? $"{title} answered." : $"{title} answered, running {result.Model}.");
                continue;
            }

            Explain(report, settings, probe, result, chatWorked: i > 0 && chat.Succeeded, chatDeployment: settings.Probes[0].Deployment);
        }
    }

    private static async Task<ProbeResult> SendAsync(HttpClient http, Settings settings, Probe probe)
    {
        var body = new Dictionary<string, object> { ["model"] = probe.Deployment };
        if (probe.Kind == ProbeKind.Embedding)
        {
            body["input"] = "ok";
        }
        else
        {
            body["messages"] = Hello;
            body["max_completion_tokens"] = 16;
        }

        // The chat probe asks the way the agent will: with your reasoning setting and, from
        // P2.01 on, with tools. Some models refuse that pair on Chat Completions, and a probe
        // without a tool would pass while the agent's first real question fails.
        if (probe.SendReasoning)
        {
            body["tools"] = ProbeTools;
            if (settings.ReasoningWireValue is not null)
            {
                body["reasoning_effort"] = settings.ReasoningWireValue;
            }
        }

        var path = probe.Kind == ProbeKind.Embedding ? "embeddings" : "chat/completions";

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(settings.ApiBase, path), content);
            var text = await response.Content.ReadAsStringAsync();
            var (model, code, message) = ParseBody(text);
            return new ProbeResult(response.StatusCode, code, message, model, null);
        }
        catch (Exception ex)
        {
            return new ProbeResult(null, null, null, null, ex);
        }
    }

    private static readonly object[] Hello = [new { role = "user", content = "Reply with OK." }];

    private static readonly object[] ProbeTools =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "startup_check",
                description = "Not a real tool. Present so the request looks like the agent's.",
                parameters = new { type = "object", properties = new { } },
            },
        },
    ];

    private static (string? Model, string? Code, string? Message) ParseBody(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, Shorten(text));
            }
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                return (null,
                    error.TryGetProperty("code", out var c) && c.ValueKind != JsonValueKind.Null ? c.ToString() : null,
                    error.TryGetProperty("message", out var m) ? Shorten(m.ToString()) : null);
            }
            return (root.TryGetProperty("model", out var model) ? model.ToString() : null, null, null);
        }
        catch (JsonException)
        {
            return (null, null, Shorten(text));
        }
    }

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 300 ? flat : flat[..300] + "...";
    }

    private static void Explain(Report report, Settings s, Probe probe, ProbeResult r, bool chatWorked, string chatDeployment)
    {
        var title = $"{probe.Label} deployment \"{probe.Deployment}\"";
        var service = s.IsAzure ? "Azure" : "The service";
        // Azure often repeats the status as the error code ("401" for a 401). Say it once.
        var errorCode = r.ErrorCode == ((int?)r.Status)?.ToString() ? null : r.ErrorCode;
        var said = errorCode is null && r.ErrorMessage is null
            ? null
            : $"{service} said: {(int?)r.Status} {errorCode}{(errorCode is null ? "" : ". ")}{r.ErrorMessage}".TrimEnd();

        void Fail(string what, params string[] fix) =>
            report.Problem($"{title}: {what}", said is null ? fix : [.. fix, said]);

        void Warn(string what, params string[] fix) =>
            report.Warning($"{title}: {what}", said is null ? fix : [.. fix, said]);

        if (r.Exception is not null)
        {
            var socket = FindInner<SocketException>(r.Exception);
            if (socket?.SocketErrorCode == SocketError.HostNotFound)
            {
                Fail($"can't find {s.ApiBase.Host}.",
                    "The resource name in AzureOpenAI:Endpoint is misspelled, or the resource no longer exists.",
                    "Copy the endpoint from the portal again rather than typing it.");
            }
            else if (socket?.SocketErrorCode == SocketError.ConnectionRefused)
            {
                Fail($"nothing is listening at {s.ApiBase.Authority}.", "Check AzureOpenAI:Endpoint.");
            }
            else if (r.Exception is TaskCanceledException)
            {
                Warn("no answer in 30 seconds.",
                    "Check your network. A VPN or a corporate proxy is the usual cause.");
            }
            else
            {
                Fail("the request didn't go through.", r.Exception.GetBaseException().Message);
            }
            return;
        }

        var code = r.ErrorCode ?? "";
        var message = r.ErrorMessage ?? "";

        switch ((int)r.Status!)
        {
            case 401 when !s.IsAzure:
                Fail($"{s.ResourceName} rejected the key.",
                    "Check AzureOpenAI:ApiKey. On the OpenAI path, create a new key at platform.openai.com and set it again.");
                break;

            case 404 when !s.IsAzure:
                Fail($"not found on {s.ResourceName}.",
                    $"Check {probe.SecretKey}. On the OpenAI path it holds the model name, like gpt-5.6-luna, not a deployment name.");
                break;

            case 401:
                Fail($"{s.ResourceName} rejected the key.",
                    "The key and the endpoint have to come from the same resource.",
                    $"In the Azure portal, open {s.ResourceName}, then Keys and Endpoint. Copy KEY 1 and the endpoint again, together.");
                break;

            case 403:
                Fail($"{s.ResourceName} refused the request.",
                    "The key was read, but the resource won't serve this machine. Check the resource's Networking page allows all networks.");
                break;

            case 404 when chatWorked:
                Fail($"not found on {s.ResourceName}.",
                    $"Your endpoint and key work for \"{chatDeployment}\", so this deployment is most likely on a different resource.",
                    "When a model isn't offered in your resource's region, Foundry can create a second resource in another region to host it. That resource has its own endpoint and key.",
                    $"Deploy \"{probe.Deployment}\" again on {s.ResourceName}, in Foundry's Models + endpoints. If the model isn't offered there, raise your hand.");
                break;

            case 404 when code.Equals("DeploymentNotFound", StringComparison.OrdinalIgnoreCase):
                Fail($"not found on {s.ResourceName}.",
                    $"Compare {probe.SecretKey} with the name in Foundry's Models + endpoints, character for character.",
                    $"If the name matches, the deployment is on another resource. Check it's on {s.ResourceName}.");
                break;

            case 404:
                Fail($"not found on {s.ResourceName}.",
                    $"Either {probe.SecretKey} doesn't match a deployment name in Foundry's Models + endpoints, or the endpoint points at the wrong resource.",
                    "Check the name character for character, then copy the endpoint from the portal again.");
                break;

            case 400 when code.Equals("OperationNotSupported", StringComparison.OrdinalIgnoreCase):
                Fail(probe.Kind == ProbeKind.Embedding ? "runs a model that can't make embeddings." : "runs a model that can't chat.",
                    $"{probe.SecretKey} probably holds the name of another deployment. Check which model each deployment runs in Foundry.");
                break;

            // A warning, not a stop: P1.02's reasoning Extra turns effort up before the agent has
            // any tools, and that works. It only breaks from P2.01, when tools go out with every request.
            case 400 when probe.SendReasoning && message.Contains("Function tools", StringComparison.OrdinalIgnoreCase):
                Warn($"won't take tools at reasoning effort \"{s.ReasoningSetting}\".",
                    "On the Chat Completions API this model only takes tools with AzureOpenAI:ReasoningEffort set to None.",
                    "Fine for P1.02's reasoning Extra. From P2.01 the agent sends tools, so set it back to None before then.");
                break;

            case 400 when probe.SendReasoning && message.Contains("reasoning", StringComparison.OrdinalIgnoreCase):
                Fail($"doesn't take reasoning effort \"{s.ReasoningSetting}\".",
                    "AzureOpenAI:ReasoningEffort has to match the model behind it: None on Path A (gpt-5.6-luna), Low on Path B (gpt-5-mini).");
                break;

            case 400:
                Fail("rejected the request.", "Read the message below. If it doesn't help, raise your hand.");
                break;

            case 429:
                Warn("is rate limited or out of quota.",
                    "Common on the Free Tier. Wait a minute and run again.",
                    "If it keeps happening, check the deployment's tokens per minute in Foundry.");
                break;

            case >= 500:
                Warn("hit a server error on the service's side.", "Wait a moment and run again.");
                break;

            default:
                Fail($"unexpected answer, {(int)r.Status}.", "Raise your hand.");
                break;
        }
    }

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is T match) return match;
        }
        return null;
    }

    // ---- Output -------------------------------------------------------------------

    // ASCII markers on purpose: the Windows console doesn't always render check marks.
    private sealed class Report
    {
        private int _problems;
        private int _warnings;

        public void Ok(string line) => Console.WriteLine($"  [ok] {line}");

        public void Info(string line) => Console.WriteLine($"  [--] {line}");

        public void Problem(string line, params string[] details)
        {
            _problems++;
            Write("[!!]", line, details);
        }

        public void Warning(string line, params string[] details)
        {
            _warnings++;
            Write("[??]", line, details);
        }

        private static void Write(string marker, string line, string[] details)
        {
            Console.WriteLine($"  {marker} {line}");
            foreach (var detail in details)
            {
                Console.WriteLine($"       {detail}");
            }
        }

        public bool Finish()
        {
            if (_problems > 0)
            {
                Console.WriteLine();
                Console.WriteLine(_problems == 1
                    ? "1 problem. Fix it and run again."
                    : $"{_problems} problems. Fix them and run again.");
                Console.WriteLine("To skip this check, set the environment variable StartupCheck__Skip=true.");
                return false;
            }

            if (_warnings > 0)
            {
                Console.WriteLine("  Starting anyway. Read the warning above before you go on.");
            }

            Console.WriteLine();
            return true;
        }
    }
}
