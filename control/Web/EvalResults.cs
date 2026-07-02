using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DokiDex.Control.Services;

namespace DokiDex.Web;

// Aggregates evals/results.jsonl -- the eval-gate harness's (evals/run-suite.ps1) log -- into a per-model
// pass/total summary for the eval-gate badges (2.5: "golden 14/15" / "ungated" on the 2.3/2.4 model surfaces).
//
// results.jsonl is PER-TASK rows, one line per (harness, model, task) run: {ts,harness,model,task,pass,seconds,
// note}. A task can appear more than once for the same model (retried after a fix, or re-run by a different
// harness) -- so the aggregation is NOT "count every row" (over-counts retries) and NOT "last line in the file
// wins" (only right by accident; a differently-ordered append breaks it). The correct rule (F3 spec correction):
// group rows by model, then within each model group by TASK and keep only the row with the latest ts for that
// task; passed/total is counted over those latest-per-task rows only. A task retried and eventually fixed then
// counts once, as passing.
public static class EvalResults
{
    private sealed class Row
    {
        public string? Ts { get; set; }
        public string? Harness { get; set; }
        public string? Model { get; set; }
        public string? Task { get; set; }
        public bool Pass { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // The promotion gate runs on ONE harness (crush -- docs/decisions.md's ">=91% golden" numbers are crush
    // scorecards); results.jsonl also carries exploratory runs from other harnesses (claw, opencode).
    public const string GateHarness = "crush";

    // Pure: jsonl lines -> { model (case-insensitive) -> (passed, total) } over the latest-per-task rows,
    // FILTERED to gateHarness. The filter exists because the badge answers "did this model pass the GATE?" --
    // without it, a newer run from a different harness shadows the gate result per the latest-per-task rule
    // (found 2026-07-01: a rapid claw run made coder-fast read 5/11 while its crush gate reads 10/11).
    // gateHarness null/empty disables the filter (all harnesses mixed -- the old behavior, for diagnostics).
    // Malformed lines (bad JSON, or missing ts/model/task -- the fields the aggregation itself depends on) are
    // skipped rather than throwing: a single corrupt/truncated trailing line must never blank out every badge.
    public static Dictionary<string, (int Passed, int Total)> Aggregate(
        IEnumerable<string> jsonlLines, string? gateHarness = GateHarness)
    {
        // model -> task -> latest row seen so far for that task. ISO-8601 timestamps (as the harness writes
        // them) sort correctly under ordinal string comparison, so no DateTime parsing is needed.
        var latestByModelTask = new Dictionary<string, Dictionary<string, Row>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in jsonlLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            Row? row;
            try { row = JsonSerializer.Deserialize<Row>(line, JsonOpts); }
            catch (JsonException) { continue; }
            if (row is null) continue;
            if (string.IsNullOrWhiteSpace(row.Model) || string.IsNullOrWhiteSpace(row.Task) || string.IsNullOrWhiteSpace(row.Ts))
                continue;
            // Gate-harness filter: a row from another harness (or with no harness at all) is not gate evidence.
            if (!string.IsNullOrWhiteSpace(gateHarness)
                && !string.Equals(row.Harness, gateHarness, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!latestByModelTask.TryGetValue(row.Model, out var tasks))
                latestByModelTask[row.Model] = tasks = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);

            if (!tasks.TryGetValue(row.Task, out var existing) || string.CompareOrdinal(row.Ts, existing.Ts) >= 0)
                tasks[row.Task] = row;
        }

        var summary = new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, tasks) in latestByModelTask)
            summary[model] = (tasks.Values.Count(r => r.Pass), tasks.Count);
        return summary;
    }

    // Pure: formats an "eval" badge value for a llamaSwapModel name (the join key both /api/llm-models and
    // /api/llm/tiers use -- results.jsonl rows are named by llama-swap/tier model name, e.g. "coder-fast",
    // "fast-candidate-gptoss20b"). Null when the model has no eval rows at all, or when there's no model name
    // to look up (fim/embed catalog entries have a null llamaSwapModel).
    public static string? Badge(string? llamaSwapModel, IReadOnlyDictionary<string, (int Passed, int Total)> summaries)
    {
        if (string.IsNullOrWhiteSpace(llamaSwapModel)) return null;
        return summaries.TryGetValue(llamaSwapModel, out var s) ? $"{s.Passed}/{s.Total}" : null;
    }

    // Thin I/O wrapper over the real repo file (evals/results.jsonl under RepoPaths.Root). Never throws --
    // missing file, unreadable file, or any other I/O hiccup all degrade to "no eval data" (empty dict), since
    // the badge is optional decoration on top of the model tables, never load-bearing.
    public static Dictionary<string, (int Passed, int Total)> LoadSummaries()
        => LoadSummaries(Path.Combine(RepoPaths.Root, "evals", "results.jsonl"));

    // Test seam: point at a fixture file instead of the real repo path.
    internal static Dictionary<string, (int Passed, int Total)> LoadSummaries(string resultsPath)
    {
        try
        {
            if (!File.Exists(resultsPath)) return new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase);
            return Aggregate(File.ReadAllLines(resultsPath));
        }
        catch
        {
            return new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
