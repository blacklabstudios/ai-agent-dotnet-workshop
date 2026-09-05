const log = document.getElementById("log");
const form = document.getElementById("composer");
const input = document.getElementById("input");
const send = document.getElementById("send");

// One thread per tab. The server keys its ConversationStore on this, so it is the
// whole of "whose conversation is this".
const threadId = sessionStorage.getItem("fa.threadId") ?? crypto.randomUUID();
sessionStorage.setItem("fa.threadId", threadId);

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const message = input.value.trim();
  if (!message) return;

  input.value = "";
  bubble("user").textContent = message;

  await run({
    messages: [{ id: crypto.randomUUID(), role: "user", content: message }]
  });
});

async function run(body) {
  send.disabled = true;

  // TOOL_CALL_START names a call, TOOL_CALL_ARGS streams its arguments, and an interrupt
  // arrives later referencing only the id. Keep the pieces so the approval card can show
  // what it is approving.
  const calls = new Map();
  let assistant = null;

  try {
    for await (const event of stream(body)) {
      switch (event.type) {
        case "TEXT_MESSAGE_START":
          assistant = bubble("assistant");
          break;

        case "TEXT_MESSAGE_CONTENT":
          assistant ??= bubble("assistant");
          assistant.textContent += event.delta;
          break;

        case "TEXT_MESSAGE_END":
          assistant = null;
          break;

        case "TOOL_CALL_START":
          assistant = null;
          calls.set(event.toolCallId, { callId: event.toolCallId, name: event.toolCallName, args: "" });
          bubble("tool").textContent = `calling ${event.toolCallName}...`;
          break;

        case "TOOL_CALL_ARGS": {
          const call = calls.get(event.toolCallId);
          if (call) call.args += event.delta;
          break;
        }

        case "RUN_FINISHED":
          if (event.outcome?.type === "interrupt") {
            for (const interrupt of event.outcome.interrupts ?? []) {
              renderApproval(interrupt, calls.get(interrupt.toolCallId));
            }
          }
          break;

        case "RUN_ERROR":
          bubble("tool").textContent = `Run failed: ${event.message}`;
          break;
      }
    }
  } catch (err) {
    bubble("tool").textContent = `Connection lost: ${err.message}`;
  } finally {
    send.disabled = false;
    input.focus();
  }
}

async function* stream(body) {
  const response = await fetch("/api/chat", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      threadId,
      runId: crypto.randomUUID(),
      messages: [],
      state: {},
      tools: [],
      context: [],
      forwardedProps: {},
      ...body
    })
  });

  if (!response.ok) throw new Error(`HTTP ${response.status}`);

  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;

    buffer += value;

    let split;
    while ((split = buffer.indexOf("\n\n")) >= 0) {
      const frame = buffer.slice(0, split);
      buffer = buffer.slice(split + 2);

      const data = frame
        .split("\n")
        .filter((line) => line.startsWith("data:"))
        .map((line) => line.slice(5).trim())
        .join("\n");

      if (data) yield JSON.parse(data);
    }
  }
}

function renderApproval(interrupt, call) {
  let args = {};
  try {
    args = JSON.parse(call?.args || "{}");
  } catch {
    args = {};
  }

  const pretty = Object.entries(args).map(([key, value]) => `${key}=${value}`).join(", ");

  const box = document.createElement("div");
  box.className = "approval";
  box.append(document.createElement("strong"), " needs your approval.", document.createElement("code"));
  box.querySelector("strong").textContent = call?.name ?? "This tool";
  box.querySelector("code").textContent = pretty || "(no arguments)";

  for (const [label, approved] of [["Approve", true], ["Decline", false]]) {
    const button = document.createElement("button");
    button.textContent = label;
    button.addEventListener("click", async () => {
      box.querySelectorAll("button").forEach((b) => (b.disabled = true));
      box.append(approved ? " Approved." : " Declined.");

      // No new message. The run resumes from the interrupt the server is holding.
      await run({
        messages: [],
        resume: [{
          interruptId: interrupt.id,
          status: "resolved",
          payload: {
            approved,
            toolCall: { callId: call?.callId ?? interrupt.toolCallId, name: call?.name, arguments: args }
          }
        }]
      });
    });
    box.append(button);
  }

  log.append(box);
  box.scrollIntoView({ block: "end" });
}

function bubble(kind) {
  const el = document.createElement("div");
  el.className = `msg ${kind}`;
  log.append(el);
  el.scrollIntoView({ block: "end" });
  return el;
}
