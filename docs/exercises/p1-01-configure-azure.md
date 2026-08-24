# P1.01 - Configure Azure AI Services

> Pillar 1, Part 1. Individual.

## Mission

Create an Azure OpenAI resource, deploy the chat model for your path, test it in Azure AI Foundry, and save its credentials in local user secrets.

Your `chat` deployment will reply to "hello" in the playground. `dotnet user-secrets list` will show the four values P1.02 needs.

**Learning Objectives**:

- Pick the model your quota tier allows
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
2. **Your endpoint, key, deployment name and reasoning setting live in `dotnet user-secrets`** so when P1.02's code reads them, they're there.

You'll validate the resource by sending "hello" through Azure AI Foundry's chat playground. If the playground replies, your deployment works. If your local code can't reach it later, that's a wiring problem, not a cloud problem.

---

## Two paths: pick yours before you start

This workshop runs on two models. You don't choose between them. Your subscription's **quota tier** does.

| | **Path A: Tier 1 or above** | **Path B: Free Tier** |
| --- | --- | --- |
| Chat model | `gpt-5.6-luna` | `gpt-5-mini` |
| `AzureOpenAI:ReasoningEffort` | `None` | `Low` |
| Eval judge (P5.02) | `gpt-5.6-sol` | `gpt-5-mini` |

Everything after this guide is the same on both paths. Your deployments get neutral names (`chat` now, `judge` in P5.02), so no later guide needs to know which model sits behind them. The embeddings deployment in P2.02 is the one exception: it keeps its model's name, because it's the same model on both paths.

### Why two paths

Foundry assigns every subscription a quota tier: Free Tier, then Tier 1 to Tier 6. A brand-new pay-as-you-go subscription often starts on the **Free Tier**, with a quota of 0 tokens per minute for the newest models. Adding a credit card does not move it. On a subscription like that, the deploy dialog fails on quota for every GPT-5.6 model, while `gpt-5-mini` deploys fine.

Tier 1 does get them: 1,000,000 TPM each for Luna and Sol on Global Standard, in [Microsoft's Tier 1 table](https://learn.microsoft.com/en-us/azure/foundry/openai/quotas-limits). So the line is **Free Tier versus Tier 1 or above**.

You could wait for Microsoft to upgrade you, or ask for quota through the [quota request form](https://aka.ms/oai/stuquotarequest). Both take time you don't control, so don't bet a workshop morning on them. Take Path B now. Moving to Path A later means redeploying `chat` on Luna and `judge` on Sol, and changing one secret, `AzureOpenAI:ReasoningEffort`.

> This split reflects quota as of October 2026. Microsoft changes tiers and model availability often, so the models on each path may change between editions.

### Check your tier

The quickest test is to try. Deploy `gpt-5.6-luna` in Step 2. If the dialog fails on quota or shows 0 TPM available, you're on the Free Tier: take Path B.

To ask Azure directly, run this from a terminal signed in with `az login` (or from Azure Cloud Shell). Replace `<subscription-id>` with yours:

```bash
az rest --method get \
  --url "https://management.azure.com/subscriptions/<subscription-id>/providers/Microsoft.CognitiveServices/quotaTiers?api-version=2025-10-01-preview"
```

Look for `currentTierName` in the JSON. `"Free-Tier"` means Path B. `"Tier-1"` or higher (`"Tier-2"` and up) means Path A.

### If you have no Azure

There's a bonus path at the end of this guide, [Bonus: use the OpenAI API instead of Foundry](#bonus-use-the-openai-api-instead-of-foundry). Same code, with a different endpoint, key and model name in your secrets. It needs paid OpenAI credit.

---

## If you're comfortable, do this

Use this list if you want the route first. The full steps explain the choices and help you recover when a step fails.

1. Create an Azure OpenAI resource (Standard).
2. Deploy your path's model in Azure AI Foundry under the deployment name `chat`: `gpt-5.6-luna` on Path A, `gpt-5-mini` on Path B.
3. Validate the deployment from Foundry's chat playground. Send "hello", get a reply.
4. Grab the endpoint URL and an API key from "Keys and Endpoint". Append `openai/v1/` to the endpoint.
5. Run four `dotnet user-secrets set` commands inside `src/FinanceAssistant/`, then smoke-test them with one `curl`.

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

### 2.2: Deploy your path's model

**Important:** The instructions below are for Microsoft Foundry before the _"new Microsoft Foundry experience"_. If your portal opens that way, turn the new experience off.

In "Models + endpoints", click "Deploy model > Deploy base model". Pick the model for your path:

- **Path A (Tier 1 or above):** **`gpt-5.6-luna`**. Not `gpt-5.6-sol`, not `gpt-5.6-terra`. All three GPT-5.6 models sit next to each other in the list, so this is an easy one to get wrong at speed.
- **Path B (Free Tier):** **`gpt-5-mini`**. Not `gpt-5`, not `gpt-5-nano`.

> Not sure which path you're on? Try `gpt-5.6-luna` first. A quota failure means Path B. Cancel the dialog and deploy `gpt-5-mini` instead.
>
> You will come back for a second deployment, `judge`, in P5.02. Deploy it then, not now. One deployment is all P1.02 needs.
>
> If you don't see the model at all, your region doesn't offer it. Go back to Step 1.3 and pick a different region.

In the deployment dialog:

- **Deployment name**: `chat`. Use exactly this name, on either path. It's the value you'll paste into user secrets in Step 5.
- **Deployment type**: Global Standard. (Foundry may also offer `Standard` and `DataZoneStandard`. They have different cost and availability tradeoffs, but Global Standard is the safe pick for this workshop.)
- Leave the rest at defaults.

Click "Deploy". Wait for the green check. Do not click "Deploy" a second time while the status is still "Creating".

> **Why `chat` and not the model name?** The deployment name and the model name don't have to match, and here they deliberately don't. Your code asks for a deployment called `chat`. Which model answers is decided in Foundry. That's why no later guide cares which path you're on, and why moving from Path B to Path A later needs no code change.

### Why these models

You didn't pick them, so here's the reasoning. It matters, because choosing a model is an engineering decision and not a contest to find the biggest one.

**Path A, `gpt-5.6-luna`.** The GPT-5.6 family has three members. Sol is the reasoning model, built for extended thinking and agentic work. Terra is the balanced everyday model. Luna is the fastest and cheapest of the three, and OpenAI describes it as the model for cost-sensitive, high-volume workloads. Per million tokens: **$0.20** input, **$0.02** cached input, **$1.20** output.

An agent is a high-volume workload. Every turn replays the whole conversation, and Pillar 4 is two exercises about exactly that bill. So the cheapest member of a current family is the right default, not a compromise.

Sol still has a job on Path A. In P5.02 it becomes the eval judge, where you want the strongest model you can get and the bill is a CI run, not every user request. Per million tokens: **$4.00** input, **$0.40** cached input, **$20.00** output.

**Path B, `gpt-5-mini`.** A smaller, older reasoning model, and the one a Free Tier subscription can deploy today. It calls tools, follows a system prompt and returns structured output, which is everything this workshop asks of a model. It's also the judge on Path B, because it's the only model your tier gives you. Per million tokens: **$0.25** input, **$0.025** cached input, **$2.00** output. That's a little more than Luna, so being on the Free Tier doesn't make the agent cheaper.

Cached input costs a tenth of input on every model here. That's the one to remember. It's why the shape of your prompt (what stays constant, what changes) turns into money, and we come back to it in Pillar 4.

---

## Step 3: Validate from the chat playground

This is your smoke test. No code yet. If the playground works, your deployment works.

### 3.1: Open the playground

In Foundry's left navigation, click "Playgrounds", then "Chat".

### 3.2: Pick your deployment

At the top of the playground, in the "Deployment" dropdown, pick `chat`. (If you used a different deployment name in Step 2.2, pick that one.)

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

Now append `openai/v1/` to it. The value you store is the full base URL the code talks to:

```
https://your-resource.openai.azure.com/openai/v1/
```

> **Why store the whole URL?** `/openai/v1/` is Foundry's OpenAI-compatible surface, the same API shape OpenAI itself serves at `https://api.openai.com/v1/`. Store the full base and the code never has to know which of the two it's talking to. That's what makes the [OpenAI bonus path](#bonus-use-the-openai-api-instead-of-foundry) a change of secrets instead of a change of code.
>
> If your portal already shows an endpoint ending in `/openai/v1/`, use it as is. Don't append the path twice.

---

## Step 5: Wire your secrets locally

### 5.1: Move into the project folder

```bash
cd src/FinanceAssistant
```

User secrets are scoped to a project. The agent project's `UserSecretsId` is `finance-assistant-workshop`, declared in `FinanceAssistant.csproj`. Run the commands below from anywhere else and `dotnet user-secrets` refuses rather than guesses. From the repo root it can't find a project file (there's a `.sln` there, not a `.csproj`). From `src/FinanceAssistant.McpServer/` it tells you that project has no `UserSecretsId`. Both errors are loud. Read them, `cd src/FinanceAssistant/`, and try again.

### 5.2: Set four secrets

Replace placeholders with the values you copied in Step 4. The first three are the same on both paths:

```bash
dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://your-resource.openai.azure.com/openai/v1/"
dotnet user-secrets set "AzureOpenAI:ApiKey"     "<KEY 1 you copied>"
dotnet user-secrets set "AzureOpenAI:Deployment" "chat"
```

The fourth depends on your path. Run the line that matches yours:

```bash
# Path A, gpt-5.6-luna
dotnet user-secrets set "AzureOpenAI:ReasoningEffort" "None"
```

```bash
# Path B, gpt-5-mini
dotnet user-secrets set "AzureOpenAI:ReasoningEffort" "Low"
```

> **Why the reasoning setting forks.** Both models think before they answer, and P1.02 turns that thinking down as far as it goes so a `hello` comes back fast. Where the bottom sits depends on the model. Luna documents `none`, so thinking switches off entirely. The GPT-5 family documents `minimal`, `low`, `medium` and `high`, and `Low` is the lowest of those the .NET abstraction can express. `gpt-5-mini` rejects `none` (tested October 2026), so `Low` is the bottom you can reach on Path B. P1.02 explains the dial properly.

`dotnet user-secrets` writes to a JSON file in your user profile. It's never in the repo. It can't be committed by accident.

> The instinct here is to drop the key into `appsettings.json` because it's faster. Don't. The first time you commit `appsettings.json` with a real key, GitHub's secret scanner notices, the key is auto-revoked, and you're explaining the rotation to your team. User secrets is the same convenience without the drama.

### 5.3: Verify

Check the names first. This confirms all four landed without echoing your API key, which matters if you're sharing your screen.

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
AzureOpenAI:ReasoningEffort
```

Four `AzureOpenAI:` lines is the thing to check. The order doesn't matter.

Drop the pipe when you want the values too:

```bash
dotnet user-secrets list
```

```
AzureOpenAI:Endpoint = https://your-resource.openai.azure.com/openai/v1/
AzureOpenAI:ApiKey = <your-key-value>
AzureOpenAI:Deployment = chat
AzureOpenAI:ReasoningEffort = None
```

That's Path A. On Path B the last line reads `Low`.

They print in plaintext because the store is plaintext. User secrets keeps the key out of the repo. It doesn't encrypt it.

### 5.4: Smoke-test the values

Step 3 proved the deployment works. It didn't prove these four strings are right, because the playground authenticated as your portal session and never touched KEY 1.

One command closes that gap. Substitute your endpoint and key, and put your reasoning setting in lowercase (`none` on Path A, `low` on Path B):

```bash
curl -sS https://your-resource.openai.azure.com/openai/v1/chat/completions \
  -H "Authorization: Bearer <KEY 1 you copied>" \
  -H "Content-Type: application/json" \
  -d '{"model":"chat","reasoning_effort":"none","messages":[{"role":"user","content":"hello"}]}'
```

> On Windows, run this from PowerShell as `curl.exe`, not `curl`. Bare `curl` is an alias for `Invoke-WebRequest`, which takes different arguments and will fail on the `-H` flags.

The URL is your `AzureOpenAI:Endpoint` with `chat/completions` on the end. JSON back with a `content` field means all four values are good, and anything that breaks in P1.02 is code you wrote.

- **`401`**: the key is wrong or got truncated on the way over. Re-copy KEY 1.
- **`404`**: the deployment name is wrong, or the endpoint is missing its `openai/v1/` path. Compare the name against Foundry's "Models + endpoints", character for character.
- **`400` naming `reasoning_effort`**: the reasoning setting holds a level the model doesn't take. Check it against the table at the top of this guide: `none` on Path A, `low` on Path B. On Path B this almost always means `none` is still in the curl or the secret. `gpt-5-mini` rejects it. Change it to `low` in the curl and `Low` in the secret.

This is the same URL path and the same `Authorization: Bearer` scheme the SDK uses in P1.02, so a reply here means the cloud side is genuinely finished.

---

## Troubleshooting

### Foundry playground doesn't return a reply

Your deployment isn't ready, or your region doesn't support the model. Check the deployment status in Foundry's "Models + endpoints" view. Status should be "Succeeded".

### Can't find "Microsoft Foundry" in the portal

Try "Azure OpenAI" or "Azure AI Foundry". Microsoft has renamed the service multiple times.

### `No secrets configured for this application.`

Your `set` commands errored and the message scrolled past. Run from anywhere other than `src/FinanceAssistant/` and `dotnet user-secrets` fails instead of writing: no project file at the repo root, no `UserSecretsId` in `src/FinanceAssistant.McpServer/`. `cd src/FinanceAssistant/`, re-run the four `set` commands, and watch for errors this time.

### Deployment fails with a quota error

On a GPT-5.6 model, this is the Free Tier, and it's the reason Path B exists. A new pay-as-you-go subscription often has 0 TPM (tokens per minute) for the newest models, and adding a card doesn't change that. Deploy `gpt-5-mini` under the name `chat` and continue on Path B. Nothing else in the workshop changes.

If you want Path A later, request quota with the [quota request form](https://aka.ms/oai/stuquotarequest). Don't wait on it during the workshop. Approval isn't something you can speed up.

On `gpt-5-mini`, it's rarer. Try a different region from Step 1.3, and raise your hand.

### Anything else

Raise your hand. The instructor has seen this fail in unusual ways.

---

## Summary

You've set up:

- **Your model path**: Path A (`gpt-5.6-luna`, Tier 1 or above) or Path B (`gpt-5-mini`, Free Tier), picked by your quota tier.
- **Azure OpenAI resource**: created in Azure portal, region picked.
- **Chat deployment**: `chat`, running your path's model, verified through the Foundry playground.
- **Local user secrets**: four keys stored outside the repo.
- **A verified local connection**: the same endpoint, key, deployment name and reasoning setting your code will use, proven from your own machine with one `curl`.

---

## What's next

P1.02 registers `IChatClient` in a `ServiceCollection` and replaces the echo line in `Program.cs` with a real call to your `chat` deployment. Two optional Extras follow it: streaming the reply, and turning the reasoning dial up to feel what the default bought you.

---

## Bonus: use the OpenAI API instead of Foundry

No Azure subscription, or no patience for quota? Point the workshop straight at OpenAI.

It works because the workshop already talks to Foundry through its OpenAI-compatible `/openai/v1/` surface. OpenAI serves the same API at `/v1/`. Only the values in your secrets change. The code stays the same.

1. Create an API key at [platform.openai.com](https://platform.openai.com/). This path uses `gpt-5.6-luna` for the agent and `gpt-5.6-sol` for the eval judge. **OpenAI's free tier doesn't support `gpt-5.6-sol`**, so the account needs paid credit.
2. Skip Steps 1 to 4. There's no resource and no deployment. On OpenAI the "deployment name" is the model name.
3. Set the four secrets from inside `src/FinanceAssistant/`:

   ```bash
   dotnet user-secrets set "AzureOpenAI:Endpoint"        "https://api.openai.com/v1/"
   dotnet user-secrets set "AzureOpenAI:ApiKey"          "<your OpenAI API key>"
   dotnet user-secrets set "AzureOpenAI:Deployment"      "gpt-5.6-luna"
   dotnet user-secrets set "AzureOpenAI:ReasoningEffort" "None"
   ```

4. Smoke-test it:

   ```bash
   curl -sS https://api.openai.com/v1/chat/completions \
     -H "Authorization: Bearer <your OpenAI API key>" \
     -H "Content-Type: application/json" \
     -d '{"model":"gpt-5.6-luna","reasoning_effort":"none","messages":[{"role":"user","content":"hello"}]}'
   ```

The keys still say `AzureOpenAI:`. That's a name, not a requirement. Later guides ask you to deploy two more things in Foundry, and on this path you set a model name instead:

- **P2.02, embeddings:** `AzureOpenAI:EmbeddingDeployment` = `text-embedding-3-small`.
- **P5.02, the eval judge:** `AzureOpenAI:JudgeDeployment` = `gpt-5.6-sol`.

> Content filtering is OpenAI's own on this path, not Azure's. So the streaming behaviour P1.02's Extra describes, which comes from Azure's filter, may look different here. Notes in later guides about "the workshop deployment" are about Foundry.

---

## Cleanup (after the workshop)

Delete the `finance-assistant-workshop` resource group from the Azure portal. That tears down the Azure OpenAI resource and the deployment together and stops any further charges. Pillar 2 will add an embeddings deployment to the same group, so wait until you're done with the whole workshop before deleting.

On the OpenAI bonus path, revoke the API key on platform.openai.com instead.

---

## Additional Resources

- [Foundry quota tiers](https://learn.microsoft.com/en-us/azure/foundry/openai/quotas-limits)
- [Azure OpenAI quota request form](https://aka.ms/oai/stuquotarequest)
- [GPT-5.6 models on OpenAI](https://developers.openai.com/api/docs/models/GPT-5.6)
- [Microsoft.Extensions.AI documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Azure OpenAI .NET SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.openai-readme)
- [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets)
