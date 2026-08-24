// The workshop-progress skill's tool. It reads the guides in docs/, your working tree and the coding
// agent's notes in progress.json, and writes progress.data.js, which progress.html at the repo root loads.
//
//   dotnet run .agents/skills/workshop-progress/scripts/Progress.cs               refreshes progress.data.js
//   dotnet run .agents/skills/workshop-progress/scripts/Progress.cs -- --status   prints JSON for a coding agent
//
// Nothing here calls a model or the database.

// File-based apps default to native AOT, which turns off reflection-based JSON. This tool doesn't need AOT.
#:property PublishAot=false

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = FindRepoRoot(Directory.GetCurrentDirectory())
    ?? throw new InvalidOperationException("Run this from inside the workshop repo (no FinanceAssistant.sln found above here).");

var course = new[]
{
    new Entry("p1-01", "core", "P1", "docs/exercises/p1-01-configure-azure.md", null, Secrets),
    new Entry("p1-02", "core", "P1", "docs/exercises/p1-02-wire-ichatclient.md", "p1-02-end", File_("src/FinanceAssistant/ServiceCollectionExtensions.cs")),
    new Entry("p2-01", "core", "P2", "docs/exercises/p2-01-convert-currency-tool.md", "p2-01-end", File_("src/FinanceAssistant/Tools/ConvertCurrencyTool.cs")),
    new Entry("p2-02", "core", "P2", "docs/exercises/p2-02-transactions-tools.md", "p2-02-end", File_("src/FinanceAssistant/Tools/SearchTransactionsTool.cs")),
    new Entry("p3-01", "core", "P3", "docs/exercises/p3-01-agent-loop.md", "p3-01-end", File_("src/FinanceAssistant/ChatAgent.cs")),
    new Entry("p4-01", "core", "P4", "docs/exercises/p4-01-conversation-history.md", "p4-01-end", File_("src/FinanceAssistant/Memory/ConversationStore.cs")),
    new Entry("p4-02", "core", "P4", "docs/exercises/p4-02-summarising-history.md", "p4-02-end", File_("src/FinanceAssistant/Memory/SummarizingHistoryReducer.cs")),
    new Entry("p5-01", "core", "P5", "docs/exercises/p5-01-confirmation-gate.md", "p5-01-end", File_("src/FinanceAssistant/Tools/TransferFundsTool.cs")),
    new Entry("p5-02", "core", "P5", "docs/exercises/p5-02-evaluate-responses.md", "p5-02-end", File_("tests/FinanceAssistant.Evals/AdviceBoundaryEvals.cs")),
    new Entry("p6-01", "core", "P6", "docs/exercises/p6-01-mcp-server.md", "p6-01-end", File_("src/FinanceAssistant.McpServer/McpTools.cs")),
    new Entry("p2-b01", "bonus", "P2", "docs/bonus/p2-b01-import-csv.md", "bonus-all", File_("src/FinanceAssistant/Tools/ImportStatementTool.cs")),
    new Entry("p2-b02", "bonus", "P2", "docs/bonus/p2-b02-analyze-expenses.md", "bonus-all", File_("src/FinanceAssistant/Tools/AnalyzeExpensesTool.cs")),
    new Entry("p5-b01", "bonus", "P5", "docs/bonus/p5-b01-trustworthy-evals.md", "bonus-all", File_("tests/FinanceAssistant.Evals/ToolCallAccuracyEvals.cs")),
    new Entry("p6-b01", "bonus", "P6", "docs/bonus/p6-b01-web-chat.md", "bonus-all", File_("src/FinanceAssistant.Web/Program.cs")),
    new Entry("demo-p5", "demo", "P5", "docs/demos/p5-01-observe-with-opentelemetry.md", "demo-p5-otel", File_("src/FinanceAssistant/LlmCostTelemetry.cs")),
    new Entry("demo-p6-1", "demo", "P6", "docs/demos/p6-01-migrate-to-agent-framework.md", "demo-p6-maf~1", Contains("src/FinanceAssistant/FinanceAssistant.csproj", "Microsoft.Agents.AI")),
    new Entry("demo-p6-2", "demo", "P6", "docs/demos/p6-02-multi-agent-handoff.md", "demo-p6-maf", File_("tests/FinanceAssistant.Evals/CoachGroundingEvals.cs")),
};

var pillars = new Dictionary<string, (string Name, string What)>
{
    ["P1"] = ("Connect", "Get a model running and call it from C#."),
    ["P2"] = ("Tools", "Give the model functions it can call: a converter, then your data."),
    ["P3"] = ("Agent loop", "Write the loop yourself, with a cap."),
    ["P4"] = ("Memory", "Remember the conversation, and keep it bounded."),
    ["P5"] = ("Safety and evals", "Approve risky actions, measure the answers."),
    ["P6"] = ("MCP", "Share the agent's tools with other clients."),
};

var repoUrl = RepoWebUrl(root);
var core = course.Where(e => e.Kind == "core").ToList();
var page = Path.Combine(root, "progress.html");
var dataFile = Path.Combine(root, "progress.data.js");
var progressFile = Path.Combine(root, "progress.json");
var saved = ReadProgress(out var progressError);
var states = Resolve();
var nextCode = core.Where(e => states[e.Id].State != "done").Select(e => Code(e)).FirstOrDefault();
var nextLine = nextCode is null ? "Every core exercise is done." : $"Up next: {nextCode}.";

if (args.Contains("--status"))
{
    // For the coding agent: what the code shows, what progress.json says, and each guide's numbered steps.
    var report = new JsonObject
    {
        ["next"] = nextCode,
        ["branch"] = CurrentBranch(root),
        ["progressFile"] = Path.GetRelativePath(root, progressFile),
        ["progressError"] = progressError,
        // Set when the learner is on an end-state branch, which means they jumped ahead: see SKILL.md.
        ["endBranch"] = course.FirstOrDefault(e => e.Kind == "core" && e.Branch is not null && e.Branch == CurrentBranch(root)) is { } eb
            ? new JsonObject { ["branch"] = eb.Branch, ["coversUpTo"] = Code(eb) } : null,
        ["exercises"] = new JsonArray(course.Select(e =>
        {
            var g = Guide.Parse(File.ReadAllText(Path.Combine(root, e.Guide)));
            var st = states[e.Id];
            return (JsonNode)new JsonObject
            {
                ["id"] = e.Id, ["code"] = g.Code, ["title"] = g.Title, ["kind"] = e.Kind, ["guide"] = e.Guide,
                ["state"] = st.State, ["decidedBy"] = st.Source, ["codeEvidence"] = st.CodeEvidence,
                ["steps"] = new JsonArray(g.StepTitles.Select((t, i) => (JsonNode)new JsonObject { ["n"] = i + 1, ["title"] = t }).ToArray()),
                ["saved"] = saved?["exercises"]?[e.Id]?.DeepClone(),
            };
        }).ToArray()),
    };
    Console.WriteLine(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return progressError is null ? 0 : 1;
}

File.WriteAllText(dataFile, BuildJs());
Console.WriteLine(nextLine);
Console.WriteLine($"Updated {Path.GetRelativePath(Directory.GetCurrentDirectory(), dataFile)}. Open {Path.GetRelativePath(Directory.GetCurrentDirectory(), page)} in a browser, or hard refresh it if it's already open.");
if (progressError is not null)
{
    // The page shows the problem too, so the data file is still written. The exit code tells the agent.
    Console.Error.WriteLine(progressError);
    return 1;
}
return 0;

string Code(Entry e) => Guide.Parse(File.ReadAllText(Path.Combine(root, e.Guide))).Code;

// Where each exercise stands. The coding agent's verdict in progress.json wins, because it checks the work
// against the guide. Without one, the code decides, conservatively: an exercise's files only show it was
// started, and it counts as done once a later core exercise has started, since the work builds in order.
// That rule also survives Demo 01, which deletes ChatAgent.cs and the files P3 to P4 added.
Dictionary<string, (string State, string Source, string CodeEvidence)> Resolve()
{
    var found = course.ToDictionary(e => e.Id, e => e.Detect(root));
    var furthest = core.FindLastIndex(e => found[e.Id].Found);
    var result = new Dictionary<string, (string, string, string)>();
    foreach (var e in course)
    {
        var i = core.IndexOf(e);
        var f = found[e.Id];
        var (codeState, codeEvidence) =
            i >= 0 && i < furthest ? ("done", $"{(f.Found ? f.Evidence + ", and a" : "A")} later exercise is already in your code")
            : f.Found && e.Id == "p1-01" ? ("done", f.Evidence)
            : f.Found ? ("started", f.Evidence)
            : ("none", f.Evidence);
        var agentDone = saved?["exercises"]?[e.Id]?["done"];
        result[e.Id] = agentDone?.GetValueKind() switch
        {
            JsonValueKind.True => ("done", "agent", codeEvidence),
            JsonValueKind.False => ("started", "agent", codeEvidence),
            _ => (codeState, "code", codeEvidence),
        };
    }
    return result;
}

string BuildJs()
{
    var guides = course.Select(e =>
    {
        Guide.GuidePath = e.Guide;
        Guide.RepoUrl = repoUrl;
        Guide.RepoRoot = root;
        var guide = Guide.Parse(File.ReadAllText(Path.Combine(root, e.Guide)));
        var st = states[e.Id];
        return new
        {
            id = e.Id, kind = e.Kind, pillar = e.Pillar, code = guide.Code, title = guide.Title,
            goal = guide.Goal, mission = guide.MissionHtml, prereq = guide.PrereqHtml, takeaways = guide.Takeaways, flow = guide.Flow, ladder = guide.Ladder,
            steps = guide.Steps, extras = guide.Extras, fixes = guide.Fixes, test = guide.Test, testNote = guide.TestNote,
            guidePath = e.Guide,
            guideUrl = repoUrl is null ? e.Guide : $"{repoUrl}/blob/main/{e.Guide}",
            onGitHub = repoUrl is not null,
            state = st.State, decidedBy = st.Source, codeEvidence = st.CodeEvidence,
        };
    }).ToList();

    var data = new
    {
        generatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"),
        branch = CurrentBranch(root),
        next = nextCode,
        progress = saved?["exercises"]?.ToJsonString() ?? "{}",
        progressError,
        pillars = pillars.Select(p => new { id = p.Key, name = p.Value.Name, what = p.Value.What }),
        guides,
    };
    var json = JsonSerializer.Serialize(data);   // the default encoder escapes < and >, so this is safe in a script
    return $"// Written by the workshop-progress skill. Don't edit: run the skill again instead.\nwindow.WORKSHOP_PROGRESS = {json};\n";
}

// progress.json belongs to the coding agent. A broken file is reported, never overwritten.
JsonNode? ReadProgress(out string? error)
{
    error = null;
    if (!File.Exists(progressFile)) return null;
    try { return JsonNode.Parse(File.ReadAllText(progressFile)); }
    catch (JsonException ex) { error = $"progress.json isn't valid JSON ({ex.Message}). Ask your coding agent to fix it."; return null; }
}

static string? FindRepoRoot(string dir)
{
    for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "FinanceAssistant.sln"))) return d.FullName;
    return null;
}

static string? CurrentBranch(string root)
{
    var head = Path.Combine(root, ".git", "HEAD");
    if (!File.Exists(head)) return null;
    var text = File.ReadAllText(head).Trim();
    return text.StartsWith("ref: refs/heads/") ? text["ref: refs/heads/".Length..] : "detached HEAD";
}

static string? RepoWebUrl(string root)
{
    var config = Path.Combine(root, ".git", "config");
    if (!File.Exists(config)) return null;
    var m = Regex.Match(File.ReadAllText(config), @"url\s*=\s*(?:https://github\.com/|git@github\.com:)([\w.-]+/[\w.-]+?)(?:\.git)?\s*$", RegexOptions.Multiline);
    return m.Success ? $"https://github.com/{m.Groups[1].Value}" : null;
}

static Func<string, Detection> File_(string relative) => root =>
    File.Exists(Path.Combine(root, relative)) ? new(true, $"Found {relative}") : new(false, $"No {relative} yet");

static Func<string, Detection> Contains(string relative, string needle) => root =>
{
    var path = Path.Combine(root, relative);
    return File.Exists(path) && File.ReadAllText(path).Contains(needle)
        ? new(true, $"{relative} references {needle}")
        : new(false, $"{relative} doesn't reference {needle} yet");
};

// P1.01 leaves no code behind, only secrets. Read the key names, never the values.
static Detection Secrets(string root)
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AzureOpenAI__Endpoint")))
        return new(true, "AzureOpenAI__Endpoint is set in the environment");
    var baseDir = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets");
    var file = Path.Combine(baseDir, "finance-assistant-workshop", "secrets.json");
    try
    {
        if (!File.Exists(file)) return new(false, "No user-secrets for finance-assistant-workshop yet");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] needed = ["AzureOpenAI:Endpoint", "AzureOpenAI:ApiKey", "AzureOpenAI:Deployment", "AzureOpenAI:ReasoningEffort"];
        var missing = needed.Where(k => !keys.Contains(k)).ToList();
        return missing.Count == 0
            ? new(true, "All four AzureOpenAI secrets are set")
            : new(false, $"Missing secrets: {string.Join(", ", missing)}");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
    {
        return new(false, "Couldn't read your user-secrets file");
    }
}

record Detection(bool Found, string Evidence);

record Entry(string Id, string Kind, string Pillar, string Guide, string? Branch, Func<string, Detection> Probe)
{
    public Detection Detect(string root) => Probe(root);
}

// Reads the parts of a guide the page shows. The guides keep a fixed shape:
// "# CODE - Title", "## Mission", "## At a glance", "## Step N: ...", "## Troubleshooting" with "### " entries.
partial class Guide
{
    public string Code = "", Title = "", Goal = "", MissionHtml = "", PrereqHtml = "";
    public List<string> Takeaways = [];
    public List<object> Flow = [];
    public List<object> Ladder = [];
    public List<object> Steps = [];
    public List<string> Extras = [];
    public List<object> Test = [];
    public string TestNote = "";
    public List<string> StepTitles = [];
    public List<object> Fixes = [];

    public static Guide Parse(string md)
    {
        md = md.Replace("\r\n", "\n");
        GuideText = md;
        AllPaths = Regex.Matches(md, @"`((?:src|tests|docs)/[\w./-]+)`").Select(m => m.Groups[1].Value).Distinct().ToList();
        var g = new Guide();
        var h1 = Regex.Match(md, @"^# (.+?) - (.+)$", RegexOptions.Multiline);
        g.Code = h1.Groups[1].Value.Trim();
        g.Title = h1.Groups[2].Value.Trim();

        var sections = Regex.Split(md, @"^## ", RegexOptions.Multiline).Skip(1)
            .Select(s => { var nl = s.IndexOf('\n'); return (Head: s[..nl].Trim(), Body: s[(nl + 1)..]); })
            .ToList();

        foreach (var (head, body) in sections)
        {
            if (head == "Mission") { g.Goal = Inline(FirstParagraph(body)); g.MissionHtml = Markdown(MissionIntro(body)); }
            else if (head == "Prerequisites") g.PrereqHtml = Markdown(TrimRule(body));
            else if (head == "At a glance") g.ParseGlance(body);
            else if (Regex.Match(head, @"^Step (\d+): (.+)$") is { Success: true } step) { g.Steps.Add(ParseStep(int.Parse(step.Groups[1].Value), step.Groups[2].Value.Trim(), body)); g.StepTitles.Add(step.Groups[2].Value.Trim()); }
            else if (head.StartsWith("Extra") || head.StartsWith("Stretch") || head.StartsWith("Bonus:")) g.Extras.Add(Inline(head));
            else if (head == "Troubleshooting") g.ParseFixes(body);
        }
        return g;
    }

    void ParseGlance(string body)
    {
        var block = "";
        var inFence = false;
        foreach (var raw in body.Split('\n'))
        {
            if (block == "test" && raw.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }
            if (block == "test" && !inFence && raw.TrimStart().StartsWith("Look for:")) { TestNote = Inline(raw.Trim()["Look for:".Length..].Trim()); continue; }
            if (inFence)
            {
                var t = raw.TrimEnd();
                Test.Add(new { text = t, input = t.StartsWith("> ") || t.StartsWith("$ ") });
                continue;
            }
            var line = raw.Trim();
            if (line.StartsWith("**You'll leave with**")) { block = "take"; continue; }
            if (line.StartsWith("**The flow**")) { block = "flow"; continue; }
            if (line.StartsWith("**Your agent so far**")) { block = "ladder"; continue; }
            if (line.StartsWith("**Test it**")) { block = "test"; continue; }
            if (line.Length == 0) continue;

            if (block == "take" && line.StartsWith("- ")) Takeaways.Add(Inline(line[2..]));
            else if (block == "flow" && line.Contains(" → "))
                foreach (var node in line.Split(" → "))
                {
                    var parts = node.Split(" · ", 2);
                    var label = parts[0].Trim();
                    var hot = label.StartsWith("**") && label.EndsWith("**");
                    Flow.Add(new { label = Inline(label.Trim('*')), detail = parts.Length > 1 ? Inline(parts[1].Trim().Trim('*')) : "", hot });
                }
            else if (block == "ladder" && line.StartsWith("- "))
            {
                var text = line[2..].Trim();
                var mark = text[0] switch { '✓' => 2, '◐' => 1, _ => 0 };
                Ladder.Add(new { text = Inline(text[1..].Trim()), mark });
            }
        }
    }

    static object ParseStep(int n, string title, string body)
    {
        var blocks = Blocks(body);
        var runStep = Regex.IsMatch(title, @"^(Run|Force|Verify|Validate|Watch|Test|Try)\b");

        // Where the step happens: the file its own text names first (full path wins over a bare name),
        // the Azure portal, or the terminal. Callouts mention other files in passing, so they don't count.
        var prose = string.Join("\n", blocks.Where(x => x.Kind is "para" or "list").Select(x => x.Kind == "para" ? x.Text : string.Join("\n", x.Items)));
        var files = Regex.Matches(prose, @"`([\w./-]+\.(?:cs|csproj|md|json|js|css|html|sln|yml))`")
            .Select(m => m.Groups[1].Value).Distinct()
            .GroupBy(f => Path.GetFileName(f)).Select(g => g.OrderByDescending(f => f.Length).First()).ToList();
        // A bare file name ("Program.cs") gets the full path when the step, or the guide, spells it out anywhere.
        files = files.Select(f => f.Contains('/') ? f : FullPath(f, body)).ToList();
        var where = files.FirstOrDefault()
            ?? (Regex.IsMatch(body, @"\b(Azure portal|Foundry portal|portal\.azure\.com|ai\.azure\.com)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(body, @"\bFoundry\b") ? "Azure portal"
            : runStep || blocks.Any(x => x.Kind == "code" && x.Lang is "bash" or "sh" or "powershell") ? "terminal" : "");

        // GitHub's heading anchor: lower case, punctuation dropped, spaces to hyphens.
        var anchor = Regex.Replace(Regex.Replace($"step {n}: {title}".ToLowerInvariant(), @"[^\w\s-]", ""), @"\s", "-");

        // The whole step, exactly as the guide has it. Extracts kept dropping the sentence that mattered.
        return new { title = Inline(title), where, anchor, run = runStep, html = Markdown(body) };
    }

    // A small Markdown renderer for the subset the guides use: headings, paragraphs, nested lists,
    // fenced code, block quotes, tables and rules, plus Inline() for spans.
    static string Markdown(string md)
    {
        var sb = new StringBuilder();
        Render(md.Replace("\r\n", "\n").Split('\n'), sb);
        return sb.ToString();
    }

    // Sections end with a "---" rule before the next heading. The page draws its own separators.
    // Resolves a bare file name to its path: from the guide text if it spells the path out, from a folder
    // the step names, or from the working tree, picking the folder the guide mentions most.
    static string FullPath(string name, string body)
    {
        if (Regex.Match(body, @"[\w./-]*/" + Regex.Escape(name) + @"\b") is { Success: true } inBody) return inBody.Value;
        if (AllPaths.FirstOrDefault(x => x.EndsWith("/" + name)) is { } inGuide) return inGuide;
        if (Regex.Match(body, @"`((?:src|tests)/[\w./-]+/)`") is { Success: true } dir) return dir.Groups[1].Value + name;
        if (RepoRoot.Length == 0) return name;
        var candidates = new[] { "src", "tests" }.Select(d => Path.Combine(RepoRoot, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, name, SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(RepoRoot, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")).ToList();
        if (candidates.Count == 1) return candidates[0];
        var best = candidates.Select(c => (c, n: Regex.Matches(GuideText, Regex.Escape(c[..c.LastIndexOf('/')]) + @"\b").Count))
            .OrderByDescending(x => x.n).FirstOrDefault();
        return best.c is not null && best.n > 0 ? best.c : name;
    }

    // The Mission up to its "Learning Objectives" list, which the takeaways already cover.
    static string MissionIntro(string body)
    {
        var cut = Regex.Match(body, @"^\*\*Learning Objectives", RegexOptions.Multiline);
        return TrimRule(cut.Success ? body[..cut.Index] : body);
    }

    static string TrimRule(string body) => Regex.Replace(body.TrimEnd(), @"\n-{3,}\s*$", "");

    static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    static string Dedent(string line, int n) => line[Math.Min(n, Indent(line))..];

    static void Render(string[] lines, StringBuilder sb)
    {
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0) { i++; continue; }

            if (t.StartsWith("```"))
            {
                var ind = Indent(line);
                var lang = t[3..].Trim();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) code.Add(Dedent(lines[i], ind));
                i++;
                sb.Append($"<pre{(lang.Length > 0 ? $" data-lang=\"{System.Net.WebUtility.HtmlEncode(lang)}\"" : "")}><code>{System.Net.WebUtility.HtmlEncode(string.Join('\n', code))}</code></pre>");
                continue;
            }
            if (Regex.Match(t, @"^(#{2,6})\s+(.*)$") is { Success: true } h)
            {
                var level = Math.Min(h.Groups[1].Value.Length + 1, 6);
                sb.Append($"<h{level}>{Inline(h.Groups[2].Value)}</h{level}>");
                i++;
                continue;
            }
            if (t is "---" or "***") { sb.Append("<hr>"); i++; continue; }
            if (t.StartsWith('>'))
            {
                var inner = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++)
                    inner.Add(Regex.Replace(lines[i].Trim(), @"^>\s?", ""));
                sb.Append("<blockquote>");
                Render(inner.ToArray(), sb);
                sb.Append("</blockquote>");
                continue;
            }
            if (t.StartsWith('|'))
            {
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                    rows.Add(lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray());
                var body = rows.Where(r => !r.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))).ToList();
                sb.Append("<div class=\"tbl\"><table>");
                for (var r = 0; r < body.Count; r++)
                {
                    var cell = r == 0 ? "th" : "td";
                    sb.Append("<tr>").Append(string.Concat(body[r].Select(c => $"<{cell}>{Inline(c)}</{cell}>"))).Append("</tr>");
                }
                sb.Append("</table></div>");
                continue;
            }
            if (Regex.Match(line, @"^(\s*)([-*]|\d+\.)\s+(.*)$") is { Success: true } li)
            {
                var ordered = char.IsDigit(li.Groups[2].Value[0]);
                var baseIndent = li.Groups[1].Value.Length;
                var start = ordered ? int.Parse(li.Groups[2].Value.TrimEnd('.')) : 1;
                sb.Append(ordered ? (start != 1 ? $"<ol start=\"{start}\">" : "<ol>") : "<ul>");
                while (i < lines.Length && Regex.Match(lines[i], @"^(\s*)([-*]|\d+\.)\s+(.*)$") is { Success: true } m
                       && m.Groups[1].Value.Length == baseIndent && char.IsDigit(m.Groups[2].Value[0]) == ordered)
                {
                    var contentIndent = baseIndent + m.Groups[2].Value.Length + 1;
                    var item = new List<string> { m.Groups[3].Value };
                    for (i++; i < lines.Length; i++)
                    {
                        var l = lines[i];
                        if (l.Trim().Length == 0)
                        {
                            // A blank line ends the item unless the next line is still indented under it.
                            var nx = Array.FindIndex(lines, i, x => x.Trim().Length > 0);
                            if (nx < 0 || Indent(lines[nx]) < contentIndent) break;
                            item.Add("");
                            continue;
                        }
                        if (Indent(l) < contentIndent && Regex.IsMatch(l, @"^\s*([-*]|\d+\.)\s+") && Indent(l) <= baseIndent) break;
                        if (Indent(l) <= baseIndent && !Regex.IsMatch(l, @"^\s*([-*]|\d+\.)\s+") && item[^1].Length == 0) break;
                        item.Add(Dedent(l, contentIndent));
                    }
                    var inner = new StringBuilder();
                    var simple = item.Count == 1;
                    if (simple) inner.Append(Inline(item[0]));
                    else Render(item.ToArray(), inner);
                    // A single paragraph renders without <p>, so short lists stay tight.
                    var html = inner.ToString();
                    if (!simple && html.StartsWith("<p>") && html.IndexOf("<p>", 3, StringComparison.Ordinal) < 0 && html.EndsWith("</p>") && !html.Contains("<pre") && !html.Contains("<ul") && !html.Contains("<ol"))
                        html = html[3..^4];
                    sb.Append("<li>").Append(html).Append("</li>");
                    while (i < lines.Length && lines[i].Trim().Length == 0) i++;
                }
                sb.Append(ordered ? "</ol>" : "</ul>");
                continue;
            }
            var para = new List<string> { t };
            for (i++; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (l.Length == 0 || l.StartsWith("```") || l.StartsWith('#') || l.StartsWith('>') || l.StartsWith('|') || Regex.IsMatch(l, @"^([-*]|\d+\.)\s+")) break;
                para.Add(l);
            }
            sb.Append("<p>").Append(Inline(string.Join(' ', para))).Append("</p>");
        }
    }

    void ParseFixes(string body)
    {
        foreach (var entry in Regex.Split(body, @"^### ", RegexOptions.Multiline).Skip(1))
        {
            var nl = entry.IndexOf('\n');
            var symptom = entry[..nl].Trim();
            // The whole entry, as the guide has it. A picked sentence kept dropping the actual fix.
            Fixes.Add(new { symptom = Inline(symptom), html = Markdown(TrimRule(entry[(nl + 1)..])) });
        }
    }

    record Block(string Kind, string Text, string Lang, List<string> Items);

    // Splits a section into paragraphs, lists, code blocks and quotes, ignoring ### headings.
    static List<Block> Blocks(string body)
    {
        var result = new List<Block>();
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || t == "---") continue;
            if (t.StartsWith("```"))
            {
                var lang = t[3..].Trim();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i]);
                result.Add(new Block("code", string.Join('\n', code), lang, []));
                continue;
            }
            if (t.StartsWith('>') || t.StartsWith('|'))
            {
                var marker = t[0];
                var text = new List<string> { t.TrimStart('>').Trim() };
                for (; i + 1 < lines.Length && lines[i + 1].Trim().StartsWith(marker); i++) text.Add(lines[i + 1].Trim().TrimStart('>').Trim());
                result.Add(new Block(marker == '>' ? "quote" : "table", marker == '>' ? string.Join(' ', text).Trim() : "", "", []));
                continue;
            }
            if (Regex.IsMatch(t, @"^(- |\d+\. )"))
            {
                var items = new List<string>();
                for (; i < lines.Length; i++)
                {
                    var m = Regex.Match(lines[i], @"^\s*(?:- |\d+\. )(.+)$");
                    if (m.Success) items.Add(m.Groups[1].Value.Trim());
                    else if (lines[i].Trim().Length > 0 && items.Count > 0 && char.IsWhiteSpace(lines[i][0])) items[^1] += " " + lines[i].Trim();
                    else break;
                }
                i--;
                result.Add(new Block("list", "", "", items));
                continue;
            }
            var para = new List<string> { t };
            for (; i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !Regex.IsMatch(lines[i + 1].Trim(), @"^(```|#|>|\||- |\d+\. )"); i++) para.Add(lines[i + 1].Trim());
            result.Add(new Block("para", string.Join(' ', para), "", []));
        }
        return result;
    }



    // Items of the first top-level list outside code fences, each cut to its first sentence.
    static List<string> FirstList(string body)
    {
        var items = new List<string>();
        var inFence = false;
        foreach (var raw in body.Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```")) { inFence = !inFence; continue; }
            if (inFence) continue;
            var m = Regex.Match(raw, @"^(?:- |\d+\. )(.+)$");
            if (m.Success) items.Add(FirstSentences(m.Groups[1].Value.Trim(), 1));
            else if (items.Count > 0 && raw.Trim().Length == 0) break;
        }
        return items;
    }

    // Cuts after the nth sentence end, ignoring full stops inside `code`.
    static string FirstSentences(string text, int n)
    {
        var inCode = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`') inCode = !inCode;
            else if (!inCode && ".!?".Contains(text[i]) && (i + 1 == text.Length || text[i + 1] == ' ') && --n == 0)
                return text[..(i + 1)];
        }
        return text;
    }

    // The first plain paragraph: skips code fences, tables, block quotes and lists.
    static string FirstParagraph(string body)
    {
        var inFence = false;
        var buffer = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```")) { inFence = !inFence; if (buffer.Count > 0) break; continue; }
            if (inFence) continue;
            if (line.Length == 0) { if (buffer.Count > 0) break; continue; }
            if (line.StartsWith('|') || line.StartsWith('>') || line.StartsWith("- ") || line.StartsWith("#") || Regex.IsMatch(line, @"^\d+\. "))
            { if (buffer.Count > 0) break; continue; }
            buffer.Add(line);
        }
        return string.Join(' ', buffer);
    }

    // Just enough Markdown for one-liners: code, bold, italics and links. Everything else is escaped.
    // The guide being parsed, and the repo's GitHub address, so relative links in a guide still work from
    // progress.html at the repo root: on GitHub when there is a GitHub remote, as a repo path otherwise.
    public static string GuidePath = "";
    static List<string> AllPaths = [];
    static string GuideText = "";
    public static string RepoRoot = "";
    public static string? RepoUrl;

    static string Link(string target)
    {
        string resolved;
        if (Regex.IsMatch(target, @"^[a-z]+:")) resolved = target;
        else
        {
            var hash = target.IndexOf('#');
            var path = hash < 0 ? target : target[..hash];
            var frag = hash < 0 ? "" : target[hash..];
            var full = path.Length == 0 ? GuidePath
                : new Uri(new Uri("https://repo/" + GuidePath), path).AbsolutePath.TrimStart('/');
            resolved = RepoUrl is null ? full + frag : $"{RepoUrl}/blob/main/{full}{frag}";
        }
        return System.Net.WebUtility.HtmlEncode(resolved);
    }

    static string Inline(string text)
    {
        var codes = new List<string>();
        var s = CodeSpan().Replace(text, m => { codes.Add(m.Groups[1].Value); return $"\u0001{codes.Count - 1}\u0001"; });
        s = System.Net.WebUtility.HtmlEncode(s);
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)\)", m =>
            $"<a href=\"{Link(System.Net.WebUtility.HtmlDecode(m.Groups[2].Value))}\" target=\"_blank\" rel=\"noopener\">{m.Groups[1].Value}</a>");
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<b>$1</b>");
        s = Regex.Replace(s, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "<i>$1</i>");
        return Regex.Replace(s, "\u0001(\\d+)\u0001", m => $"<code>{System.Net.WebUtility.HtmlEncode(codes[int.Parse(m.Groups[1].Value)])}</code>");
    }

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex CodeSpan();
}
