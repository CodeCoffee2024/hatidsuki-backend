using System.Text.Json;
using System.Text.RegularExpressions;
using HatidSuki.Application.Common;
using HatidSuki.Application.Items;
using HatidSuki.Domain;
using HatidSuki.Domain.Forms;
using HatidSuki.Domain.Items;
using HatidSuki.Domain.Orders;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HatidSuki.Application.Public;

public record PublicItemDto(Guid Id, string Name, string? Description, decimal Price, string? Category, string Unit,
    bool IsAvailable, List<OptionGroupDto> OptionGroups);

public record PublicLocationDto(Guid Id, string Name, string? Note);

public record PublicFormDto(string Code, string BusinessName, string Currency, string FormName, string State,
    string? ClosedMessage, FormDefinition? Definition, List<PublicItemDto> Items, int Version, string? SourceName,
    List<PublicLocationDto> Locations);

/// <summary>Works out which catalog items an order-items field offers.</summary>
public static class ItemResolver
{
    public static async Task<List<PublicItemDto>> ResolveAsync(IAppDbContext db, OrderItemsSettings settings, CancellationToken ct,
        Guid? workspaceId = null)
    {
        // Public callers pass the workspace explicitly (they have no tenant); staff callers rely on the tenant filter.
        IQueryable<Item> query = workspaceId is { } ws
            ? db.Items.IgnoreQueryFilters().AsNoTracking().Where(i => i.WorkspaceId == ws && !i.IsArchived)
            : db.Items.AsNoTracking().Where(i => !i.IsArchived);
        if (settings.Source == "selected") query = query.Where(i => settings.ItemIds.Contains(i.Id));
        var items = await query.OrderBy(i => i.Category).ThenBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        return items.Select(i => new PublicItemDto(i.Id, i.Name, i.Description, i.Price, i.Category, i.Unit, i.IsAvailable,
            ItemMapper.ToDto(i).OptionGroups)).ToList();
    }
}

// ---- open a published form ---------------------------------------------------------------------

public record GetPublicFormQuery(string Code, string? SourceCode) : IRequest<PublicFormDto>;

public class GetPublicFormHandler(IAppDbContext db) : IRequestHandler<GetPublicFormQuery, PublicFormDto>
{
    public async Task<PublicFormDto> Handle(GetPublicFormQuery q, CancellationToken ct)
    {
        // Public requests have no tenant, so the workspace is resolved from the short code only.
        var form = await db.Forms.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.ShortCode == q.Code, ct);
        if (form is null || form.Status == FormStatus.Draft || form.PublishedVersion is null)
            throw new NotFoundException("This order page isn't available.");

        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == form.WorkspaceId, ct);
        var version = await db.FormVersions.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(v => v.FormId == form.Id && v.Number == form.PublishedVersion, ct);
        var def = FormDefinition.FromJson(version.DefinitionJson);

        string? sourceName = null;
        if (!string.IsNullOrWhiteSpace(q.SourceCode))
        {
            var src = await db.FormSources.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.FormId == form.Id && s.Code == q.SourceCode, ct);
            if (src is { IsActive: true })
            {
                sourceName = src.Name;
                src.RecordScan(); // aggregate counter only: no IP or personal data
                await db.SaveChangesAsync(ct);
            }
        }

        if (form.Status == FormStatus.Closed)
            return new PublicFormDto(form.ShortCode, workspace.Name, workspace.Currency, form.Name, "closed",
                def.ClosedMessage ?? "We're not taking orders right now. Please check back soon.", null, [], version.Number, sourceName, []);

        var items = def.OrderItemsField?.OrderItems is { } s ? await ItemResolver.ResolveAsync(db, s, ct, form.WorkspaceId) : [];
        // Every order form asks where to deliver, so the locations always travel with it (and are read live, not frozen in a version).
        var locations = await db.DeliveryLocations.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == form.WorkspaceId && l.IsActive).OrderBy(l => l.SortOrder).ThenBy(l => l.Name)
            .Select(l => new PublicLocationDto(l.Id, l.Name, l.Note)).ToListAsync(ct);
        return new PublicFormDto(form.ShortCode, workspace.Name, workspace.Currency, form.Name, "open", null, def, items, version.Number, sourceName, locations);
    }
}

// ---- building people (parts) from a request ----------------------------------------------------

/// <summary>Options is the group id → chosen option ids for that group, for items that have option groups (FS-008).</summary>
public record LineInput(Guid ItemId, int Quantity, string? Note, Dictionary<Guid, List<Guid>>? Options = null);
public record PartInput(string? Person, string? Note, List<LineInput>? Lines);

internal static class PartBuilder
{
    /// <summary>A stable key so two lines choosing the exact same options for the same item are one line, not two.</summary>
    private static string OptionsKey(Dictionary<Guid, List<Guid>>? options) =>
        options is null || options.Count == 0
            ? ""
            : string.Join("|", options.OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}:{string.Join(",", kv.Value.Distinct().OrderBy(x => x))}"));

    public static async Task<(List<PartDraft> Parts, List<(string Key, string Message)> Problems)> BuildAsync(
        IAppDbContext db, Guid workspaceId, OrderItemsSettings? settings, List<PartInput>? inputs, string fallbackLabel, CancellationToken ct)
    {
        var problems = new List<(string, string)>();
        var drafts = new List<PartDraft>();
        inputs ??= [];

        if (settings is null)
        {
            if (inputs.Count > 0) problems.Add(("parts", "This form doesn't take items."));
            return (drafts, problems);
        }
        if (inputs.Count == 0) { problems.Add(("parts", "Add at least one item to your order.")); return (drafts, problems); }
        if (inputs.Count > 1 && !settings.AllowGroupOrders) { problems.Add(("parts", "This form takes one person's order at a time.")); return (drafts, problems); }
        if (inputs.Count > settings.MaxParts) { problems.Add(("parts", $"You can add up to {settings.MaxParts} people to one order.")); return (drafts, problems); }

        var wanted = inputs.SelectMany(p => p.Lines ?? []).Select(l => l.ItemId).Distinct().ToList();
        if (wanted.Sum(_ => 1) > 300) { problems.Add(("parts", "That's too many different items in one order.")); return (drafts, problems); }
        var items = await db.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId && wanted.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        var totalQty = 0;
        for (var pi = 0; pi < inputs.Count; pi++)
        {
            var input = inputs[pi];
            var label = (input.Person ?? "").Trim();
            if (inputs.Count > 1 && label.Length == 0) problems.Add(($"parts[{pi}].person", $"Enter a name for person {pi + 1}."));
            if (label.Length > 60) problems.Add(($"parts[{pi}].person", "Names can be up to 60 characters."));
            if (label.Length == 0) label = fallbackLabel;

            var lines = new List<LineDraft>();
            var merged = (input.Lines ?? []).Where(l => l.Quantity > 0).GroupBy(l => (l.ItemId, Options: OptionsKey(l.Options)));
            foreach (var g in merged)
            {
                var (itemId, _) = g.Key;
                var qty = g.Sum(l => l.Quantity);
                if (!items.TryGetValue(itemId, out var item)) { problems.Add(($"parts[{pi}]", "One of the items doesn't exist any more. Please reload the page.")); continue; }
                if (settings.Source == "selected" && !settings.ItemIds.Contains(item.Id)) { problems.Add(($"parts[{pi}]", $"'{item.Name}' isn't on this menu.")); continue; }
                if (!item.IsOrderable) { problems.Add(($"parts[{pi}]", $"'{item.Name}' is sold out.")); continue; }
                if (qty > 99) { problems.Add(($"parts[{pi}]", $"You can order up to 99 of '{item.Name}' per person.")); continue; }

                decimal unitPrice; List<SelectedOptionSnapshot> selected;
                try { (unitPrice, selected) = item.PriceFor(g.First().Options ?? []); }
                catch (DomainException ex) { problems.Add(($"parts[{pi}]", $"'{item.Name}': {ex.Message}")); continue; }

                totalQty += qty;
                var note = settings.AllowLineNotes ? g.Select(l => l.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() : null;
                lines.Add(new LineDraft(item.Id, item.Name, unitPrice, qty, note is { Length: > 200 } ? note[..200] : note, selected));
            }
            if (lines.Count == 0) problems.Add(($"parts[{pi}]", inputs.Count > 1 ? $"Add at least one item for {label}." : "Add at least one item to your order."));
            var partNote = input.Note?.Trim();
            drafts.Add(new PartDraft(label, partNote is { Length: > 300 } ? partNote[..300] : partNote, lines));
        }
        if (totalQty > 500) problems.Add(("parts", "That's more than 500 items in one order. Please split it up."));
        return (drafts, problems);
    }
}

// ---- checking the customer's answers -----------------------------------------------------------

internal sealed record AnswerSnapshot(Guid FieldId, string Label, string Type, List<string> Values);

internal static partial class AnswerChecker
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailPattern();

    public static (List<(string Key, string Message)> Problems, string? Name, string? Phone, string? Email, string Json) Check(
        FormDefinition def, Dictionary<string, List<string>>? answers)
    {
        var problems = new List<(string, string)>();
        var snapshot = new List<AnswerSnapshot>();
        string? name = null, phone = null, email = null;
        answers ??= [];

        foreach (var f in def.Fields.Where(f => f.Type is not (FieldTypes.Heading or FieldTypes.OrderItems)))
        {
            var key = $"field:{f.Id}";
            answers.TryGetValue(f.Id.ToString(), out var raw);
            var values = (raw ?? []).Select(v => (v ?? "").Trim()).Where(v => v.Length > 0).ToList();

            if (values.Count == 0)
            {
                if (f.Required) problems.Add((key, $"'{f.Label}' is required."));
                continue;
            }
            var max = f.Type == FieldTypes.Paragraph ? 2000 : 500;
            if (values.Any(v => v.Length > max)) { problems.Add((key, $"'{f.Label}' is too long.")); continue; }

            switch (f.Type)
            {
                case FieldTypes.Email when !EmailPattern().IsMatch(values[0]):
                    problems.Add((key, $"'{f.Label}' must be a valid email address.")); break;
                case FieldTypes.Phone when values[0].Count(char.IsDigit) is < 6 or > 15 || values[0].Any(c => !(char.IsDigit(c) || " +-().".Contains(c))):
                    problems.Add((key, $"'{f.Label}' must be a valid phone number.")); break;
                case FieldTypes.Number when !decimal.TryParse(values[0], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _):
                    problems.Add((key, $"'{f.Label}' must be a number.")); break;
                case FieldTypes.Date when !DateOnly.TryParse(values[0], System.Globalization.CultureInfo.InvariantCulture, out _):
                    problems.Add((key, $"'{f.Label}' must be a date.")); break;
                case FieldTypes.Time when !TimeOnly.TryParse(values[0], System.Globalization.CultureInfo.InvariantCulture, out _):
                    problems.Add((key, $"'{f.Label}' must be a time.")); break;
                case FieldTypes.SingleChoice or FieldTypes.Dropdown when !f.Options.Contains(values[0]):
                    problems.Add((key, $"Choose one of the options for '{f.Label}'.")); break;
                case FieldTypes.MultiChoice when values.Any(v => !f.Options.Contains(v)):
                    problems.Add((key, $"Choose from the options for '{f.Label}'.")); break;
                case FieldTypes.Consent when f.Required && values[0] != "true":
                    problems.Add((key, $"Please tick '{f.Label}'.")); break;
            }

            if (f.Role == FieldRoles.CustomerName) name = values[0];
            if (f.Role == FieldRoles.CustomerPhone) phone = values[0];
            if (f.Role == FieldRoles.CustomerEmail) email = values[0];
            // The label is stored with the answer, so old orders read correctly even if the form is edited later.
            snapshot.Add(new AnswerSnapshot(f.Id, f.Label, f.Type, values));
        }
        return (problems, name, phone, email, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

// ---- place an order (customers and staff share this one code path) -----------------------------

public record PlaceOrderCommand(string FormCode, string? SourceCode, string? IdempotencyKey,
    Dictionary<string, List<string>>? Answers, List<PartInput>? Parts,
    string? CustomerName, string? CustomerPhone, string? ManualType, bool MarkPaid, bool MarkServed, bool IsStaff,
    Guid? DeliveryLocationId = null, string? DeliveryNote = null) : IRequest<PlacedOrderDto>;

public record PlacedOrderDto(Guid OrderId, int Number, decimal Total, string Currency, string TrackingToken,
    string? ThankYou, string? PaymentMessage, int People, string? DeliveryTo = null);

public class PlaceOrderHandler(IAppDbContext db, ICurrentUser current, IClock clock, IEmailSender email,
    IOptions<AppOptions> appOptions, ILogger<PlaceOrderHandler> logger) : IRequestHandler<PlaceOrderCommand, PlacedOrderDto>
{
    public async Task<PlacedOrderDto> Handle(PlaceOrderCommand r, CancellationToken ct)
    {
        var form = await db.Forms.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.ShortCode == r.FormCode, ct);
        if (form is null || form.Status == FormStatus.Draft || form.PublishedVersion is null)
            throw new NotFoundException("This order page isn't available.");

        if (r.IsStaff)
        {
            if (current.WorkspaceId != form.WorkspaceId) throw new NotFoundException("This order page isn't available.");
        }
        else if (form.Status == FormStatus.Closed)
        {
            throw new ConflictException("We're not taking orders right now.");
        }

        // A retry of the same submission (double tap, flaky network) returns the original order.
        if (!string.IsNullOrWhiteSpace(r.IdempotencyKey))
        {
            var previous = await db.Orders.IgnoreQueryFilters().AsNoTracking().Include(o => o.Parts)
                .FirstOrDefaultAsync(o => o.WorkspaceId == form.WorkspaceId && o.IdempotencyKey == r.IdempotencyKey, ct);
            if (previous is not null) return await ToPlacedAsync(previous, form, ct);
        }

        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == form.WorkspaceId, ct);
        var version = await db.FormVersions.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(v => v.FormId == form.Id && v.Number == form.PublishedVersion, ct);
        var def = FormDefinition.FromJson(version.DefinitionJson);

        var (problems, name, phone, email, answersJson) = AnswerChecker.Check(def, r.Answers);
        var customerName = r.IsStaff && !string.IsNullOrWhiteSpace(r.CustomerName) ? r.CustomerName.Trim() : name;
        if (r.IsStaff) { phone = string.IsNullOrWhiteSpace(r.CustomerPhone) ? phone : r.CustomerPhone.Trim(); problems.RemoveAll(p => p.Key.StartsWith("field:")); }

        var (parts, partProblems) = await PartBuilder.BuildAsync(db, form.WorkspaceId, def.OrderItemsField?.OrderItems, r.Parts,
            customerName ?? "Order", ct);
        problems.AddRange(partProblems);

        // Every order says where it goes. Customers must choose; staff entering a counter sale may leave it out.
        DeliveryLocation? location = null;
        if (r.DeliveryLocationId is { } locationId)
        {
            location = await db.DeliveryLocations.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == locationId && l.WorkspaceId == form.WorkspaceId && l.IsActive, ct);
            if (location is null) problems.Add(("deliveryLocation", "That delivery location isn't available any more. Please choose another."));
        }
        else if (!r.IsStaff)
        {
            problems.Add(("deliveryLocation", "Choose where we should deliver your order."));
        }
        if (problems.Count > 0) throw Problems.Validation(problems);

        // Source: only an active source of this form counts; otherwise the order is a "Direct link" one.
        string? sourceCode = null, sourceName = r.IsStaff ? (string.IsNullOrWhiteSpace(r.ManualType) ? "Manual" : r.ManualType!.Trim()) : "Direct link";
        if (!r.IsStaff && !string.IsNullOrWhiteSpace(r.SourceCode))
        {
            var src = await db.FormSources.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(s => s.FormId == form.Id && s.Code == r.SourceCode && s.IsActive, ct);
            if (src is not null) { sourceCode = src.Code; sourceName = src.Name; }
        }

        var number = await db.NextOrderNumberAsync(form.WorkspaceId, ct);
        var order = Order.Place(form.WorkspaceId, form.Id, version.Id, number, workspace.Currency,
            r.IsStaff ? OrderChannel.Staff : OrderChannel.Public, r.IsStaff ? r.ManualType : null,
            customerName ?? "Customer", phone, email, answersJson, sourceCode, sourceName, isTest: false,
            r.IdempotencyKey, parts, clock.UtcNow, r.IsStaff ? current.Name : "customer");

        order.SetDelivery(location?.Id, location?.Name, r.DeliveryNote);
        if (r.IsStaff && r.MarkPaid) order.TagAllPaid(true, clock.UtcNow, current.Name);
        if (r.IsStaff && r.MarkServed) order.Serve(clock.UtcNow, current.Name);

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        await SendConfirmationEmailAsync(order, workspace.Name, def.PaymentMessage, ct);
        return new PlacedOrderDto(order.Id, order.Number, order.Total, order.Currency, order.TrackingToken,
            def.ThankYou, def.PaymentMessage, order.Parts.Count, order.DeliveryLocationName);
    }

    /// <summary>Best-effort: a mail provider outage must never fail order placement (FS-020 AC3/AC7).</summary>
    private async Task SendConfirmationEmailAsync(Order order, string businessName, string? paymentMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(order.CustomerEmail)) return;
        var trackingLink = $"{appOptions.Value.PublicBaseUrl.TrimEnd('/')}/t/{order.TrackingToken}";
        var body = $"""
            Hi {order.CustomerName},

            Thanks — order #{order.Number} from {businessName} has been received.

            Total: {order.Total:0.00} {order.Currency}
            {(paymentMessage is null ? "" : paymentMessage + "\n")}
            Track your order: {trackingLink}
            """;
        try { await email.SendAsync(order.CustomerEmail, $"Order #{order.Number} received — {businessName}", body, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Order confirmation email failed for order {OrderId}.", order.Id); }
    }

    private async Task<PlacedOrderDto> ToPlacedAsync(Order o, Form form, CancellationToken ct)
    {
        var version = await db.FormVersions.IgnoreQueryFilters().AsNoTracking().FirstAsync(v => v.Id == o.FormVersionId, ct);
        var def = FormDefinition.FromJson(version.DefinitionJson);
        return new PlacedOrderDto(o.Id, o.Number, o.Total, o.Currency, o.TrackingToken, def.ThankYou, def.PaymentMessage, o.Parts.Count, o.DeliveryLocationName);
    }
}

// ---- customer tracking page --------------------------------------------------------------------

public record TrackingPartDto(string Person, bool IsReady, bool IsCancelled, List<string> Items);
public record TrackingDto(int Number, string Status, int ReadyCount, int ActiveCount, string BusinessName, string Currency,
    decimal Total, string? PaymentMessage, string PaymentStatus, DateTime UpdatedAtUtc, List<TrackingPartDto> People,
    string? DeliveryTo, string? DeliveryNote);

public record GetTrackingQuery(string Token) : IRequest<TrackingDto>;

public class GetTrackingHandler(IAppDbContext db) : IRequestHandler<GetTrackingQuery, TrackingDto>
{
    public async Task<TrackingDto> Handle(GetTrackingQuery q, CancellationToken ct)
    {
        var o = await db.Orders.IgnoreQueryFilters().AsNoTracking().Include(x => x.Parts).ThenInclude(p => p.Lines)
            .FirstOrDefaultAsync(x => x.TrackingToken == q.Token, ct) ?? throw new NotFoundException("We couldn't find that order.");
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == o.WorkspaceId, ct);
        var version = await db.FormVersions.IgnoreQueryFilters().AsNoTracking().FirstAsync(v => v.Id == o.FormVersionId, ct);
        var def = FormDefinition.FromJson(version.DefinitionJson);

        // Only what the customer needs: no internal notes, no staff names.
        var people = o.Parts.OrderBy(p => p.SortOrder)
            .Select(p => new TrackingPartDto(p.PersonLabel, p.IsReady, p.IsCancelled, p.Lines.Select(l => $"{l.Quantity} × {l.ItemName}").ToList())).ToList();
        return new TrackingDto(o.Number, o.Status.ToString(), o.ReadyCount, o.ActiveCount, ws.Name, o.Currency, o.Total,
            def.PaymentMessage, o.PaymentStatus.ToString(), o.UpdatedAtUtc, people, o.DeliveryLocationName, o.DeliveryNote);
    }
}
