using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using DokiDex.Control.Models;
using DokiDex.Control.ViewModels;
using Xunit;

namespace DokiDex.Control.Tests;

// MainViewModel's pure-ish logic (mode derivation, VRAM headroom math, the Busy signal, counts) is driven
// directly via the internal Apply(StatusDoc) seam (InternalsVisibleTo) — no network, no Dispatcher pump.
public class MainViewModelTests
{
    private static MainViewModel Make() => new(Dispatcher.CurrentDispatcher);

    private static ServiceStatus Svc(string name, string group, bool running, bool healthy = true, int vram = 0)
        => new() { Name = name, Group = group, Running = running, Healthy = healthy, Installed = true, VramGb = vram };

    private static StatusDoc Doc(IEnumerable<ServiceStatus> svcs, Dictionary<string, List<string>>? profiles = null, string activeGroup = "llm")
        => new() { Services = svcs.ToList(), Profiles = profiles ?? new(), Gpu = new GpuStatus { UsedMB = 1, TotalMB = 32768, ActiveGroup = activeGroup } };

    [Fact]
    public void DeriveMode_precedence_media_beats_coexist_beats_agent()
    {
        var vm = Make();
        vm.Apply(Doc(new[] { Svc("media", "media", true), Svc("fim", "llm", true), Svc("llama-swap", "llm", true) }, activeGroup: "media"));
        Assert.Equal("media", vm.ActiveMode);
    }

    [Fact]
    public void DeriveMode_fim_is_coexist_llamaswap_is_agent_stopped_is_none()
    {
        var vm = Make();
        vm.Apply(Doc(new[] { Svc("fim", "llm", true) }));
        Assert.Equal("coexist", vm.ActiveMode);

        vm = Make();   // fresh VM: _byName accumulates across Apply() (services are never removed)
        vm.Apply(Doc(new[] { Svc("llama-swap", "llm", true) }));
        Assert.Equal("agent", vm.ActiveMode);

        vm = Make();
        vm.Apply(Doc(new[] { Svc("llama-swap", "llm", false) }, activeGroup: "none"));
        Assert.Equal("none", vm.ActiveMode);
    }

    [Fact]
    public void SwitchMode_is_disabled_for_the_already_active_mode()
    {
        var vm = Make();
        vm.Apply(Doc(new[] { Svc("media", "media", true) }, activeGroup: "media"));
        Assert.Equal("media", vm.ActiveMode);
        Assert.False(vm.SwitchModeCommand.CanExecute("media"));   // already in MEDIA -> its button disables
        Assert.True(vm.SwitchModeCommand.CanExecute("agent"));    // a different mode stays clickable
        Assert.True(vm.SwitchModeCommand.CanExecute("coexist"));
    }

    [Fact]
    public void BuildExplain_reports_free_headroom_when_it_fits()
    {
        var vm = Make();
        vm.Apply(Doc(new[] { Svc("llama-swap", "llm", true, vram: 26) }, new() { ["agent"] = new() { "llama-swap" } }));
        vm.HoverMode = "agent";
        Assert.Contains("~6 GB free", vm.SwitchExplain);   // 32 - 26
        Assert.DoesNotContain("exceeds", vm.SwitchExplain);
    }

    [Fact]
    public void BuildExplain_warns_when_a_profile_exceeds_32gb()
    {
        var vm = Make();
        vm.Apply(Doc(new[] { Svc("media", "media", true, vram: 18), Svc("prompt-rewriter", "media", true, vram: 20) },
            new() { ["media"] = new() { "media", "prompt-rewriter" } }, activeGroup: "media"));
        vm.HoverMode = "media";
        Assert.Contains("exceeds 32 GB", vm.SwitchExplain);   // 18 + 20 = 38 > 32 — the only thing that hits the warn branch
    }

    [Fact]
    public void Busy_true_while_a_service_is_degraded_then_clears_when_healthy()
    {
        var vm = Make();
        vm.Apply(Doc(new[] { Svc("media", "media", running: true, healthy: false) }));   // freshly degraded
        Assert.True(vm.Busy);
        vm.Apply(Doc(new[] { Svc("media", "media", running: true, healthy: true) }));    // recovered
        Assert.False(vm.Busy);
    }

    [Fact]
    public void TotalServiceCount_counts_both_bands()
    {
        var vm = Make();
        vm.Apply(Doc(new[] { Svc("llama-swap", "llm", true), Svc("media", "media", true) }));
        Assert.Equal(2, vm.TotalServiceCount);
    }

    [Fact]
    public void Apply_keeps_StatusUnavailable_false_on_success()
    {
        var vm = Make();
        Assert.False(vm.StatusUnavailable);
        vm.Apply(Doc(new[] { Svc("llama-swap", "llm", true) }));
        Assert.False(vm.StatusUnavailable);
    }

    // The --design / DOKI_SAMPLE fixture is the panel's headless QA surface: it must paint EVERY card state
    // (healthy/degraded/crashed/down/notinstalled) across BOTH bands, or a preview/snapshot silently stops
    // covering a state. Guards SampleData + LoadDesignSample together (incl. the forced-crashed seam).
    [Fact]
    public void DesignSample_exercises_the_full_state_vocabulary_across_both_bands()
    {
        var vm = Make();
        vm.LoadDesignSample();

        Assert.NotEmpty(vm.LlmServices);     // DokiCode band
        Assert.NotEmpty(vm.MediaServices);   // DokiGen band

        var kinds = vm.LlmServices.Concat(vm.MediaServices).Select(s => s.StateKind).ToHashSet();
        foreach (var expected in new[] { "healthy", "degraded", "crashed", "down", "notinstalled" })
            Assert.Contains(expected, kinds);

        // the two time-/config-derived states the fixture exists to prove are reachable headlessly
        Assert.Equal("crashed", vm.LlmServices.Single(s => s.Name == "stt").StateKind);   // forced in LoadDesignSample
        Assert.Equal("down", vm.LlmServices.Single(s => s.Name == "tts").StateKind);      // installed + stopped
        Assert.True(vm.LlmServices.Single(s => s.Name == "llama-swap").HasModelSwap);     // >1 configured model -> swap chips
        Assert.True(vm.Busy);                                                             // fim degraded -> caption sigil works
        Assert.Equal("design sample — no backend", vm.StatusText);
    }
}
