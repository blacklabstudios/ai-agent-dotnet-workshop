# Build Your First AI Agent in .NET

This is the companion repository for the **Build Your First AI Agent in .NET** workshop: a hands-on program where .NET developers learn the building blocks of agentic AI by shipping a real agent end-to-end.

A personal finance assistant agent built with .NET 10. Throughout the workshop, you'll extend this foundation with AI-powered features: tool-calling, semantic search over your transactions with pgvector (RAG), conversation memory, a human-in-the-loop safety gate, and exposing the agent to other clients over the Model Context Protocol.

**Built by [Gui Ferreira](https://github.com/gsferreira)** at [Black Lab Studios](https://blacklabstudios.com): Microsoft MVP, Software Engineer Educator, and Dometrain author.

> **Note**: This repository contains example credentials (database passwords, keys) for demo purposes only. These are intentionally simple values for local development. Never use these in production.

## Prerequisites

- Comfortable writing C# / .NET. We assume you can read and modify an ASP.NET / console app without hand-holding.
- An Azure account. The first exercise provisions an Azure OpenAI (Microsoft Foundry) resource. Walkthrough: [`docs/exercises/p1-01-configure-azure.md`](docs/exercises/p1-01-configure-azure.md).
  - The model you use depends on your subscription's quota tier. **Tier 1 or above** runs `gpt-5.6-luna` as the agent and `gpt-5.6-sol` as the eval judge. **Free Tier**, which is where many new pay-as-you-go subscriptions start, runs `gpt-5-mini` for both. Both paths run every exercise. P1.01 shows you how to tell which one you're on.
  - No Azure? P1.01 has a bonus path that uses the OpenAI API with the same GPT-5.6 models instead. It needs paid OpenAI credit.

## Required Tools

- **.NET 10 SDK**: at least `10.0.400` (pinned via `global.json`). Verify with `dotnet --version`.
- **xUnit v3 templates**: P5.02 scaffolds its test project with `dotnet new xunit3`, which doesn't ship with the SDK. Install it before the day with `dotnet new install xunit.v3.templates::3.0.1`.
- **Docker**: Docker Desktop, OrbStack, Rancher Desktop, or any other flavour. Verify with `docker compose version`.
- **Git**: to clone this repo and check out per-pillar checkpoints.
- **An IDE**: Visual Studio, JetBrains Rider, or VS Code with the C# Dev Kit. Open `FinanceAssistant.sln`.
- **A code assistant**: Claude Code, GitHub Copilot, Cursor, or your tool of choice. You'll lean on it during the exercises, so make sure it's installed, signed in, and working before you start.

## Getting started

1. Clone this repo.
2. From the repo root, bring up the database container: `docker compose up -d`. Verify with `docker compose ps`: you should see `finance-assistant-postgres` running and healthy. The container must be running before `dotnet run`.
3. From the repo root, `dotnet build` (or open `FinanceAssistant.sln` in your IDE). You should see `Build succeeded.` with zero warnings.
4. Optional smoke test: `dotnet run --project src/FinanceAssistant`. The REPL should start and echo whatever you type. Type `exit` to quit.

> **Where's the connection string?** It's hard-coded in [`src/FinanceAssistant/Data/FinanceDbContext.cs`](src/FinanceAssistant/Data/FinanceDbContext.cs), in the `DefaultConnectionString` constant:
>
> ```
> Host=localhost;Port=5432;Database=financeassistant;Username=postgres;Password=postgres
> ```
>
> It matches the credentials in [`docker-compose.yml`](docker-compose.yml). Change the port, database, user, or password in one place and you have to change it in the other.

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
git stash                  # set your in-progress work aside
git checkout p2-02-end     # jump to the end of, e.g., exercise P2.02
```
