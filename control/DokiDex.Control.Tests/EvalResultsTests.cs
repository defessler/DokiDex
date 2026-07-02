using System;
using System.Collections.Generic;
using System.IO;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// EvalResults.Aggregate turns evals/results.jsonl's PER-TASK rows ({ts,harness,model,task,pass,seconds,note},
// one line per run of one task against one model) into a per-model passed/total badge. The naive readings are
// both WRONG and must not creep back in:
//   - "count every row" over-counts retries (a task retried 3x until it passed would show 1/3, not 1/1).
//   - "last line in the file wins" is right only by accident when rows happen to be appended in ts order AND
//     interleaved differently-shaped task sets don't collide.
// The correct rule (F3): group by model, then within each model group by TASK and keep only the row with the
// latest ts for that task; passed/total is counted over those latest-per-task rows only.
public class EvalResultsTests
{
    private static string Row(string ts, string model, string task, bool pass, string harness = "crush")
        => $$"""{"ts":"{{ts}}","harness":"{{harness}}","model":"{{model}}","task":"{{task}}","pass":{{(pass ? "true" : "false")}},"seconds":1.0,"note":"x"}""";

    [Fact]
    public void Aggregate_of_empty_input_returns_an_empty_dictionary()
        => Assert.Empty(EvalResults.Aggregate(Array.Empty<string>()));

    [Fact]
    public void A_newer_run_from_a_NON_gate_harness_cannot_shadow_the_crush_gate_result()
    {
        // The 2026-07-01 field bug: a rapid claw-harness run wrote newer rows for the same tasks, and the
        // harness-blind latest-per-task rule let them shadow the crush gate (coder-fast read 5/11 instead of
        // its real 10/11 gate). Rows from other harnesses must be excluded from the gate badge entirely.
        var lines = new[]
        {
            Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: true),                     // the crush gate result
            Row("2026-06-13T01:53:00", "coder-fast", "t1", pass: false, harness: "claw"),   // newer, wrong harness
            Row("2026-06-13T01:54:00", "coder-fast", "t9", pass: false, harness: "claw"),   // claw-only task
        };
        var summary = EvalResults.Aggregate(lines);
        var (passed, total) = summary["coder-fast"];
        Assert.Equal(1, total);    // t9's claw-only row contributes nothing to the gate badge
        Assert.Equal(1, passed);   // and t1 still reads as the crush pass
    }

    [Fact]
    public void A_null_gate_harness_disables_the_filter_for_diagnostics()
    {
        var lines = new[]
        {
            Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: true),
            Row("2026-06-13T01:53:00", "coder-fast", "t1", pass: false, harness: "claw"),
        };
        var summary = EvalResults.Aggregate(lines, gateHarness: null);
        var (passed, total) = summary["coder-fast"];
        Assert.Equal(1, total);
        Assert.Equal(0, passed);   // unfiltered: the newer claw fail wins latest-per-task (the old behavior)
    }

    [Fact]
    public void Aggregate_keeps_only_the_most_recent_row_per_task_an_older_fail_then_newer_pass_counts_as_1_of_1()
    {
        var lines = new[]
        {
            Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: false),
            Row("2026-06-12T16:29:00", "coder-fast", "t1", pass: true),
        };
        var summary = EvalResults.Aggregate(lines);
        var (passed, total) = summary["coder-fast"];
        Assert.Equal(1, total);
        Assert.Equal(1, passed);
    }

    [Fact]
    public void Aggregate_counts_a_newer_fail_over_an_older_pass_for_the_same_task()
    {
        var lines = new[]
        {
            Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: true),
            Row("2026-06-12T16:29:00", "coder-fast", "t1", pass: false),
        };
        var summary = EvalResults.Aggregate(lines);
        var (passed, total) = summary["coder-fast"];
        Assert.Equal(1, total);
        Assert.Equal(0, passed);
    }

    [Fact]
    public void Aggregate_counts_each_distinct_task_once_toward_the_total()
    {
        var lines = new[]
        {
            Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: true),
            Row("2026-06-12T16:21:00", "coder-fast", "t2", pass: false),
            Row("2026-06-12T16:22:00", "coder-fast", "t3", pass: true),
        };
        var summary = EvalResults.Aggregate(lines);
        var (passed, total) = summary["coder-fast"];
        Assert.Equal(3, total);
        Assert.Equal(2, passed);
    }

    [Fact]
    public void Aggregate_groups_separately_per_model()
    {
        var lines = new[]
        {
            Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: true),
            Row("2026-06-12T16:21:00", "coder-big", "t1", pass: false),
            Row("2026-06-12T16:22:00", "coder-big", "t2", pass: false),
        };
        var summary = EvalResults.Aggregate(lines);
        Assert.Equal((1, 1), summary["coder-fast"]);
        Assert.Equal((0, 2), summary["coder-big"]);
    }

    [Fact]
    public void Aggregate_treats_model_names_case_insensitively()
    {
        var lines = new[]
        {
            Row("2026-06-12T16:20:00", "Coder-Fast", "t1", pass: true),
            Row("2026-06-12T16:21:00", "coder-fast", "t2", pass: true),
        };
        var summary = EvalResults.Aggregate(lines);
        Assert.Single(summary);
        Assert.Equal((2, 2), summary["coder-fast"]);
        Assert.Equal((2, 2), summary["CODER-FAST"]);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"ts":"2026-06-12T16:20:00","harness":"crush","task":"t1","pass":true,"seconds":1.0,"note":"x"}""")]   // missing model
    [InlineData("""{"ts":"2026-06-12T16:20:00","harness":"crush","model":"coder-fast","pass":true,"seconds":1.0,"note":"x"}""")]   // missing task
    [InlineData("""{"harness":"crush","model":"coder-fast","task":"t1","pass":true,"seconds":1.0,"note":"x"}""")]   // missing ts
    public void Aggregate_skips_malformed_or_incomplete_lines_without_throwing(string badLine)
    {
        var lines = new[]
        {
            Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: true),
            badLine,
        };
        var summary = EvalResults.Aggregate(lines);
        Assert.Equal((1, 1), summary["coder-fast"]);
    }

    [Fact]
    public void Aggregate_skips_blank_lines_interspersed_in_the_file()
    {
        var lines = new[] { "", Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: true), "   " };
        var summary = EvalResults.Aggregate(lines);
        Assert.Equal((1, 1), summary["coder-fast"]);
    }

    // ---------------------------------------------------------------------------------------------------
    // Badge formatting (pure): "passed/total" for a llamaSwapModel key, or null when there's no data / no
    // model name at all -- the join key used by both /api/llm-models and /api/llm/tiers (2.5).
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Badge_formats_passed_over_total_for_a_known_model()
    {
        var summary = new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase)
        {
            ["coder-fast"] = (14, 15),
        };
        Assert.Equal("14/15", EvalResults.Badge("coder-fast", summary));
    }

    [Fact]
    public void Badge_is_case_insensitive_on_the_model_key()
    {
        var summary = new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase)
        {
            ["coder-fast"] = (14, 15),
        };
        Assert.Equal("14/15", EvalResults.Badge("CODER-FAST", summary));
    }

    [Fact]
    public void Badge_returns_null_for_a_model_with_no_eval_data()
    {
        var summary = new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase);
        Assert.Null(EvalResults.Badge("coder-fast", summary));
    }

    [Fact]
    public void Badge_returns_null_for_a_null_or_blank_model_name()
    {
        var summary = new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase)
        {
            ["coder-fast"] = (14, 15),
        };
        Assert.Null(EvalResults.Badge(null, summary));
        Assert.Null(EvalResults.Badge("", summary));
        Assert.Null(EvalResults.Badge("   ", summary));
    }

    // ---------------------------------------------------------------------------------------------------
    // LoadSummaries (thin I/O wrapper): missing file -> empty dict, never throws.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void LoadSummaries_of_a_missing_file_returns_an_empty_dictionary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dokidex-evalresults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var result = EvalResults.LoadSummaries(Path.Combine(dir, "does-not-exist.jsonl"));
            Assert.Empty(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadSummaries_reads_and_aggregates_a_real_file_on_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dokidex-evalresults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "results.jsonl");
        try
        {
            File.WriteAllLines(path, new[]
            {
                Row("2026-06-12T16:20:00", "coder-fast", "t1", pass: false),
                Row("2026-06-12T16:29:00", "coder-fast", "t1", pass: true),
            });
            var result = EvalResults.LoadSummaries(path);
            Assert.Equal((1, 1), result["coder-fast"]);
        }
        finally { Directory.Delete(dir, true); }
    }
}
