namespace HatidSuki.Domain;

/// <summary>
/// A place the business delivers to (a building lobby, a table, a stall). Every order form asks the customer to choose one.
/// Locations are hidden rather than deleted so past orders keep a meaningful name.
/// </summary>
public class DeliveryLocation : Entity, ITenantEntity
{
    private DeliveryLocation() { }

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>Shown to the customer under the name, e.g. "Ground floor, next to the guard".</summary>
    public string? Note { get; private set; }
    public bool IsActive { get; private set; } = true;
    public int SortOrder { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static DeliveryLocation Create(Guid workspaceId, string name, string? note, int sortOrder, DateTime now)
    {
        var location = new DeliveryLocation { WorkspaceId = workspaceId, SortOrder = sortOrder, CreatedAtUtc = now };
        location.Update(name, note);
        return location;
    }

    public void Update(string name, string? note)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A delivery location needs a name.");
        if (name.Trim().Length > 80) throw new DomainException("Location names can be up to 80 characters.");
        Name = name.Trim();
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void SetActive(bool active) => IsActive = active;
}
