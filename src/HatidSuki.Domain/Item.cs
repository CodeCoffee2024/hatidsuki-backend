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
}
