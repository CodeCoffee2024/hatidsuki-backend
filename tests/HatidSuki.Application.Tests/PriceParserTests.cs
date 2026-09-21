using FluentAssertions;
using HatidSuki.Application.Items;

namespace HatidSuki.Application.Tests;

public class PriceParserTests
{
    [Theory]
    [InlineData("12", 12)]
    [InlineData("12.50", 12.5)]
    [InlineData("12,50", 12.5)]          // decimal comma
    [InlineData("1,200", 1200)]          // thousands comma
    [InlineData("1,200.50", 1200.5)]
    [InlineData("1.200,50", 1200.5)]     // European style
    [InlineData("$ 45", 45)]
    [InlineData("₱45.00", 45)]
    [InlineData("  60  ", 60)]
    public void Reads_the_prices_people_actually_type(string text, double expected)
    {
        PriceParser.TryParse(text, out var price).Should().BeTrue();
        price.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("free")]
    public void Rejects_text_that_is_not_a_price(string? text) =>
        PriceParser.TryParse(text, out _).Should().BeFalse();
}
