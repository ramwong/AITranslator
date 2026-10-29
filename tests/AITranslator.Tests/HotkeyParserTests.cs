using AITranslator.Services;
using Xunit;

namespace AITranslator.Tests;

public class HotkeyParserTests
{
    [Fact]
    public void CtrlAltE_ReturnsControlAltAndKeyE()
    {
        var (success, mods, key) = HotkeyParser.Parse("Ctrl+Alt+E");

        Assert.True(success);
        Assert.Equal(0x2u | 0x1u, mods); // MOD_CONTROL | MOD_ALT
        Assert.Equal(0x45u, key);       // 'E'
    }

    [Fact]
    public void CtrlAltZ_ReturnsControlAltAndKeyZ()
    {
        var (success, mods, key) = HotkeyParser.Parse("Ctrl+Alt+Z");

        Assert.True(success);
        Assert.Equal(0x2u | 0x1u, mods);
        Assert.Equal(0x5Au, key);       // 'Z'
    }

    [Fact]
    public void ShiftF5_ReturnsShiftAndF5()
    {
        var (success, mods, key) = HotkeyParser.Parse("Shift+F5");

        Assert.True(success);
        Assert.Equal(0x4u, mods);       // MOD_SHIFT
        Assert.Equal(0x74u, key);       // F5 = 0x70 + 5 - 1
    }

    [Fact]
    public void AllThreeModifiers_AreCombined()
    {
        var (success, mods, key) = HotkeyParser.Parse("Ctrl+Alt+Shift+F1");

        Assert.True(success);
        Assert.Equal(0x2u | 0x1u | 0x4u, mods);
        Assert.Equal(0x70u, key);       // F1
    }

    [Fact]
    public void LowercaseTokens_AreAccepted()
    {
        var (success, mods, key) = HotkeyParser.Parse("ctrl+alt+e");

        Assert.True(success);
        Assert.Equal(0x2u | 0x1u, mods);
        Assert.Equal(0x45u, key);
    }

    [Fact]
    public void MixedCaseModifiers_WithLowercaseLetter_AreAccepted()
    {
        var (success, mods, key) = HotkeyParser.Parse("sHiFt+a");

        Assert.True(success);
        Assert.Equal(0x4u, mods);
        Assert.Equal(0x41u, key);       // 'A'
    }

    [Fact]
    public void UnknownModifier_Fails()
    {
        var (success, _, _) = HotkeyParser.Parse("Invalid+Combo");

        Assert.False(success);
    }

    [Fact]
    public void EmptyString_Fails()
    {
        var (success, _, _) = HotkeyParser.Parse("");

        Assert.False(success);
    }

    [Fact]
    public void Null_Fails()
    {
        var (success, _, _) = HotkeyParser.Parse(null!);

        Assert.False(success);
    }

    [Fact]
    public void SpaceSeparators_Fail()
    {
        var (success, _, _) = HotkeyParser.Parse("Ctrl Alt E");

        Assert.False(success);
    }

    [Fact]
    public void NoModifiers_Fails()
    {
        var (success, _, _) = HotkeyParser.Parse("E");

        Assert.False(success);
    }

    [Fact]
    public void MissingKey_Fails()
    {
        var (success, _, _) = HotkeyParser.Parse("Ctrl+Alt+");

        Assert.False(success);
    }

    [Fact]
    public void ModifierAsFinalToken_Fails()
    {
        var (success, _, _) = HotkeyParser.Parse("Ctrl+Alt");

        Assert.False(success);
    }

    [Fact]
    public void MultiCharacterKey_Fails()
    {
        var (success, _, _) = HotkeyParser.Parse("Ctrl+Alt+Abc");

        Assert.False(success);
    }

    [Fact]
    public void NonLetterSingleChar_Fails()
    {
        var (success, _, _) = HotkeyParser.Parse("Ctrl+1");

        Assert.False(success);
    }

    [Fact]
    public void OutOfRangeFunctionKey_Fails()
    {
        Assert.False(HotkeyParser.Parse("Ctrl+F13").success);
        Assert.False(HotkeyParser.Parse("Ctrl+F0").success);
    }

    [Fact]
    public void F12_IsParsed()
    {
        var (success, mods, key) = HotkeyParser.Parse("Ctrl+F12");

        Assert.True(success);
        Assert.Equal(0x2u, mods);
        Assert.Equal(0x7Bu, key);       // F12 = 0x70 + 12 - 1
    }
}