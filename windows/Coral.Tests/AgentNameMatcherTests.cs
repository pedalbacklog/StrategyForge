using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the AgentNameMatcher tests — no direct Swift test file
/// existed to mirror, so these are written from the documented behavior of
/// titlesMatch(_:_:).</summary>
public class AgentNameMatcherTests
{
    [Theory]
    [InlineData("worker", "worker", true)]
    [InlineData("worker-1", "worker", true)]     // streamed subagent_type slug vs. role name
    [InlineData("worker", "worker-1", true)]     // either direction
    [InlineData("Code Reviewer", "code-reviewer", true)] // case + punctuation ignored
    [InlineData("worker", "reviewer", false)]
    [InlineData("", "worker", false)]
    [InlineData("worker", "", false)]
    public void TitlesMatch(string a, string b, bool expected) =>
        Assert.Equal(expected, AgentNameMatcher.TitlesMatch(a, b));
}
