using System.Text.Json;
using System.Text.Json.Serialization;

namespace HatidSuki.Domain.Items;

public static class SelectionTypes
{
    public const string Single = "single", Multiple = "multiple";
    public static readonly HashSet<string> All = [Single, Multiple];
}

/// <summary>One choice inside an <see cref="ItemOptionGroup"/>, e.g. "Large" under "Size".</summary>
public class ItemOption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Added to the item's base price when this option is chosen. May be 0 or negative.</summary>
    public decimal PriceDelta { get; set; }
    public bool IsAvailable { get; set; } = true;
    /// <summary>Pre-selected in the customer-facing picker; purely a UX convenience.</summary>
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>A group of choices on an item, e.g. "Size" (single) or "Extras" (multiple).</summary>
public class ItemOptionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string SelectionType { get; set; } = SelectionTypes.Single;
    public bool Required { get; set; }
    /// <summary>Only meaningful when <see cref="SelectionType"/> is "multiple".</summary>
    public int? MinSelect { get; set; }
    public int? MaxSelect { get; set; }
    public int SortOrder { get; set; }
    public List<ItemOption> Options { get; set; } = [];
}

/// <summary>What ended up selected on an order line, snapshotted so later catalog edits never rewrite history.</summary>
public record SelectedOptionSnapshot(string GroupName, string OptionName, decimal PriceDelta);

/// <summary>Blocks impossible option configurations before they can be saved (FS-008 AC5).</summary>
public static class ItemOptionGroupValidator
{
    public const int MaxGroups = 10;
    public const int MaxOptionsPerGroup = 30;

    public static void Validate(List<ItemOptionGroup> groups)
    {
        if (groups.Count > MaxGroups) throw new DomainException($"An item can have at most {MaxGroups} option groups.");
        var groupNames = new HashSet<string>();
        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Name)) throw new DomainException("Every option group needs a name.");
            if (!groupNames.Add(g.Name.Trim().ToLowerInvariant())) throw new DomainException("Option group names must be unique.");
            if (!SelectionTypes.All.Contains(g.SelectionType)) throw new DomainException($"'{g.Name}' has an unknown selection type.");
            if (g.Options.Count > MaxOptionsPerGroup) throw new DomainException($"'{g.Name}' can have at most {MaxOptionsPerGroup} options.");

            var optionNames = new HashSet<string>();
            foreach (var o in g.Options)
            {
                if (string.IsNullOrWhiteSpace(o.Name)) throw new DomainException($"Every option in '{g.Name}' needs a name.");
                if (!optionNames.Add(o.Name.Trim().ToLowerInvariant())) throw new DomainException($"Option names in '{g.Name}' must be unique.");
            }

            if (g.SelectionType == SelectionTypes.Multiple)
            {
                var min = g.MinSelect ?? 0;
                var max = g.MaxSelect ?? g.Options.Count;
                if (min < 0) throw new DomainException($"'{g.Name}': the minimum can't be negative.");
                if (max < min) throw new DomainException($"'{g.Name}': the maximum can't be less than the minimum.");
                if (max > g.Options.Count) throw new DomainException($"'{g.Name}': the maximum can't exceed the number of options.");
            }
            if (g.Required && g.Options.Count(o => o.IsAvailable) == 0)
                throw new DomainException($"'{g.Name}' is required but has no available options.");
        }
    }
}

/// <summary>Computes a line's unit price from an item's base price plus the customer's selected options.</summary>
public static class PriceCalculator
{
    /// <param name="selections">Group id → the option ids chosen from it.</param>
    public static (decimal UnitPrice, List<SelectedOptionSnapshot> Selected) Calculate(
        decimal basePrice, IReadOnlyList<ItemOptionGroup> groups, IReadOnlyDictionary<Guid, List<Guid>> selections)
    {
        var selected = new List<SelectedOptionSnapshot>();
        var price = basePrice;
        foreach (var g in groups)
        {
            var chosenIds = selections.TryGetValue(g.Id, out var ids) ? ids.Distinct().ToList() : [];
            var chosenOptions = g.Options.Where(o => chosenIds.Contains(o.Id)).ToList();
            if (chosenOptions.Count != chosenIds.Count)
                throw new DomainException($"One of the options in '{g.Name}' isn't available any more.");
            if (chosenOptions.Any(o => !o.IsAvailable))
                throw new DomainException($"An option in '{g.Name}' is sold out.");

            if (g.SelectionType == SelectionTypes.Single)
            {
                if (chosenOptions.Count > 1) throw new DomainException($"Choose only one option for '{g.Name}'.");
                if (g.Required && chosenOptions.Count == 0) throw new DomainException($"Choose an option for '{g.Name}'.");
            }
            else
            {
                var min = g.Required ? Math.Max(1, g.MinSelect ?? 1) : g.MinSelect ?? 0;
                var max = g.MaxSelect ?? g.Options.Count;
                if (chosenOptions.Count < min) throw new DomainException($"Choose at least {min} option{(min == 1 ? "" : "s")} for '{g.Name}'.");
                if (chosenOptions.Count > max) throw new DomainException($"Choose at most {max} option{(max == 1 ? "" : "s")} for '{g.Name}'.");
            }

            foreach (var o in chosenOptions)
            {
                price += o.PriceDelta;
                selected.Add(new SelectedOptionSnapshot(g.Name, o.Name, o.PriceDelta));
            }
        }
        if (price < 0) throw new DomainException("This combination can't have a negative price.");
        return (decimal.Round(price, 2), selected);
    }

    /// <summary>A best-effort price for the "default selection" preview shown in the item editor. Never throws.</summary>
    public static decimal DefaultPrice(decimal basePrice, IReadOnlyList<ItemOptionGroup> groups)
    {
        var price = basePrice + groups.SelectMany(g => g.Options.Where(o => o.IsDefault && o.IsAvailable)).Sum(o => o.PriceDelta);
        return Math.Max(0, decimal.Round(price, 2));
    }
}

internal static class ItemOptionsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(List<ItemOptionGroup> groups) => JsonSerializer.Serialize(groups, Options);
    public static List<ItemOptionGroup> Deserialize(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<ItemOptionGroup>>(json, Options) ?? [];

    public static string SerializeSelected(List<SelectedOptionSnapshot> selected) => JsonSerializer.Serialize(selected, Options);
    public static List<SelectedOptionSnapshot> DeserializeSelected(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<SelectedOptionSnapshot>>(json, Options) ?? [];
}
