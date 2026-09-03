using Microsoft.Extensions.AI;
using Xunit;

namespace FinanceAssistant.Evals;

// Grades the judge, not the agent. Every other eval in this suite ends by taking
// AdviceJudge's word for something, and nothing else checks whether it can do the job.
public class JudgeCalibrationEvals(ITestOutputHelper output)
{
    [Fact]
    public async Task Judge_agrees_with_every_hand_labelled_response()
    {
        List<string> disagreements = [];

        foreach (LabelledResponse testCase in EvalCases.JudgeCalibration)
        {
            List<ChatMessage> messages = [new(ChatRole.User, testCase.UserMessage)];
            ChatResponse response = new(new ChatMessage(ChatRole.Assistant, testCase.AssistantText));

            AdviceVerdict verdict = await AdviceJudge.GradeAsync(
                messages, response, TestContext.Current.CancellationToken);

            bool agreed = verdict.Passed == testCase.ShouldPass;
            output.WriteLine(
                $"[{(agreed ? "agrees" : "DISAGREES")}] {testCase.Name}: "
                + $"labelled {testCase.ShouldPass}, judged {verdict.Passed}. {verdict.Reason}");

            if (!agreed)
            {
                disagreements.Add(
                    $"  {testCase.Name}: labelled {testCase.ShouldPass}, "
                    + $"judged {verdict.Passed}. {verdict.Reason}");
            }
        }

        // These six are unambiguous by construction, so the bar is all of them. There is
        // no threshold worth negotiating on a case you would bet money on yourself.
        Assert.True(
            disagreements.Count == 0,
            $"The judge disagreed with {disagreements.Count} of {EvalCases.JudgeCalibration.Length} "
            + "hand-labelled responses, so its verdicts elsewhere in this suite are not trustworthy."
            + Environment.NewLine + string.Join(Environment.NewLine, disagreements));
    }
}
