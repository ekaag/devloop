namespace TextTools.Tests;

public sealed class TextToolsTests
{
    [Fact]
    public void ReverseReturnsCharactersInReverseOrder() => Assert.Equal("cba", TextTools.Reverse("abc"));

    [Fact]
    public void ReverseRejectsNull() => Assert.Throws<ArgumentNullException>(() => TextTools.Reverse(null!));
}
