using FluentAssertions;
using HatidSuki.Application.Orders;

namespace HatidSuki.Application.Tests;

public class CsvSanitizerTests
{
    [Theory]
    [InlineData("=cmd|'/c calc'!A1", "'=cmd|'/c calc'!A1")]
    [InlineData("+1-234-555", "'+1-234-555")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tsneaky", "'\tsneaky")]
    public void Neutralizes_formula_injection_attempts(string input, string expected) =>
        CsvSanitizer.Cell(input).Should().Be(expected);

    [Theory]
    [InlineData("Ana's Bakery")]
    [InlineData("Table 5")]
    [InlineData("100.50")]
    public void Leaves_ordinary_text_alone(string input) => CsvSanitizer.Cell(input).Should().Be(input);

    [Fact]
    public void Quotes_a_cell_containing_a_comma()
    {
        CsvSanitizer.Cell("Ana, Ben +2").Should().Be("\"Ana, Ben +2\"");
    }

    [Fact]
    public void Doubles_embedded_quotes_and_wraps_the_cell()
    {
        CsvSanitizer.Cell("She said \"hi\"").Should().Be("\"She said \"\"hi\"\"\"");
    }

    [Fact]
    public void A_null_value_becomes_an_empty_cell()
    {
        CsvSanitizer.Cell(null).Should().Be("");
    }
}
