using VPS3Drive.Core;
using Xunit;

namespace VPS3Drive.Core.Tests;

public sealed class ObjectPathMapperTests
{
    [Theory]
    [InlineData("folder\\photo.jpg", "root/folder/photo.jpg")]
    [InlineData("/folder/photo.jpg/", "root/folder/photo.jpg")]
    [InlineData("", "root/")]
    public void ToObjectKey_NormalizesSeparatorsAndAppliesPrefix(string input, string expected)
    {
        var mapper = new ObjectPathMapper("/root/");
        Assert.Equal(expected, mapper.ToObjectKey(input));
    }

    [Fact]
    public void ToObjectKey_RejectsTraversalSegments()
    {
        var mapper = new ObjectPathMapper("root");
        Assert.Throws<ArgumentException>(() => mapper.ToObjectKey("folder/../secret"));
    }

    [Fact]
    public void ToRelativePath_RejectsKeysOutsidePrefix()
    {
        var mapper = new ObjectPathMapper("root");
        Assert.Throws<ArgumentException>(() => mapper.ToRelativePath("outside/file.txt"));
    }

    [Fact]
    public void ToRelativePath_RemovesRootPrefix()
    {
        var mapper = new ObjectPathMapper("root");
        Assert.Equal("folder/file.txt", mapper.ToRelativePath("root/folder/file.txt"));
    }
}
