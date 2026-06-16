using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DokiDex.Control.Models;
using DokiDex.Control.Services;
using Xunit;

namespace DokiDex.Control.Tests;

// The native control plane must not drift from doki.ps1: these parse the real $Services/$Profiles and assert
// the C# ServiceRegistry mirrors them, plus cover the pure status/lifecycle helpers (GPU parse, active group,
// netstat port-owner parse).
public class ControlPlaneTests
{
    private static string FindDokiPs1()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null) { var f = Path.Combine(d.FullName, "doki.ps1"); if (File.Exists(f)) return f; d = d.Parent; }
        throw new FileNotFoundException("doki.ps1 not found walking up from the test bin");
    }

    [Fact]
    public void Registry_service_names_match_doki_ps1_Services()
    {
        var text = File.ReadAllText(FindDokiPs1());
        var block = Regex.Match(text, @"\$Services\s*=\s*\[ordered\]@\{(.*?)\n\}", RegexOptions.Singleline).Groups[1].Value;
        var names = Regex.Matches(block, @"(?m)^\s*""([a-z0-9-]+)""\s*=\s*@\{").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(names);
        Assert.Equal(names, ServiceRegistry.Services.Select(s => s.Name).ToHashSet());
    }

    [Fact]
    public void Registry_profiles_match_doki_ps1_Profiles()
    {
        var text = File.ReadAllText(FindDokiPs1());
        var line = Regex.Match(text, @"\$Profiles\s*=\s*\[ordered\]@\{(.*?)\}", RegexOptions.Singleline).Groups[1].Value;
        var parsed = new Dictionary<string, List<string>>();
        foreach (Match m in Regex.Matches(line, @"(\w+)\s*=\s*@\(([^)]*)\)"))
            parsed[m.Groups[1].Value] = Regex.Matches(m.Groups[2].Value, @"""([a-z0-9-]+)""").Select(x => x.Groups[1].Value).ToList();

        Assert.Equal(parsed.Keys.OrderBy(k => k), ServiceRegistry.Profiles.Keys.OrderBy(k => k));
        foreach (var kv in parsed) Assert.Equal(kv.Value, ServiceRegistry.Profiles[kv.Key]);
    }

    [Theory]
    [InlineData("12000, 32607, 30, 55, 410.5, 38", 12000, 32607, 30, 55, 38)]
    public void ParseGpu_reads_csv_fields(string line, int used, int total, int util, int temp, int fan)
    {
        var g = StatusProbe.ParseGpu(line, "media");
        Assert.NotNull(g);
        Assert.Equal(used, g!.UsedMB);
        Assert.Equal(total, g.TotalMB);
        Assert.Equal(util, g.Util);
        Assert.Equal(temp, g.Temp);
        Assert.Equal(fan, g.Fan);
        Assert.Equal("media", g.ActiveGroup);
    }

    [Fact]
    public void ParseGpu_coerces_NA_fan_to_null_and_blank_line_to_null()
    {
        var g = StatusProbe.ParseGpu("12000, 32607, 30, 55, 410.5, [N/A]", "llm");
        Assert.NotNull(g);
        Assert.Null(g!.Fan);
        Assert.Equal(12000, g.UsedMB);
        Assert.Null(StatusProbe.ParseGpu("", "none"));
        Assert.Null(StatusProbe.ParseGpu(null, "none"));
    }

    [Fact]
    public void ActiveGroup_is_first_running_services_group_else_none()
    {
        Assert.Equal("none", StatusProbe.ActiveGroup(new List<ServiceStatus>()));
        var svcs = new List<ServiceStatus>
        {
            new() { Name = "llama-swap", Group = "llm", Running = false },
            new() { Name = "media", Group = "media", Running = true },
        };
        Assert.Equal("media", StatusProbe.ActiveGroup(svcs));
    }

    [Fact]
    public void ParsePortOwners_extracts_listening_pids_for_the_port_only()
    {
        var netstat = string.Join("\n", new[]
        {
            "Active Connections",
            "  Proto  Local Address          Foreign Address        State           PID",
            "  TCP    127.0.0.1:8080         0.0.0.0:0              LISTENING       1234",
            "  TCP    127.0.0.1:8080         127.0.0.1:55000       ESTABLISHED     1234",  // not LISTENING
            "  TCP    0.0.0.0:7801           0.0.0.0:0             LISTENING       9999",   // different port
            "  TCP    [::]:8080              [::]:0                LISTENING       5678",    // ipv6, same port
        });
        var owners = Lifecycle.ParsePortOwners(netstat, 8080);
        Assert.Contains(1234, owners);
        Assert.Contains(5678, owners);
        Assert.DoesNotContain(9999, owners);
        Assert.Equal(2, owners.Count);
    }
}
