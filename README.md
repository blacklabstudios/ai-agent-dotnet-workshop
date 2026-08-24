# Build Your First AI Agent in .NET

This is the companion repository for the **Build Your First AI Agent in .NET** workshop: a hands-on program where .NET developers learn the building blocks of agentic AI by shipping a real agent end-to-end.

A personal finance assistant agent built with .NET 10. Throughout the workshop, you'll extend this foundation with AI-powered features: tool-calling, semantic search over your transactions with pgvector (RAG), conversation memory, a human-in-the-loop safety gate, and exposing the agent to other clients over the Model Context Protocol.

**Built by [Gui Ferreira](https://github.com/gsferreira)** at [Black Lab Studios](https://blacklabstudios.com): Microsoft MVP, Software Engineer Educator, and Dometrain author.

> **Note**: This repository contains example credentials (database passwords, keys) for demo purposes only. These are intentionally simple values for local development. Never use these in production.

## Prerequisites

- Comfortable writing C# / .NET. We assume you can read and modify an ASP.NET / console app without hand-holding.
- An Azure account that can create resources. An existing personal or company account is best. A new pay-as-you-go account is fine too, and we set it up together in the room. The first exercise provisions an Azure OpenAI (Microsoft Foundry) resource. Walkthrough: [`docs/exercises/p1-01-configure-azure.md`](docs/exercises/p1-01-configure-azure.md).
  - Expect roughly 5 EUR of Foundry usage over the two days.
  - The model you use depends on your subscription's quota tier. **Tier 1 or above** runs `gpt-5.6-luna` as the agent and `gpt-5.6-sol` as the eval judge. **Free Tier**, which is where many new pay-as-you-go subscriptions start, runs `gpt-5-mini` for both. Both paths run every exercise. P1.01 shows you how to tell which one you're on.
  - No Azure? P1.01 has a bonus path that uses the OpenAI API with the same GPT-5.6 models instead. It needs paid OpenAI credit.

## Required Tools

- **.NET 10 SDK**: at least `10.0.400` (pinned via `global.json`). Verify with `dotnet --version`.
- **xUnit v3 templates**: P5.02 scaffolds its test project with `dotnet new xunit3`, which doesn't ship with the SDK. Install it before the day with `dotnet new install xunit.v3.templates@3.0.1`.
- **Docker**: Docker Desktop, OrbStack, Rancher Desktop, or any other flavour. Verify with `docker compose version`, and make sure Docker is running before you start.
- **Git**: to clone this repo and check out per-pillar checkpoints. On Windows it also installs Git Bash, which you'll need (see below).
- **An IDE**: Visual Studio, JetBrains Rider, or VS Code with the C# Dev Kit. If you use Visual Studio, you'll need Visual Studio 2026. Open `FinanceAssistant.sln`.
- **Node.js**: P6.01 runs the MCP Inspector with `npx`. Verify with `node --version`.
- **A coding agent**: Claude Code, Codex, GitHub Copilot, OpenCode, Cursor, or your tool of choice. You'll lean on it during the exercises, so make sure it's installed, signed in, and working before you start. The repo's [`AGENTS.md`](AGENTS.md) tells it how to help you, and the first four also pick up the [progress skill](#track-your-progress).

> **On Windows:** run `curl`, `git` and `docker` commands in **Git Bash**, not PowerShell. Windows PowerShell mangles the quotes in some of them. `dotnet` commands work in either.
>
> **Doing the web chat bonus (P6.B01)?** Its terminal helper pipes through `python3`, so you also need Python 3. On Windows, install it from python.org: the `python3` that ships with Windows only opens the Microsoft Store.

## Getting started

1. Clone this repo.
2. Start Docker, then from the repo root bring up the database container: `docker compose up -d`. Verify with `docker compose ps`: you should see `finance-assistant-postgres` with a status of `Up`. The container must be running before `dotnet run`.
3. From the repo root, `dotnet build` (or open `FinanceAssistant.sln` in your IDE). You should see `Build succeeded.` with zero warnings.
4. Optional smoke test: `dotnet run --project src/FinanceAssistant`. A short setup check runs first. Before P1.01 it says Azure isn't set up yet, which is expected, and that Postgres is answering. Then the REPL starts and echoes whatever you type. Type `exit` to quit.
5. Open [`progress.html`](progress.html) in your browser. Keep it open for the day. [Track your progress](#track-your-progress) says how to keep it current.

> **Where's the connection string?** It's hard-coded in [`src/FinanceAssistant/Data/FinanceDbContext.cs`](src/FinanceAssistant/Data/FinanceDbContext.cs), in the `DefaultConnectionString` constant:
>
> ```
> Host=localhost;Port=5432;Database=financeassistant;Username=postgres;Password=postgres
> ```
>
> It matches the credentials in [`docker-compose.yml`](docker-compose.yml). Change the port, database, user, or password in one place and you have to change it in the other.

> **`Conflict. The container name "/finance-assistant-postgres" is already in use`?** An older clone or a previous workshop left a container with the same name. Remove it with `docker rm -f finance-assistant-postgres`, then run `docker compose up -d` again.

## Repo layout

- [`src/FinanceAssistant/`](src/FinanceAssistant): the agent. A single .NET 10 console app holding the domain (Models, Data, Services), the REPL (Program.cs), and the system prompt (`Prompts/SystemPrompt.md`). Evolves in place across all six pillars.
- [`src/FinanceAssistant.McpServer/`](src/FinanceAssistant.McpServer): empty ASP.NET Core minimal-API placeholder. Filled in during Pillar 6.
- `tests/FinanceAssistant.Evals/`: the eval project. It doesn't exist when you clone: you create it in P5.02. P5.02 adds it to `FinanceAssistant.sln`, so from then on the root `dotnet build` compiles it and a bare `dotnet test` runs it. Every eval calls a live model, so that `dotnet test` bills tokens.
- `FinanceAssistant.sln`: solution file. Open this in Visual Studio, Rider, or VS Code. Holds the two `src/` projects until P5.02 adds the eval project under `tests/`.
- `global.json`: pins the .NET SDK to `10.0.400`.
- `scaffolding/transactions.csv`: bank-statement-style seed (498 rows, 2024-06-01 to 2026-11-10) imported on first run.
- `docker-compose.yml`: local `pgvector/pgvector:pg18` container. Used by the agent for transactions storage and (from P2.02) embeddings.
- [`docs/exercises/`](docs/exercises): the per-exercise tutorials you follow through the workshop.
- [`docs/demos/`](docs/demos): the facilitator-driven demos. One runs in Pillar 5 (OpenTelemetry and the Aspire Dashboard) and two run at the end of the workshop (Agent Framework migration, multi-agent handoff). Nobody types; the room watches.
- [`docs/bonus/`](docs/bonus): optional exercises to pick up on your own (CSV import, 50/30/20 expense analysis, a trustworthy eval suite, web chat).
- [`progress.html`](progress.html): your progress page. Open it straight from disk.
- [`AGENTS.md`](AGENTS.md) and [`.agents/skills/workshop-progress/`](.agents/skills/workshop-progress): how your coding agent should help you, and the skill that keeps `progress.html` current.

## Track your progress

1. **Open [`progress.html`](progress.html)** from the repo root in your browser. The first time, it asks you to load your progress.
2. **When you want it updated, run the `workshop-progress` skill.** In Claude Code, type `/workshop-progress`. In Codex, GitHub Copilot or OpenCode, ask your agent to run the workshop-progress skill, or just say "update my workshop progress". Then refresh the page.

The skill checks your work against the guide before it records anything. It notes each step you finish, what you actually did, and any problem you hit with its fix, in `progress.json` (gitignored). Then it refreshes the page. The page shows every exercise with its takeaways, the flow of one request, the steps, a quick test and the usual failures, all read from the guides. An exercise counts as done once your agent has checked it. Until then, its files in your working tree show it as in progress.

The skill lives in `.agents/skills/workshop-progress/`. `.claude/skills`, `.github/skills` and `.opencode/skills` link to it. On Windows, Git only checks those links out if symlinks are enabled: turn on Developer Mode, then run `git config --global core.symlinks true` before you clone. Codex reads `.agents/skills` directly, and Copilot and OpenCode accept it too, so without the links the skill still reaches most agents.

## Falling behind?

Each exercise's end-state is published as a branch in this repo. Branch names mirror the exercise filenames in [`docs/exercises/`](docs/exercises) with an `-end` suffix. P1.01 is portal setup, so it has no branch:

| Branch          | State at the end of exercise…                |
| --------------- | -------------------------------------------- |
| `p1-02-end`     | P1.02: chat client wired up                 |
| `p2-01-end`     | P2.01: currency conversion tool             |
| `p2-02-end`     | P2.02: database queries + pgvector RAG      |
| `p3-01-end`     | P3.01: hand-written agent loop              |
| `p4-01-end`     | P4.01: episodic conversation memory         |
| `p4-02-end`     | P4.02: history summarization reducer        |
| `p5-01-end`     | P5.01: human-in-the-loop safety gate        |
| `p5-02-end`     | P5.02: agent evaluations                    |
| `p6-01-end`     | P6.01: MCP server                           |

If you get stuck, fast-forward to the canonical state:

```bash
git stash -u               # set your in-progress work aside, new files included
git checkout p2-02-end     # jump to the end of, e.g., exercise P2.02
```

Then ask your coding agent to update your progress, so `progress.html` shows where you jumped to.
