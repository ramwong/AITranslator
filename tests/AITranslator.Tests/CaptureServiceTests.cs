using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

public class CaptureServiceTests
{
    [Fact]
    public void SequenceUnchanged_ReturnsNoSelection()
    {
        var outcome = CaptureService.Decide(42u, 42u, "hello", 5000);

        Assert.Equal(CaptureOutcome.NoSelection, outcome);
    }

    [Fact]
    public void NullText_ReturnsNoSelection()
    {
        var outcome = CaptureService.Decide(42u, 43u, null, 5000);

        Assert.Equal(CaptureOutcome.NoSelection, outcome);
    }

    [Fact]
    public void EmptyText_ReturnsNoSelection()
    {
        var outcome = CaptureService.Decide(42u, 43u, string.Empty, 5000);

        Assert.Equal(CaptureOutcome.NoSelection, outcome);
    }

    [Fact]
    public void TextOverMax_ReturnsTooLong()
    {
        var outcome = CaptureService.Decide(42u, 43u, new string('a', 11), 10);

        Assert.Equal(CaptureOutcome.TooLong, outcome);
    }

    [Fact]
    public void NormalText_ReturnsSuccess()
    {
        var outcome = CaptureService.Decide(42u, 43u, "hello", 5000);

        Assert.Equal(CaptureOutcome.Success, outcome);
    }
}