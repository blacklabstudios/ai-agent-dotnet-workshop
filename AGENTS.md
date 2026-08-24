# AGENTS.md

This is the companion repo for the **Build Your First AI Agent in .NET** workshop. A learner builds a personal finance
agent step by step, following the guides in `docs/`. You are most likely helping one of those learners.

## How to help a learner

- The learner is here to learn by building. Explain, point at the right part of the guide, review their code, and help
  them debug. Write the exercise code for them only when they ask you to.
- The guide is the source of truth for each exercise. Work from the guide's steps, in order, and use its names.
  When the learner's code departs from the guide on purpose, follow their code.
- When the learner finishes a step or an exercise, asks where they are or what's next, or asks for their progress page,
  use the `workshop-progress` skill. It records verified progress and notes in `progress.json` and refreshes the
  learner's `progress.html` page.

## Layout

- `src/FinanceAssistant/`: the agent, a .NET 10 console app. It grows across all six pillars.
- `src/FinanceAssistant.McpServer/`: the MCP server, filled in during P6.01.
- `tests/FinanceAssistant.Evals/`: the eval project. It doesn't exist until P5.02 creates it.
- `docs/exercises/`: the core exercises, P1.01 to P6.01, done in order.
- `docs/bonus/`: optional exercises. `docs/demos/`: facilitator-driven demos the room watches.
- Each guide opens with `## Mission` and `## At a glance`, then `## Step N: ...` sections and `## Troubleshooting`.
- End-state branches: `p1-02-end` to `p6-01-end`, plus `bonus-all`, `demo-p5-otel` and `demo-p6-maf`.

## Commands

Run from the repo root.

```bash
docker compose up -d                          # Postgres with pgvector, needed from P1.02 on
dotnet build                                  # the whole solution
dotnet run --project src/FinanceAssistant     # the agent's REPL
```

- `dotnet run` and `dotnet test` call live models from P1.02 and P5.02 on. That costs the learner money, so ask
  before you run either.
- Secrets live in `dotnet user-secrets` under the id `finance-assistant-workshop`. Never print, log, copy or commit a
  secret value. To check what's set, list key names only.
- The startup check that runs before the REPL reports setup problems with `[!!]` and `[??]`. Read it before debugging
  anything else.

## Editing the guides

For maintainers. The `workshop-progress` skill parses the guides, so keep their shape: the `# CODE - Title` heading,
`## Mission`, the `## At a glance` blocks (**You'll leave with**, **The flow**, **Test it**, **Your agent so far**),
`## Step N: Title` headings and `### ` entries under `## Troubleshooting`. Don't use em dashes or en dashes in the guides.
