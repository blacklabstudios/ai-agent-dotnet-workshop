using FinanceAssistant.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace FinanceAssistant.Evals;

// No AIEVAL001 pragma here. BooleanMetric and EvaluationDiagnostic are stable, and
// ToolCallGrading signed for the experimental types on the whole suite's behalf.
[Trait("Category", "Eval")]
public class ToolCallAccuracyEvals(ITestOutputHelper output)
{
    [EvalFact]
    public async Task Agent_calls_GetTransactions_for_a_month_request()
    {
        AgentTurn turn = await AgentUnderTest.RespondToAsync(
            "List my transactions for May 2025.", TestContext.Current.CancellationToken);

        output.WriteLine($"[tools] called: {turn.ToolCallSummary}");

        // Free and never billed, though not deterministic: the tool choice is still the
        // model's. Cheap enough to gate on, and a plain month request has one right tool.
        Assert.Contains(turn.ToolCalls, c => c.Name == "GetTransactions");

        BooleanMetric accuracy = await ToolCallGrading.GradeAsync(
            turn.Messages, turn.Response, turn.Tools, TestContext.Current.CancellationToken);

        output.WriteLine($"[tools] {accuracy.Value}: {accuracy.Reason}");

        // Both guards, every time. The first says why there was no verdict, the second
        // refuses to read "no verdict" as a pass.
        EvaluationDiagnostic? error = accuracy.FirstError();
        Assert.True(error is null, $"The evaluator could not grade this: {error?.Message}");

        Assert.True(accuracy.Passed(), $"Tool call accuracy failed: {accuracy.Reason}");
    }

    [EvalFact]
    public async Task Recorded_call_with_a_natural_language_date_is_rejected()
    {
        AIFunction getTransactions =
            AIFunctionFactory.Create(new GetTransactionsTool().GetTransactions);

        List<ChatMessage> messages = [new(ChatRole.User, "List my transactions for May 2025.")];

        // The exact input Pillar 2 taught the agent never to send. This eval asserts the
        // evaluator CATCHES it, which is a calibration check on the library's judge, the
        // same idea P5.02 Step 5 applied to your own.
        ChatResponse response = new(
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        callId: "call-1",
                        name: getTransactions.Name,
                        arguments: new Dictionary<string, object?> { ["dateExpression"] = "yesterday" })
                ]));

        BooleanMetric accuracy = await ToolCallGrading.GradeAsync(
            messages, response, [getTransactions], TestContext.Current.CancellationToken);

        output.WriteLine($"[tools] {accuracy.Value}: {accuracy.Reason}");

        EvaluationDiagnostic? error = accuracy.FirstError();
        Assert.True(error is null, $"The evaluator could not grade this: {error?.Message}");

        Assert.False(
            accuracy.Passed(),
            "The evaluator passed a natural-language date against a contract that "
            + $"demands ISO 8601, so it is not measuring what we think: {accuracy.Reason}");
    }
}
