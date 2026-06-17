using System.Linq;
using DokiDex.Web;
using Xunit;

namespace DokiDex.Control.Tests;

// The pure argv-builder for kohya LoRA training. The trainer + GPU aren't present in a dev env, so TrainAsync
// degrades to "not installed" (verified at the endpoint); the documented train_network.py command is locked here.
public class TrainingTests
{
    private static string Val(System.Collections.Generic.IReadOnlyList<string> a, string flag)
        => a[a.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Builds_the_standard_lora_train_command()
    {
        var a = Training.BuildArgs(@"C:\m\base.safetensors", @"C:\d", @"C:\out", "mystyle", 1600, 16);
        Assert.Equal("launch", a[0]);
        Assert.EndsWith("train_network.py", a[1]);
        Assert.Equal(@"C:\m\base.safetensors", Val(a, "--pretrained_model_name_or_path"));
        Assert.Equal(@"C:\d", Val(a, "--train_data_dir"));
        Assert.Equal("mystyle", Val(a, "--output_name"));
        Assert.Equal("networks.lora", Val(a, "--network_module"));
        Assert.Equal("1600", Val(a, "--max_train_steps"));
        Assert.Equal("16", Val(a, "--network_dim"));
    }

    [Fact]
    public void Steps_and_dim_are_clamped_to_sane_ranges()
    {
        var a = Training.BuildArgs("b", "d", "o", "n", 999999, 999);
        Assert.Equal("20000", Val(a, "--max_train_steps"));
        Assert.Equal("128", Val(a, "--network_dim"));
    }

    [Fact]
    public void Not_installed_in_a_dev_env()
        => Assert.False(Training.Installed);   // no sd-scripts venv here -> graceful degradation path
}
