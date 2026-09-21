namespace HatidSuki.Domain;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
}

/// <summary>Marks data owned by exactly one workspace (tenant). Filtered globally by EF Core.</summary>
public interface ITenantEntity
{
    Guid WorkspaceId { get; }
}

/// <summary>A business rule was violated. Mapped to HTTP 409/400 by the API.</summary>
public class DomainException(string message) : Exception(message);

public enum Role { Owner, Manager, Staff }
public enum FormStatus { Draft, Published, Closed }
public enum OrderStatus { New, Ready, Served, Cancelled }
public enum OrderChannel { Public, Staff }
public enum PaymentStatus { Unpaid, PartlyPaid, Paid }
