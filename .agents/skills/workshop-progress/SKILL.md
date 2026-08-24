---
name: workshop-progress
description: Track and show a learner's progress through the Build Your First AI Agent in .NET workshop. Use when the learner finishes a step or an exercise, asks where they are or what's next, asks to see or open their progress page, or hits and fixes a problem worth recording. Reads the guides and the working tree, and records verified progress and notes in progress.json.
---

# Workshop progress

The learner follows their progress in `progress.html` at the repo root, opened straight from disk. This skill keeps
it current: you record verified progress in `progress.json`, then the tool at `scripts/Progress.cs` folds that, the
guides and the working tree into `progress.data.js`, which the page loads. Run every command from the repo root.

## 1. See where the learner is

```bash
dotnet run .agents/skills/workshop-progress/scripts/Progress.cs -- --status
```

It prints JSON. For each exercise you get its `code`, `title`, `guide`, its numbered `steps`, its `state`
(`none`, `started` or `done`), `decidedBy` (`agent` when `progress.json` holds your verdict, `code` otherwise),
`codeEvidence` and `saved` (what `progress.json` already holds). From code alone an exercise is only `started` until a
later one starts, so the page waits for your check before it says done. `next` is the first core exercise that isn't
done. If the learner hasn't said which exercise they're on, assume `next`, and ask when the conversation suggests
otherwise. When `endBranch` is set, the learner is on an end-state branch: apply "After a jump" below before anything
else. If the command exits with code 1, `progressError` says what's wrong with `progress.json`: fix the file first.

## 2. Check before you record

Only record what you have evidence for:

- A step is done when its change is in the working tree and matches what the guide's step asks for. Read the guide
  section (`## Step N: ...` in the exercise's `guide` file) and compare.
- An exercise is done when every step is done and `dotnet build` passes. Mark it with `"done": true` and an
  `evidence` line that says what you checked, such as `"dotnet build passed; the learner saw the Test it output"`.
  Your verdict wins over the code: `"done": false` shows the exercise as in progress even when its files exist, so use
  it when a step is still missing, and say which one in `evidence`.
- Some steps only run the app and watch what happens, such as P3.01's runaway or P5.01's mixed batch. If the learner
  chooses to skip one, record that step with `"done": false`, `"skipped": true` and a note saying it was skipped on
  purpose. The page marks it Skipped. The exercise
  can still be done once every step that changes code is in and `dotnet build` passes. Name the skipped step in
  `evidence`.
- The guide's "Test it" check (in its `## At a glance` section) runs the app against a live model, which costs money.
  Don't run it yourself unless the learner asks. Ask them to run it and tell you what they saw.
- Never mark something done because the learner is in a hurry or because a file merely exists.
- Some steps leave nothing in the repo: the Azure portal steps in P1.01, or a run the learner watched. When the learner
  tells you they did one ("mark step 2 of P1.01 done"), record it as done with a note that says so, such as
  `"Done in the Azure portal, per the learner"`. That's the only way a learner ticks a step: the page is read-only.
- Some steps are reading only, such as P2.01 Step 5, "Where you have already seen this". They change no code and run
  nothing. Tick one when the learner says they've read it, with a note such as `"Read, per the learner"`.

## 3. Update progress.json

Create the file if it's missing. Merge into what's there: never delete another exercise, step, note or issue.
Exercise ids are the `id` values from `--status` (`p1-01`, `p3-01`, `p2-b01`, `demo-p5`, ...). Step keys are the
step numbers as strings.

```json
{
  "exercises": {
    "p3-01": {
      "done": true,
      "evidence": "dotnet build passed; the learner saw the Test it output. Step 5 (the runaway) skipped on purpose",
      "updated": "2026-11-25T10:30",
      "steps": {
        "1": { "done": true, "note": "Removed UseFunctionInvocation from ServiceCollectionExtensions.cs" },
        "2": { "done": true, "note": "Wrote ChatAgent.cs from the guide; renamed maxIterations to maxRounds" },
        "5": { "done": false, "skipped": true, "note": "Skipped the runaway on purpose" }
      },
      "notes": ["Chat deployment is named gpt-5.6-luna, not chat. The secret holds it, so no code change."],
      "issues": [
        { "problem": "404 DeploymentNotFound on embeddings", "fix": "Redeployed the embedding model on the endpoint's resource" }
      ]
    }
  }
}
```

- `note`: one line, in plain words, about what the learner actually did, including where they departed from the guide.
- `notes`: deviations that matter later (different names, a skipped optional step, a path they chose).
- `issues`: problems they hit and how they were fixed. Use `"fix": ""` for one that is still open.
- `caughtUp`: the branch name, only when the exercise came from a jump to an end-state branch. Leave it out otherwise.
- `updated`: local time, `yyyy-MM-ddTHH:mm`. Get it from the shell rather than guessing: `date +%Y-%m-%dT%H:%M`
  (macOS, Linux, Git Bash) or `Get-Date -Format yyyy-MM-ddTHH:mm` (PowerShell).
- Never write a secret value, an API key or an endpoint's key into the file.
- `progress.json` is gitignored. Don't commit it.

### After a jump to an end-state branch

Learners who fall behind check out an end-state branch, such as `p4-02-end` (`git branch --show-current` tells you
which). The branch holds every core exercise up to and including that one, from P1.02 on. P1.01 is Azure setup, which
no branch contains, so leave it to its own evidence.

For each of those exercises, P1.02 up to the branch's exercise:

- Already `"done": true`? Leave it exactly as it is. The learner did that work, and its evidence stays.
- Otherwise, set `"done": true`, `"evidence": "Caught up with the p4-02-end branch"` and `"caughtUp": "p4-02-end"`.
  This replaces an earlier `"done": false` too, because the branch now holds the finished code.
- Keep the notes and issues already recorded, and the steps the learner finished (`"done": true`). Don't invent step
  notes for work the learner didn't write.
- If the exercise had been marked `"done": false`, add one `notes` line naming what was still missing before the jump,
  then remove the entries for the steps that weren't done. Their old notes ("not started") would otherwise sit under a
  finished exercise. The page shows those steps as coming from the branch.

The page then shows the exercises you set as caught up, and the ones the learner finished as their own.

`endBranch` stays set for as long as the learner works on that branch. Once every exercise it covers is done, this
section has nothing left to do, so skip it and carry on with the exercise they're on.

## 4. Refresh the page

```bash
dotnet run .agents/skills/workshop-progress/scripts/Progress.cs
```

It rewrites `progress.data.js` and prints what's next. Tell the learner to open `progress.html` at the repo root, or to
hard refresh it if it's already open (Cmd+Shift+R on macOS, Ctrl+Shift+R or Ctrl+F5 on Windows and Linux). A plain
click on a link inside the page doesn't reload the data. Do this every time you use this skill, even when you only read
the status, so the page matches the working tree.

## Rules

- This skill records progress. It never writes or edits the learner's exercise code.
- Keep notes short and factual. They're for the learner and their facilitator, not a log of your reasoning.
