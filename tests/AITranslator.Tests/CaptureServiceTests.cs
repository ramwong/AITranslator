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

    [Fact]
    public void AnyModifierPressed_CtrlHeld_ReturnsTrue()
    {
        // 模擬：Ctrl 實體壓著（高字元 0x8000）→ 應判為有修飾鍵壓著
        Assert.True(CaptureService.AnyModifierPressed(
            vk => vk == 0x11 ? unchecked((short)0x8000) : (short)0));
    }

    [Fact]
    public void AnyModifierPressed_AllReleased_ReturnsFalse()
    {
        Assert.False(CaptureService.AnyModifierPressed(_ => 0));
    }

    [Fact]
    public void AnyModifierPressed_AltHeld_ReturnsTrue()
    {
        // 模擬：只有 Alt 壓著（重按 hotkey 當下的常態）→ 應判為有
        Assert.True(CaptureService.AnyModifierPressed(
            vk => vk == 0x12 ? unchecked((short)0x8000) : (short)0));
    }

    [Fact]
    public void AnyModifierPressed_ToggleBitOnly_ReturnsFalse()
    {
        // 切換狀態位元（低字元 0x0001，如 CapsLock toggle）不是「壓著」
        Assert.False(CaptureService.AnyModifierPressed(
            vk => vk == 0x11 ? (short)0x0001 : (short)0));
    }
}