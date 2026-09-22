using System.Security.Cryptography;
using HatidSuki.Domain.Items;

namespace HatidSuki.Domain.Orders;

public record LineDraft(Guid ItemId, string ItemName, decimal UnitPrice, int Quantity, string? Note,
    IReadOnlyList<SelectedOptionSnapshot>? Options = null);
public record PartDraft(string PersonLabel, string? Note, IReadOnlyList<LineDraft> Lines);

public class OrderLine : Entity
{
    private OrderLine() { }
    public Guid PartId { get; private set; }
    public Guid ItemId { get; private set; }
    /// <summary>Name and price are snapshotted so later catalog edits never change history.</summary>
    public string ItemName { get; private set; } = "";
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public string? Note { get; private set; }
    /// <summary>The chosen options (name + price delta), snapshotted the same way the item name and price are (FS-008).</summary>
    public string OptionsJson { get; private set; } = "[]";
    public List<SelectedOptionSnapshot> Options => ItemOptionsJson.DeserializeSelected(OptionsJson);
    public decimal LineTotal => UnitPrice * Quantity;

    internal static OrderLine From(LineDraft d)
    {
        if (d.Quantity is < 1 or > 99) throw new DomainException("Quantity must be between 1 and 99.");
        return new OrderLine
        {
            ItemId = d.ItemId, ItemName = d.ItemName, UnitPrice = d.UnitPrice, Quantity = d.Quantity, Note = d.Note,
            OptionsJson = ItemOptionsJson.SerializeSelected((d.Options ?? []).ToList())
        };
    }
}

/// <summary>One person's portion of an order. A normal order has one part; a group order has several.</summary>
public class OrderPart : Entity
{
    private OrderPart() { }
    private readonly List<OrderLine> _lines = [];

    public Guid OrderId { get; private set; }
    public string PersonLabel { get; private set; } = "";
    public string? Note { get; private set; }
    public bool IsReady { get; private set; }
    public DateTime? ReadyAtUtc { get; private set; }
    public DateTime? PaidAtUtc { get; private set; }
    public string? PaidBy { get; private set; }
    public bool IsCancelled { get; private set; }
    public string? CancelReason { get; private set; }
    public bool IsLateAddition { get; private set; }
    public int SortOrder { get; private set; }
    public IReadOnlyList<OrderLine> Lines => _lines;

    public decimal Subtotal => _lines.Sum(l => l.LineTotal);
    public bool IsPaid => PaidAtUtc is not null;

    internal static OrderPart From(PartDraft d, int sortOrder, bool late)
    {
        if (d.Lines.Count == 0) throw new DomainException("Each person needs at least one item.");
        var part = new OrderPart { PersonLabel = d.PersonLabel.Trim(), Note = d.Note, SortOrder = sortOrder, IsLateAddition = late };
        foreach (var l in d.Lines) part._lines.Add(OrderLine.From(l));
        return part;
    }

    internal void SetReady(bool ready, DateTime now) { IsReady = ready; ReadyAtUtc = ready ? now : null; }
    internal void SetPaid(bool paid, DateTime now, string? by) { PaidAtUtc = paid ? now : null; PaidBy = paid ? by : null; }
    internal void Cancel(string reason) { IsCancelled = true; CancelReason = reason; }
}

public class OrderEvent : Entity
{
    private OrderEvent() { }
    public Guid OrderId { get; private set; }
    public DateTime AtUtc { get; private set; }
    public string Type { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string? By { get; private set; }

    internal static OrderEvent Create(string type, string message, DateTime now, string? by) =>
        new() { Type = type, Message = message, AtUtc = now, By = by };
}

/// <summary>
/// One customer submission. Holds one or more <see cref="OrderPart"/>s (people). Status is derived from the parts:
/// New until every active person is checked ready, then Ready; Served and Cancelled are final.
/// </summary>
public class Order : Entity, ITenantEntity
{
    private Order() { }
    private readonly List<OrderPart> _parts = [];
    private readonly List<OrderEvent> _events = [];

    public Guid WorkspaceId { get; private set; }
    public Guid FormId { get; private set; }
    public Guid FormVersionId { get; private set; }
    public int Number { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.New;
    public OrderChannel Channel { get; private set; }
    public string? ManualType { get; private set; }
    public string CustomerName { get; private set; } = "";
    public string? CustomerPhone { get; private set; }
    public string? CustomerEmail { get; private set; }
    public string AnswersJson { get; private set; } = "{}";
    public decimal Total { get; private set; }
    public string Currency { get; private set; } = "";
    public string? SourceCode { get; private set; }
    public string? SourceName { get; private set; }
    /// <summary>Where the order goes. The name is copied so renaming a location never rewrites old orders.</summary>
    public Guid? DeliveryLocationId { get; private set; }
    public string? DeliveryLocationName { get; private set; }
    /// <summary>What the customer added about the spot, e.g. "Room 402" or "by the blue door".</summary>
    public string? DeliveryNote { get; private set; }
    public bool IsTest { get; private set; }
    public string TrackingToken { get; private set; } = "";
    public string? IdempotencyKey { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public DateTime? ReadyAtUtc { get; private set; }
    public DateTime? ServedAtUtc { get; private set; }
    /// <summary>Set when staff open or "Seen"-tick a new order. Unacknowledged orders re-alert.</summary>
    public DateTime? AcknowledgedAtUtc { get; private set; }
    public DateTime? NotifiedAtUtc { get; private set; }
    public string? NotifiedChannel { get; private set; }
    public string? CancelReason { get; private set; }

    public IReadOnlyList<OrderPart> Parts => _parts;
    public IReadOnlyList<OrderEvent> Events => _events;

    public IEnumerable<OrderPart> ActiveParts => _parts.Where(p => !p.IsCancelled);
    public int ActiveCount => ActiveParts.Count();
    public int ReadyCount => ActiveParts.Count(p => p.IsReady);
    public bool IsOpen => Status is OrderStatus.New or OrderStatus.Ready;

    public PaymentStatus PaymentStatus
    {
        get
        {
            var active = ActiveParts.ToList();
            if (active.Count == 0 || active.All(p => !p.IsPaid)) return PaymentStatus.Unpaid;
            return active.All(p => p.IsPaid) ? PaymentStatus.Paid : PaymentStatus.PartlyPaid;
        }
    }

    public decimal UnpaidAmount => ActiveParts.Where(p => !p.IsPaid).Sum(p => p.Subtotal);

    public static Order Place(Guid workspaceId, Guid formId, Guid formVersionId, int number, string currency,
        OrderChannel channel, string? manualType, string customerName, string? phone, string? email, string answersJson,
        string? sourceCode, string? sourceName, bool isTest, string? idempotencyKey,
        IEnumerable<PartDraft> parts, DateTime now, string? by)
    {
        var order = new Order
        {
            WorkspaceId = workspaceId, FormId = formId, FormVersionId = formVersionId, Number = number, Currency = currency,
            Channel = channel, ManualType = manualType, CustomerName = customerName.Trim(), CustomerPhone = phone,
            CustomerEmail = email, AnswersJson = answersJson, SourceCode = sourceCode, SourceName = sourceName,
            IsTest = isTest, IdempotencyKey = idempotencyKey, CreatedAtUtc = now, UpdatedAtUtc = now,
            TrackingToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=')
        };
        var list = parts.ToList();
        if (list.Count == 0) throw new DomainException("An order needs at least one person's items.");
        for (var i = 0; i < list.Count; i++) order._parts.Add(OrderPart.From(list[i], i, late: false));
        order.Recalculate();
        // Orders entered by staff are already "seen".
        if (channel == OrderChannel.Staff) order.AcknowledgedAtUtc = now;
        var people = order._parts.Count == 1 ? "person" : "people";
        order.Log("placed", $"Order placed with {order._parts.Count} {people}.", now, by);
        return order;
    }

    public void SetDelivery(Guid? locationId, string? locationName, string? note)
    {
        DeliveryLocationId = locationId;
        DeliveryLocationName = locationName;
        DeliveryNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 200)];
    }

    // ---- per-person "ready" check (the checker) -------------------------------------------------

    public void SetPartReady(Guid partId, bool ready, DateTime now, string? by)
    {
        EnsureOpen();
        var part = FindActivePart(partId);
        if (part.IsReady == ready) return;
        part.SetReady(ready, now);
        Log(ready ? "part-ready" : "part-unready", $"{part.PersonLabel} marked {(ready ? "ready" : "not ready")}.", now, by);
        RefreshStatusFromParts(now, by);
    }

    public void MarkAllReady(DateTime now, string? by)
    {
        EnsureOpen();
        foreach (var p in ActiveParts.Where(p => !p.IsReady)) p.SetReady(true, now);
        Log("all-ready", "All people marked ready.", now, by);
        RefreshStatusFromParts(now, by);
    }

    /// <summary>Handed over / picked up / delivered. Final.</summary>
    public void Serve(DateTime now, string? by)
    {
        if (Status == OrderStatus.Cancelled) throw new DomainException("A cancelled order can't be served.");
        if (Status == OrderStatus.Served) return;
        foreach (var p in ActiveParts.Where(p => !p.IsReady)) p.SetReady(true, now);
        ReadyAtUtc ??= now;
        Status = OrderStatus.Served;
        ServedAtUtc = now;
        AcknowledgedAtUtc ??= now;
        Touch(now);
        Log("status", "Order served.", now, by);
    }

    public void Cancel(string reason, DateTime now, string? by)
    {
        if (Status == OrderStatus.Served) throw new DomainException("A served order can't be cancelled.");
        if (Status == OrderStatus.Cancelled) return;
        Status = OrderStatus.Cancelled;
        CancelReason = reason;
        AcknowledgedAtUtc ??= now;
        Touch(now);
        Log("status", $"Order cancelled: {reason}", now, by);
    }

    // ---- cash payment tags ----------------------------------------------------------------------

    public void TagPartPaid(Guid partId, bool paid, DateTime now, string? by)
    {
        var part = FindActivePart(partId);
        part.SetPaid(paid, now, by);
        Touch(now);
        Log(paid ? "paid" : "unpaid", $"{part.PersonLabel} tagged {(paid ? "paid (cash)" : "unpaid")}.", now, by);
    }

    public void TagAllPaid(bool paid, DateTime now, string? by)
    {
        foreach (var p in ActiveParts) p.SetPaid(paid, now, by);
        Touch(now);
        Log(paid ? "paid" : "unpaid", paid ? "Everyone tagged paid (cash)." : "Everyone tagged unpaid.", now, by);
    }

    // ---- changes after placement (group orders) -------------------------------------------------

    /// <summary>"Oh, and add one more for Carlo." Highlighted as a late addition and re-alerts until acknowledged.</summary>
    public OrderPart AddPart(PartDraft draft, DateTime now, string? by)
    {
        EnsureOpen();
        var part = OrderPart.From(draft, _parts.Count, late: true);
        _parts.Add(part);
        Recalculate();
        AcknowledgedAtUtc = null;
        RefreshStatusFromParts(now, by);
        Log("part-added", $"Added later: {part.PersonLabel}.", now, by);
        return part;
    }

    public void CancelPart(Guid partId, string reason, DateTime now, string? by)
    {
        EnsureOpen();
        var part = FindActivePart(partId);
        part.Cancel(reason);
        Recalculate();
        Log("part-cancelled", $"{part.PersonLabel} removed: {reason}", now, by);
        if (ActiveCount == 0) Cancel("Everyone was removed from the order.", now, by);
        else RefreshStatusFromParts(now, by);
    }

    // ---- attention & notification tags ----------------------------------------------------------

    public void Acknowledge(DateTime now)
    {
        if (AcknowledgedAtUtc is not null) return;
        AcknowledgedAtUtc = now;
        Touch(now);
    }

    public void MarkNotified(string? channel, DateTime now, string? by)
    {
        NotifiedAtUtc = now;
        NotifiedChannel = channel;
        Touch(now);
        Log("notified", $"Customer notified{(channel is null ? "" : $" via {channel}")}.", now, by);
    }

    public void UndoNotified(DateTime now, string? by)
    {
        NotifiedAtUtc = null;
        NotifiedChannel = null;
        Touch(now);
        Log("notified-undo", "Notified tag removed.", now, by);
    }

    // ---- internals ------------------------------------------------------------------------------

    private void RefreshStatusFromParts(DateTime now, string? by)
    {
        if (!IsOpen) { Touch(now); return; }
        var before = Status;
        if (ActiveCount > 0 && ActiveParts.All(p => p.IsReady)) { Status = OrderStatus.Ready; ReadyAtUtc ??= now; }
        else Status = OrderStatus.New;
        if (before != Status) Log("status", $"Order is now {Status}.", now, by);
        Touch(now);
    }

    private void Recalculate() => Total = ActiveParts.Sum(p => p.Subtotal);
    private void Touch(DateTime now) => UpdatedAtUtc = now;

    private void EnsureOpen()
    {
        if (!IsOpen) throw new DomainException($"This order is {Status.ToString().ToLowerInvariant()} and can't be changed.");
    }

    private void Log(string type, string message, DateTime now, string? by) => _events.Add(OrderEvent.Create(type, message, now, by));

    private OrderPart FindActivePart(Guid id) =>
        _parts.FirstOrDefault(p => p.Id == id && !p.IsCancelled) ?? throw new DomainException("That person isn't on this order.");
}
