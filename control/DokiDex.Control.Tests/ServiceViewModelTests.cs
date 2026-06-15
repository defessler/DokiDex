using DokiDex.Control.Models;
using DokiDex.Control.Services;
using DokiDex.Control.ViewModels;
using Xunit;

namespace DokiDex.Control.Tests;

public class ServiceViewModelTests
{
    private static ServiceViewModel Make(ServiceStatus s) => new(new DokiService(), new TestGenService(), s);

    [Fact]
    public void Healthy_service_derives_state_detail_and_swap()
    {
        var vm = Make(new ServiceStatus
        {
            Name = "llama-swap", Group = "llm", Healthy = true, Running = true, Installed = true,
            Model = "coder-fast", VramGb = 26, Pid = 123,
            ConfiguredModels = new() { "coder-big", "coder-fast", "coder-fast-lite" }
        });
        Assert.Equal("healthy", vm.StateKind);
        Assert.Contains("coder-fast", vm.Detail);
        Assert.True(vm.HasModelSwap);   // > 1 configured model
    }

    [Fact]
    public void Not_installed_shows_setup_hint_and_blocks_start()
    {
        var vm = Make(new ServiceStatus { Name = "stt", Installed = false });
        Assert.Equal("notinstalled", vm.StateKind);
        Assert.Contains("setup.ps1 -Stt", vm.Detail);
        Assert.False(vm.StartCommand.CanExecute(null));   // gated on Installed
    }

    [Fact]
    public void Degraded_when_running_but_unhealthy()
    {
        var vm = Make(new ServiceStatus { Name = "tts", Installed = true, Running = true, Healthy = false });
        Assert.Equal("degraded", vm.StateKind);
    }

    [Fact]
    public void Stopped_when_installed_and_not_running()
    {
        var vm = Make(new ServiceStatus { Name = "fim", Installed = true, Running = false, Healthy = false });
        Assert.Equal("down", vm.StateKind);
        Assert.False(vm.HasModelSwap);   // 0 configured models
    }

    [Fact]
    public void Update_badge_only_when_behind()
    {
        var vm = Make(new ServiceStatus { Name = "media", Installed = true });
        vm.Update = "current";
        Assert.False(vm.HasUpdate);
        vm.Update = "3 behind";
        Assert.True(vm.HasUpdate);
        Assert.Contains("3 behind", vm.VersionLine);
    }

    [Fact]
    public void Single_configured_model_has_no_swap()
    {
        var vm = Make(new ServiceStatus { Name = "x", Installed = true, ConfiguredModels = new() { "only" } });
        Assert.False(vm.HasModelSwap);
    }

    // --- crashed-state escalation (degraded -> crashed after a 90s grace), via the injected clock seam ---
    private static ServiceStatus Unhealthy() => new() { Name = "media", Group = "media", Installed = true, Running = true, Healthy = false };
    private static ServiceStatus Recovered() => new() { Name = "media", Group = "media", Installed = true, Running = true, Healthy = true };
    private static ServiceViewModel Clocked(ServiceStatus s, System.Func<System.DateTime> now)
        => new(new DokiService(), new TestGenService(), s, now);

    [Fact]
    public void Stays_degraded_within_the_grace_window()
    {
        var t = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        var vm = Clocked(Unhealthy(), () => t);
        Assert.Equal("degraded", vm.StateKind);            // elapsed 0s
        t = t.AddSeconds(80); vm.Sync(Unhealthy());
        Assert.Equal("degraded", vm.StateKind);            // 80s < 90s grace
    }

    [Fact]
    public void Escalates_to_crashed_past_the_grace_window()
    {
        var t = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        var vm = Clocked(Unhealthy(), () => t);
        t = t.AddSeconds(91); vm.Sync(Unhealthy());
        Assert.Equal("crashed", vm.StateKind);
        Assert.Contains("not responding", vm.StateLabel);
    }

    [Fact]
    public void Recovery_resets_the_grace_clock()
    {
        var t = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        var vm = Clocked(Unhealthy(), () => t);
        t = t.AddSeconds(91); vm.Sync(Unhealthy());
        Assert.Equal("crashed", vm.StateKind);
        vm.Sync(Recovered());
        Assert.Equal("healthy", vm.StateKind);             // recovery clears _unhealthySince
        t = t.AddSeconds(10); vm.Sync(Unhealthy());
        Assert.Equal("degraded", vm.StateKind);            // grace restarts; does NOT immediately re-crash
    }

    [Fact]
    public void Flap_restarts_the_grace_so_it_does_not_prematurely_crash()
    {
        var t = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
        var vm = Clocked(Unhealthy(), () => t);            // unhealthy @0
        t = t.AddSeconds(10); vm.Sync(Recovered());        // healthy @10 -> resets
        t = t.AddSeconds(10); vm.Sync(Unhealthy());        // unhealthy @20 -> grace restarts here
        t = t.AddSeconds(60); vm.Sync(Unhealthy());        // @80: only 60s since the restart -> still degraded
        Assert.Equal("degraded", vm.StateKind);
    }
}
