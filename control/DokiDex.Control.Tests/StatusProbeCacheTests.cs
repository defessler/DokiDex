using DokiDex.Control.Models;
using DokiDex.Control.Services;
using Xunit;

namespace DokiDex.Control.Tests;

// Pins that the 1-second TTL cache on DokiService.GetStatusAsync collapses rapid back-to-back calls
// to a single StatusProbe invocation (the slow nvidia-smi + HTTP path). Clock and probe are injectable
// internal seams; the test never touches the filesystem, GPU, or network.
public class StatusProbeCacheTests
{
    [Fact]
    public async Task Second_call_within_TTL_reuses_cached_result_without_re_probing()
    {
        var callCount = 0;
        var fakeDoc = new StatusDoc();
        var t0 = DateTime.UtcNow;

        var svc = new DokiService();
        svc._hasRoot = () => true;           // bypass filesystem check
        svc._now    = () => t0;              // frozen clock: 0 ms elapsed across both calls
        svc._probe  = _ => { callCount++; return Task.FromResult(fakeDoc); };

        await svc.GetStatusAsync();          // first call: primes the cache (callCount = 1)
        await svc.GetStatusAsync();          // second call: frozen clock → still within TTL → cache hit

        Assert.Equal(1, callCount);
    }
}
