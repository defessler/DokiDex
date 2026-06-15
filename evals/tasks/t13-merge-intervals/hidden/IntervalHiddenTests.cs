using App;
using Xunit;

// Hidden tests (never seen by the agent): a fully-contained range must keep the WIDER end (not the inner
// one — catches an impl that uses next.end instead of max), a one-unit GAP must stay separate, and
// unsorted/chained inputs must still merge correctly. The visible cases all have next.end >= current.end,
// so they can't distinguish max() from next.end — that's exactly what these enforce.
public class IntervalHiddenTests
{
    [Theory]
    [InlineData("1-10,2-3,4-5", "1-10")]            // fully contained -> keep the wider end
    [InlineData("1-2,3-4", "1-2,3-4")]              // gap (2 and 3 don't touch) -> stay separate
    [InlineData("9-12,1-3,3-5,11-15", "1-5,9-15")]  // unsorted + chained merges
    [InlineData("2-3,1-4", "1-4")]                  // second contains first, unsorted
    [InlineData("1-1", "1-1")]                      // single point
    public void Merge_handles_containment_gaps_and_order(string input, string expected)
        => Assert.Equal(expected, IntervalMerger.Merge(input));
}
