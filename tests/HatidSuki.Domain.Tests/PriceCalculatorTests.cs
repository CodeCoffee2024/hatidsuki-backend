using FluentAssertions;
using HatidSuki.Domain;
using HatidSuki.Domain.Items;

namespace HatidSuki.Domain.Tests;

public class PriceCalculatorTests
{
    private static ItemOption Opt(string name, decimal delta, bool available = true, bool isDefault = false) =>
        new() { Name = name, PriceDelta = delta, IsAvailable = available, IsDefault = isDefault };

    private static ItemOptionGroup Single(string name, bool required, params ItemOption[] options) =>
        new() { Name = name, SelectionType = SelectionTypes.Single, Required = required, Options = options.ToList() };

    private static ItemOptionGroup Multiple(string name, bool required, int? min, int? max, params ItemOption[] options) =>
        new() { Name = name, SelectionType = SelectionTypes.Multiple, Required = required, MinSelect = min, MaxSelect = max, Options = options.ToList() };

    [Fact]
    public void Unit_price_is_the_base_price_plus_selected_deltas()
    {
        var large = Opt("Large", 20m);
        var size = Single("Size", required: true, Opt("Small", 0m), large);
        var extras = Multiple("Extras", required: false, null, null, Opt("Cheese", 15m), Opt("Bacon", 25m));

        var (unitPrice, selected) = PriceCalculator.Calculate(80m,
            [size, extras],
            new Dictionary<Guid, List<Guid>> { [size.Id] = [large.Id], [extras.Id] = [extras.Options[0].Id, extras.Options[1].Id] });

        unitPrice.Should().Be(80m + 20m + 15m + 25m);
        selected.Should().HaveCount(3);
        selected.Should().Contain(s => s.OptionName == "Large" && s.PriceDelta == 20m);
    }

    [Fact]
    public void A_negative_delta_can_reduce_the_price_as_long_as_it_stays_non_negative()
    {
        var small = Opt("Small", -10m);
        var size = Single("Size", required: true, small, Opt("Regular", 0m));

        var (unitPrice, _) = PriceCalculator.Calculate(20m, [size], new Dictionary<Guid, List<Guid>> { [size.Id] = [small.Id] });
        unitPrice.Should().Be(10m);
    }

    [Fact]
    public void A_combination_that_would_go_negative_is_rejected()
    {
        var small = Opt("Small", -30m);
        var size = Single("Size", required: true, small);

        FluentActions.Invoking(() => PriceCalculator.Calculate(20m, [size], new Dictionary<Guid, List<Guid>> { [size.Id] = [small.Id] }))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void A_required_single_group_needs_a_selection()
    {
        var size = Single("Size", required: true, Opt("Small", 0m), Opt("Large", 10m));
        FluentActions.Invoking(() => PriceCalculator.Calculate(50m, [size], new Dictionary<Guid, List<Guid>>()))
            .Should().Throw<DomainException>().WithMessage("*Size*");
    }

    [Fact]
    public void A_single_group_rejects_more_than_one_choice()
    {
        var size = Single("Size", required: false, Opt("Small", 0m), Opt("Large", 10m));
        FluentActions.Invoking(() => PriceCalculator.Calculate(50m, [size],
                new Dictionary<Guid, List<Guid>> { [size.Id] = [size.Options[0].Id, size.Options[1].Id] }))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void A_multiple_group_enforces_min_and_max()
    {
        var extras = Multiple("Extras", required: false, min: 1, max: 2, Opt("A", 1m), Opt("B", 1m), Opt("C", 1m));

        FluentActions.Invoking(() => PriceCalculator.Calculate(10m, [extras], new Dictionary<Guid, List<Guid>> { [extras.Id] = [] }))
            .Should().Throw<DomainException>().WithMessage("*at least*");

        FluentActions.Invoking(() => PriceCalculator.Calculate(10m, [extras],
                new Dictionary<Guid, List<Guid>> { [extras.Id] = extras.Options.Select(o => o.Id).ToList() }))
            .Should().Throw<DomainException>().WithMessage("*at most*");
    }

    [Fact]
    public void An_unavailable_option_cannot_be_chosen()
    {
        var soldOut = Opt("Oat milk", 5m, available: false);
        var milk = Single("Milk", required: false, soldOut, Opt("Whole", 0m));
        FluentActions.Invoking(() => PriceCalculator.Calculate(50m, [milk], new Dictionary<Guid, List<Guid>> { [milk.Id] = [soldOut.Id] }))
            .Should().Throw<DomainException>().WithMessage("*sold out*");
    }

    [Fact]
    public void Choosing_an_option_id_that_does_not_exist_is_rejected()
    {
        var size = Single("Size", required: false, Opt("Small", 0m));
        FluentActions.Invoking(() => PriceCalculator.Calculate(50m, [size], new Dictionary<Guid, List<Guid>> { [size.Id] = [Guid.NewGuid()] }))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Default_price_ignores_required_ness_and_never_goes_negative()
    {
        var opt = Opt("Large", -999m, isDefault: true);
        var size = Single("Size", required: true, opt);
        PriceCalculator.DefaultPrice(10m, [size]).Should().Be(0m);
    }
}

public class ItemOptionGroupValidatorTests
{
    private static ItemOption Opt(string name, bool available = true) => new() { Name = name, IsAvailable = available };

    [Fact]
    public void Rejects_more_than_ten_groups()
    {
        var groups = Enumerable.Range(0, 11).Select(i => new ItemOptionGroup { Name = $"G{i}", Options = [Opt("A")] }).ToList();
        FluentActions.Invoking(() => ItemOptionGroupValidator.Validate(groups)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Rejects_duplicate_option_names_within_a_group()
    {
        var group = new ItemOptionGroup { Name = "Size", Options = [Opt("Large"), Opt("large")] };
        FluentActions.Invoking(() => ItemOptionGroupValidator.Validate([group])).Should().Throw<DomainException>();
    }

    [Fact]
    public void Rejects_a_multiple_group_where_the_maximum_is_less_than_the_minimum()
    {
        var group = new ItemOptionGroup
        {
            Name = "Extras", SelectionType = SelectionTypes.Multiple, MinSelect = 3, MaxSelect = 1,
            Options = [Opt("A"), Opt("B"), Opt("C")]
        };
        FluentActions.Invoking(() => ItemOptionGroupValidator.Validate([group])).Should().Throw<DomainException>();
    }

    [Fact]
    public void Rejects_a_maximum_larger_than_the_number_of_options()
    {
        var group = new ItemOptionGroup { Name = "Extras", SelectionType = SelectionTypes.Multiple, MaxSelect = 5, Options = [Opt("A"), Opt("B")] };
        FluentActions.Invoking(() => ItemOptionGroupValidator.Validate([group])).Should().Throw<DomainException>();
    }

    [Fact]
    public void Rejects_a_required_group_with_no_available_options()
    {
        var group = new ItemOptionGroup { Name = "Size", Required = true, Options = [Opt("Small", available: false)] };
        FluentActions.Invoking(() => ItemOptionGroupValidator.Validate([group])).Should().Throw<DomainException>();
    }

    [Fact]
    public void Accepts_a_well_formed_set_of_groups()
    {
        var groups = new List<ItemOptionGroup>
        {
            new() { Name = "Size", Required = true, Options = [Opt("Small"), Opt("Large")] },
            new()
            {
                Name = "Extras", SelectionType = SelectionTypes.Multiple, MinSelect = 0, MaxSelect = 2,
                Options = [Opt("Cheese"), Opt("Bacon"), Opt("Egg")]
            }
        };
        FluentActions.Invoking(() => ItemOptionGroupValidator.Validate(groups)).Should().NotThrow();
    }
}
