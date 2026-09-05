using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace FinanceAssistant.Evals;

#pragma warning disable AIEVAL001

// The packaged evaluator, behind one method, so only this file signs for the
// experimental API and every tool-call eval grades the same way.
internal static class ToolCallGrading
{
    public static async Task<BooleanMetric> GradeAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        IEnumerable<AITool> tools,
        CancellationToken ct = default)
    {
        // The evaluator builds its own ChatOptions and pins Temperature = 0 for
        // determinism. Judge.Client points at a reasoning deployment, and those accept
        // only their default temperature, so that call comes back HTTP 400 before any
        // grading happens. Strip the sampling parameters here rather than in Judge:
        // AdviceJudge sends no options of its own and has never tripped this.
        IChatClient gradingClient = Judge.Client
            .AsBuilder()
            .ConfigureOptions(o => { o.Temperature = null; o.TopP = null; })
            .Build();

        ChatConfiguration chatConfig = new(gradingClient);
        ToolCallAccuracyEvaluatorContext toolContext = new(tools);

        IEvaluator evaluator = new ToolCallAccuracyEvaluator();
        EvaluationResult result = await evaluator.EvaluateAsync(
            messages, response, chatConfig, additionalContext: [toolContext], cancellationToken: ct);

        return result.Get<BooleanMetric>(ToolCallAccuracyEvaluator.ToolCallAccuracyMetricName);
    }

    // AdviceJudge throws when it cannot grade. This one does not: it files the problem in
    // Diagnostics and hands back a metric with nothing in it. So the throwing moves here.
    public static EvaluationDiagnostic? FirstError(this EvaluationMetric metric) =>
        (metric.Diagnostics ?? []).FirstOrDefault(d => d.Severity == EvaluationDiagnosticSeverity.Error);

    // "No verdict" is not a pass. Interpretation must exist AND say it did not fail.
    // Interpretation?.Failed ?? false reads a missing verdict as success. Do not write that.
    public static bool Passed(this EvaluationMetric metric) =>
        metric.Interpretation is { Failed: false };
}

#pragma warning restore AIEVAL001
