using DokiCode.Control.Models;
using DokiCode.Control.Services;
using DokiCode.Control.ViewModels;
using Xunit;

namespace DokiCode.Control.Tests;

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
}
