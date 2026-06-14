using DokiCode.Control.Models;
using DokiCode.Control.ViewModels;
using Xunit;

namespace DokiCode.Control.Tests;

public class GpuViewModelTests
{
    [Fact]
    public void Update_computes_gb_percent_and_group()
    {
        var vm = new GpuViewModel();
        vm.Update(new GpuStatus { UsedMB = 16384, TotalMB = 32768, Util = 50, Temp = 60, Watts = 200, Fan = 40, ActiveGroup = "media" });
        Assert.True(vm.Available);
        Assert.Equal(16.0, vm.UsedGb, 1);
        Assert.Equal(16.0, vm.FreeGb, 1);
        Assert.Equal(50, vm.UsedPercent);
        Assert.False(vm.LowHeadroom);
        Assert.False(vm.HotTemp);
        Assert.Contains("MEDIA", vm.GroupLabel);
    }

    [Fact]
    public void Flags_low_headroom_and_hot_temp()
    {
        var vm = new GpuViewModel();
        vm.Update(new GpuStatus { UsedMB = 31000, TotalMB = 32768, Temp = 85, ActiveGroup = "llm" });
        Assert.True(vm.LowHeadroom);   // < 2 GB free
        Assert.True(vm.HotTemp);       // >= 80 C
    }

    [Fact]
    public void Null_gpu_marks_unavailable()
    {
        var vm = new GpuViewModel();
        vm.Update(new GpuStatus { UsedMB = 1, TotalMB = 2, ActiveGroup = "llm" }); // make it available first
        vm.Update(null);
        Assert.False(vm.Available);
        Assert.Equal("GPU n/a", vm.Headline);
    }
}
