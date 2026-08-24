# P1.01 - Configure Azure AI Services

> Pillar 1, Part 1. Individual.

## Mission

Create an Azure OpenAI resource, deploy the chat model, test it in Azure AI Foundry, and save its credentials in local user secrets.

Your `gpt-5.6-luna` deployment will reply to "hello" in the playground. `dotnet user-secrets list` will show the three values P1.02 needs.

**Learning Objectives**:

- Create a Foundry deployment
- Store local secrets with .NET user secrets
- Test a model before you write code against it

---

## Prerequisites

- An Azure account with permission to create resources
- .NET SDK `10.0.400` or newer. `global.json` pins the feature band, so `10.0.1xx` and `10.0.2xx` won't do. Check with `dotnet --version` **from inside the repo**. If that fails with "A compatible .NET SDK was not found", your SDK is too old. Your install isn't broken.
- Repo cloned and `dotnet build` green from the repo root

---

## What we're solving

The starter REPL only echoes what you type. P1.02 will call Azure. First, you need an Azure deployment that works and credentials the code can read.

Two things have to be in place before P1.02 makes sense:

1. **An Azure OpenAI resource exists in your subscription** with a chat model deployed. (Pillar 2 will add an embeddings model later, when the workshop actually needs vectors.)
2. **Your endpoint, key, and deployment name live in `dotnet user-secrets`** so when P1.02's code reads them, they're there.

You'll validate the resource by sending "hello" through Azure AI Foundry's chat playground. If the playground replies, your deployment works. If your local code can't reach it later, that's a wiring problem, not a cloud problem.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Create an Azure OpenAI resource (Standard).
2. Deploy `gpt-5.6-luna` in Azure AI Foundry.
3. Validate the deployment from Foundry's chat playground. Send "hello", get a reply.
4. Grab the endpoint URL and an API key from "Keys and Endpoint".
5. Run three `dotnet user-secrets set` commands inside `src/FinanceAssistant/`, then smoke-test them with one `curl`.

---

## Step 1: Create the Azure OpenAI resource

### 1.1: Open the portal

Go to https://portal.azure.com and sign in. Click "Create a resource" (the + icon, top-left).

### 1.2: Find Microsoft Foundry

Search for "Microsoft Foundry" in the marketplace. Pick "Microsoft Foundry" from the results. Click "Create".

> Azure renames things often. If you can't find "Microsoft Foundry", look for "Azure OpenAI" or "Azure AI Foundry". Same product.

### 1.3: Fill in the basics

- **Subscription**: whichever you have access to.
- **Resource group**: create a new one called `finance-assistant-workshop`. Keeps cleanup easy after the workshop.
- **Region**: Sweden Central, East US, or West Europe all work as of mid-2026. Pick East US if you're not sure.
- **Name**: anything unique. `finance-assistant-<your-initials>` is fine.

Click "Review + create", then "Create". Click "Go to resource" once the deployment finishes.

---

## Step 2: Deploy the chat model

> Azure portal and Foundry rearrange labels and navigation regularly. The instructions below describe the intent. Adapt to whatever the current UI calls the equivalent action.

### 2.1: Open Azure AI Foundry Portal

In your resource's overview page, click "Go to Foundry Portal". In the Portal's left navigation, find "Models + endpoints".

### 2.2: Deploy gpt-5.6-luna

**Important:** The instructions below are for Microsoft Foundry before the _"new Microsoft Foundry experience"_. If your portal opens that way, turn the new experience off.

In "Models + endpoints", click "Deploy model > Deploy base model". Pick **`gpt-5.6-luna`** from the list.

> The model the *agent* runs on is `gpt-5.6-luna`. Not `gpt-5.6-sol`, not `gpt-5.6-terra`. Pick `gpt-5.6-luna` exactly.
>
> All three are in the list and all three are generally available in Foundry, so this is an easy one to get wrong at speed.
>
> You will come back for `gpt-5.6-terra` in P5.02, where the eval judge gets its own deployment so that no model ends up grading its own output. Deploy it then, not now. One deployment is all P1.02 needs.
>
> If you don't see it, your region doesn't support it. Go back to Step 1.3 and pick a different region.

In the deployment dialog:

- **Deployment name**: `gpt-5.6-luna`. Use exactly this name. It's the value you'll paste into user secrets in Step 5.
- **Deployment type**: Global Standard. (Foundry may also offer `Standard` and `DataZoneStandard`. They have different cost and availability tradeoffs, but Global Standard is the safe pick for this workshop.)
- Leave the rest at defaults.

Click "Deploy". Wait for the green check. Do not click "Deploy" a second time while the status is still "Creating".

> The deployment name and the model name don't have to match. We're keeping them identical to remove a moving part. If you rename the deployment, that renamed value is what goes into your secrets.

### Why this model

You didn't pick it, so here's the reasoning. It matters, because choosing a model is an engineering decision and not a contest to find the biggest one.

The GPT-5.6 family has three members. Sol is the reasoning model, built for extended thinking and agentic work. Terra is the balanced everyday model. Luna is the fastest and cheapest of the three, and OpenAI describes it as the model for cost-sensitive, high-volume workloads.

An agent is a high-volume workload. Every turn replays the whole conversation, and Pillar 4 is two exercises about exactly that bill. So the cheapest member of a current family is the right default, not a compromise.

Luna's numbers, per million tokens: **$0.20** input, **$0.02** cached input, **$1.20** output.

Cached input at a tenth of the input price is the one to remember. It's why the shape of your prompt (what stays constant, what changes) turns into money, and we come back to it in Pillar 4.

---

## Step 3: Validate from the chat playground

This is your smoke test. No code yet. If the playground works, your deployment works.

### 3.1: Open the playground

In Foundry's left navigation, click "Playgrounds", then "Chat".

### 3.2: Pick your deployment

At the top of the playground, in the "Deployment" dropdown, pick `gpt-5.6-luna`. (If you used a different deployment name in Step 2.2, pick that one.)

### 3.3: Send "hello"

In the chat box at the bottom, type:

```
hello
```

Press Enter. You should get a reply. Something like "Hi! How can I help you today?".

If you do, the cloud side works. Anything else, see the troubleshooting section.

---

## Step 4: Grab the endpoint and the key

Switch back to the Azure portal tab (not Foundry). On your resource's overview page, click "Keys and Endpoint" in the left navigation.

Copy two things:

- **KEY 1**. Either key works, so KEY 1 is fine.
- **Endpoint (OpenAI)**. A URL like `https://your-resource.openai.azure.com/`. This pane may list more than one endpoint. You want the one whose host ends in `.openai.azure.com`.

> Copy the endpoint exactly as the portal shows it, trailing slash included. P1.02 normalises it with `UriBuilder` before calling anything, so a missing slash won't actually break the request, but matching what the portal shows keeps your secret comparable to your neighbour's when you're debugging side by side.

---

## Step 5: Wire your secrets locally

### 5.1: Move into the project folder

```bash
cd src/FinanceAssistant
```

User secrets are scoped to a project. The agent project's `UserSecretsId` is `finance-assistant-workshop`, declared in `FinanceAssistant.csproj`. Run the commands below from anywhere else and `dotnet user-secrets` refuses rather than guesses. From the repo root it can't find a project file (there's a `.sln` there, not a `.csproj`). From `src/FinanceAssistant.McpServer/` it tells you that project has no `UserSecretsId`. Both errors are loud. Read them, `cd src/FinanceAssistant/`, and try again.

### 5.2: Set three secrets

Replace placeholders with the values you copied in Step 4:

```bash
dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://your-resource.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:ApiKey"     "<KEY 1 you copied>"
dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-5.6-luna"
```

`dotnet user-secrets` writes to a JSON file in your user profile. It's never in the repo. It can't be committed by accident.

> The instinct here is to drop the key into `appsettings.json` because it's faster. Don't. The first time you commit `appsettings.json` with a real key, GitHub's secret scanner notices, the key is auto-revoked, and you're explaining the rotation to your team. User secrets is the same convenience without the drama.

### 5.3: Verify

Check the names first. This confirms all three landed without echoing your API key, which matters if you're sharing your screen.

On macOS/Linux:

```bash
dotnet user-secrets list | awk -F' = ' '{print $1}'
```

On Windows (PowerShell):

```powershell
dotnet user-secrets list | ForEach-Object { ($_ -split ' = ')[0] }
```

`dotnet user-secrets list` prints secrets in the order you set them rather than alphabetically, so working through Step 5.2 top to bottom gives you:

```
AzureOpenAI:Endpoint
AzureOpenAI:ApiKey
AzureOpenAI:Deployment
```

Three `AzureOpenAI:` lines is the thing to check. The order doesn't matter.

Drop the pipe when you want the values too:

```bash
dotnet user-secrets list
```

```
AzureOpenAI:Endpoint = https://your-resource.openai.azure.com/
AzureOpenAI:ApiKey = <your-key-value>
AzureOpenAI:Deployment = gpt-5.6-luna
```

They print in plaintext because the store is plaintext. User secrets keeps the key out of the repo. It doesn't encrypt it.

### 5.4: Smoke-test the values

Step 3 proved the deployment works. It didn't prove these three strings are right, because the playground authenticated as your portal session and never touched KEY 1.

One command closes that gap. Substitute your endpoint and key:

```bash
curl -sS https://your-resource.openai.azure.com/openai/v1/chat/completions \
  -H "Authorization: Bearer <KEY 1 you copied>" \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-5.6-luna","messages":[{"role":"user","content":"hello"}]}'
```

> On Windows, run this from PowerShell as `curl.exe`, not `curl`. Bare `curl` is an alias for `Invoke-WebRequest`, which takes different arguments and will fail on the `-H` flags.

JSON back with a `content` field means all three values are good, and anything that breaks in P1.02 is code you wrote.

- **`401`**: the key is wrong or got truncated on the way over. Re-copy KEY 1.
- **`404`**: the deployment name is wrong. Compare it against Foundry's "Models + endpoints", character for character.

This is the same URL path and the same `Authorization: Bearer` scheme the SDK uses in P1.02, so a reply here means the cloud side is genuinely finished.

---

## Troubleshooting

### Foundry playground doesn't return a reply

Your deployment isn't ready, or your region doesn't support the model. Check the deployment status in Foundry's "Models + endpoints" view. Status should be "Succeeded".

### Can't find "Microsoft Foundry" in the portal

Try "Azure OpenAI" or "Azure AI Foundry". Microsoft has renamed the service multiple times.

### `No secrets configured for this application.`

Your `set` commands errored and the message scrolled past. Run from anywhere other than `src/FinanceAssistant/` and `dotnet user-secrets` fails instead of writing: no project file at the repo root, no `UserSecretsId` in `src/FinanceAssistant.McpServer/`. `cd src/FinanceAssistant/`, re-run the three `set` commands, and watch for errors this time.

### Deployment fails with a quota error

Fresh subscriptions sometimes ship with zero TPM (tokens-per-minute) quota for `gpt-5.6-luna` in your chosen region. Either request quota in the portal under "Quotas" → "Cognitive Services - Azure OpenAI", or pick a different region from Step 1.3.

### Anything else

Raise your hand. The instructor has seen this fail in unusual ways.

---

## Summary

You've set up:

- **Azure OpenAI resource**: created in Azure portal, region picked.
- **Chat deployment**: `gpt-5.6-luna` deployed and verified through the Foundry playground.
- **Local user secrets**: three keys stored outside the repo.
- **A verified local path**: the same endpoint, key and deployment name your code will use, proven from your own machine with one `curl`.

---

## What's next

P1.02 registers `IChatClient` in a `ServiceCollection` and replaces the echo line in `Program.cs` with a real call to your `gpt-5.6-luna` deployment. Two optional Extras follow it: streaming the reply, and turning the reasoning dial up to feel what the default bought you.

---

## Cleanup (after the workshop)

Delete the `finance-assistant-workshop` resource group from the Azure portal. That tears down the Azure OpenAI resource and the deployment together and stops any further charges. Pillar 2 will add an embeddings deployment to the same group, so wait until you're done with the whole workshop before deleting.

---

## Additional Resources

- [Microsoft.Extensions.AI documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Azure OpenAI .NET SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.openai-readme)
- [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)
