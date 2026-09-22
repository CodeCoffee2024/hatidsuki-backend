using HatidSuki.Domain.Items;

namespace HatidSuki.Domain;

/// <summary>A sellable entry in the business's catalog ("the item list").</summary>
public class Item : Entity, ITenantEntity
{
    private Item() { }

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public decimal Price { get; private set; }
    public string? Category { get; private set; }
    public string Unit { get; private set; } = "each";
    public bool IsAvailable { get; private set; } = true;   // manual "sold out" switch
    public bool IsArchived { get; private set; }
    public int SortOrder { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Sizes, flavors, add-ons, etc. (FS-008). Stored as JSON: small, bounded, and always replaced whole.</summary>
    public string OptionsJson { get; private set; } = "[]";

    /// <summary>Deserialized view of <see cref="OptionsJson"/>. Not mapped by EF; ignore it in configuration.</summary>
    public List<ItemOptionGroup> OptionGroups => ItemOptionsJson.Deserialize(OptionsJson);

    public static Item Create(Guid workspaceId, string name, decimal price, string? category, string? description,
        string? unit, int sortOrder, DateTime now)
    {
        var item = new Item { WorkspaceId = workspaceId, SortOrder = sortOrder, CreatedAtUtc = now };
        item.Update(name, price, category, description, unit);
        return item;
    }

    public void Update(string name, decimal price, string? category, string? description, string? unit)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("An item needs a name.");
        if (price < 0) throw new DomainException("Price can't be negative.");
        Name = name.Trim();
        Price = decimal.Round(price, 2);
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Unit = string.IsNullOrWhiteSpace(unit) ? "each" : unit.Trim();
    }

    public void SetAvailability(bool available) => IsAvailable = available;
    public void SetArchived(bool archived) => IsArchived = archived;

    /// <summary>Can this item be put in a customer's order right now?</summary>
    public bool IsOrderable => IsAvailable && !IsArchived;

    /// <summary>Replaces the whole option-group set (the editor always saves the section as one unit).</summary>
    public void SetOptionGroups(List<ItemOptionGroup> groups)
    {
        ItemOptionGroupValidator.Validate(groups);
        OptionsJson = ItemOptionsJson.Serialize(groups);
    }

    /// <summary>Copies another item's option groups onto this one, with fresh ids (so editing one never edits the other).</summary>
    public void CopyOptionGroupsFrom(Item source)
    {
        var copied = source.OptionGroups.Select(g => new ItemOptionGroup
        {
            Id = Guid.NewGuid(), Name = g.Name, SelectionType = g.SelectionType, Required = g.Required,
            MinSelect = g.MinSelect, MaxSelect = g.MaxSelect, SortOrder = g.SortOrder,
            Options = g.Options.Select(o => new ItemOption
            {
                Id = Guid.NewGuid(), Name = o.Name, PriceDelta = o.PriceDelta, IsAvailable = o.IsAvailable,
                IsDefault = o.IsDefault, SortOrder = o.SortOrder
            }).ToList()
        }).ToList();
        SetOptionGroups(copied);
    }

    /// <summary>Marks one option available/sold-out without touching the rest of the item's options.</summary>
    public void SetOptionAvailability(Guid groupId, Guid optionId, bool available)
    {
        var groups = OptionGroups;
        var group = groups.FirstOrDefault(g => g.Id == groupId) ?? throw new DomainException("That option group no longer exists.");
        var option = group.Options.FirstOrDefault(o => o.Id == optionId) ?? throw new DomainException("That option no longer exists.");
        option.IsAvailable = available;
        SetOptionGroups(groups);
    }

    /// <summary>Unit price for a line, given the customer's selected options (group id → chosen option ids).</summary>
    public (decimal UnitPrice, List<SelectedOptionSnapshot> Selected) PriceFor(IReadOnlyDictionary<Guid, List<Guid>> selections) =>
        PriceCalculator.Calculate(Price, OptionGroups, selections);
}
