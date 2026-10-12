using AICOS.BuildingBlocks.Hosting;

namespace AICOS.BuildingBlocks.Tests;

public class ExtractTokenTests
{
    [Theory]
    [InlineData("Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("Bearer Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("Bearer \"abc.def.ghi\"", "abc.def.ghi")]
    [InlineData("  Bearer   abc.def.ghi  ", "abc.def.ghi")]
    public void Extracts_the_raw_token(string header, string expected) =>
        AuthenticationExtensions.ExtractToken(header).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    public void Returns_null_when_there_is_no_bearer_token(string? header) =>
        AuthenticationExtensions.ExtractToken(header).Should().BeNull();
}
