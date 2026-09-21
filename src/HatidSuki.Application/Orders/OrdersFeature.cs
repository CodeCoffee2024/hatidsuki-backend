using System.Text.Json;
using FluentValidation;
using HatidSuki.Application.Common;
using HatidSuki.Application.Public;
using HatidSuki.Domain;
using HatidSuki.Domain.Forms;
using HatidSuki.Domain.Identity;
using HatidSuki.Domain.Orders;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HatidSuki.Application.Orders;

public static class Alerting
{
    /// <summary>A new order nobody has opened or acknowledged within this time is flagged and re-alerts.</summary>
    public const int StaleAfterMinutes = 5;
}

public record LineDto(string ItemName, int Quantity, decimal UnitPrice, decimal LineTotal, string? Note);

public record PartDto(Guid Id, string Person, string? Note, bool IsReady, bool IsPaid, string? PaidBy, bool IsCancelled,
    string? CancelReason, bool IsLateAddition, decimal Subtotal, List<LineDto> Lines);

public record AnswerDto(string Label, string Type, List<string> Values);
public record EventDto(DateTime AtUtc, string Type, string Message, string? By);

public record OrderDto(Guid Id, int Number, string Status, string PaymentStatus, string CustomerName, string? CustomerPhone,
    string? CustomerEmail, string? Source, string Channel, decimal Total, string Currency, DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc, bool NeedsAttention, bool IsStale, bool HasLateAddition, bool IsNotified, string? NotifiedChannel,
    int ActiveCount, int ReadyCount, int PaidCount, decimal UnpaidAmount, string TrackingToken,
    List<PartDto> Parts, List<AnswerDto>? Answers, List<EventDto>? Events, string? DeliveryLocation, string? DeliveryNote);

public static class OrderMapper
{
    public static OrderDto ToDto(Order o, DateTime now, bool detail)
    {
        var unacknowledged = o.IsOpen && o.AcknowledgedAtUtc is null;
        var parts = o.Parts.OrderBy(p => p.SortOrder).Select(p => new PartDto(p.Id, p.PersonLabel, p.Note, p.IsReady, p.IsPaid,
            p.PaidBy, p.IsCancelled, p.CancelReason, p.IsLateAddition, p.Subtotal,
            p.Lines.Select(l => new LineDto(l.ItemName, l.Quantity, l.UnitPrice, l.LineTotal, l.Note)).ToList())).ToList();

        List<AnswerDto>? answers = null;
        if (detail)
        {
            try
            {
                answers = JsonSerializer.Deserialize<List<AnswerDto>>(o.AnswersJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch (JsonException) { answers = []; }
        }

        return new OrderDto(o.Id, o.Number, o.Status.ToString(), o.PaymentStatus.ToString(), o.CustomerName, o.CustomerPhone,
            o.CustomerEmail, o.SourceName, o.Channel.ToString(), o.Total, o.Currency, o.CreatedAtUtc, o.UpdatedAtUtc,
            unacknowledged, unacknowledged && (now - o.CreatedAtUtc).TotalMinutes >= Alerting.StaleAfterMinutes,
            o.IsOpen && o.Parts.Any(p => p.IsLateAddition && !p.IsCancelled) && unacknowledged,
            o.NotifiedAtUtc is not null, o.NotifiedChannel, o.ActiveCount, o.ReadyCount, o.ActiveParts.Count(p => p.IsPaid),
            o.UnpaidAmount, o.TrackingToken, parts, answers,
            detail ? o.Events.OrderBy(e => e.AtUtc).Select(e => new EventDto(e.AtUtc, e.Type, e.Message, e.By)).ToList() : null,
            o.DeliveryLocationName, o.DeliveryNote);
    }

    public static IQueryable<Order> WithParts(this IQueryable<Order> q) => q.Include(o => o.Parts).ThenInclude(p => p.Lines);
}

internal static class OrderLoader
{
    public static async Task<Order> LoadAsync(IAppDbContext db, Guid id, CancellationToken ct) =>
        await db.Orders.WithParts().Include(o => o.Events).FirstOrDefaultAsync(o => o.Id == id, ct)
        ?? throw new NotFoundException("That order doesn't exist.");
}

// ---- the checker board -------------------------------------------------------------------------

public record BoardCounters(int New, int ReadyWaiting, int Unacknowledged, int UnpaidOrders, decimal CashToCollect, int OlderOpen);
public record PrepLine(string Item, int Quantity, int People);
public record CheckerBoardDto(List<OrderDto> Orders, BoardCounters Counters, List<PrepLine> Prep, DateTime ServerTimeUtc);

public record GetCheckerBoardQuery(string? Search) : IRequest<CheckerBoardDto>;

public class GetCheckerBoardHandler(IAppDbContext db, ICurrentUser current, IClock clock) : IRequestHandler<GetCheckerBoardQuery, CheckerBoardDto>
{
    public async Task<CheckerBoardDto> Handle(GetCheckerBoardQuery q, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var wsId = current.RequireWorkspaceId();
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == wsId, ct);

        var open = await db.Orders.AsNoTracking().WithParts()
            .Where(o => (o.Status == OrderStatus.New || o.Status == OrderStatus.Ready) && !o.IsTest)
            .OrderBy(o => o.CreatedAtUtc).ToListAsync(ct);

        // Cash still owed on any order that wasn't cancelled (including ones already served).
        var unpaid = await db.Orders.AsNoTracking().WithParts()
            .Where(o => o.Status != OrderStatus.Cancelled && !o.IsTest && o.Parts.Any(p => !p.IsCancelled && p.PaidAtUtc == null))
            .ToListAsync(ct);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(ws.Timezone);
        var startOfToday = TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTimeFromUtc(now, tz).Date, tz);

        var prep = open.SelectMany(o => o.ActiveParts.Where(p => !p.IsReady)).SelectMany(p => p.Lines.Select(l => new { p.Id, l }))
            .GroupBy(x => x.l.ItemName).Select(g => new PrepLine(g.Key, g.Sum(x => x.l.Quantity), g.Select(x => x.Id).Distinct().Count()))
            .OrderByDescending(x => x.Quantity).ToList();

        var counters = new BoardCounters(
            open.Count(o => o.Status == OrderStatus.New), open.Count(o => o.Status == OrderStatus.Ready),
            open.Count(o => o.AcknowledgedAtUtc is null), unpaid.Count, unpaid.Sum(o => o.UnpaidAmount),
            open.Count(o => o.CreatedAtUtc < startOfToday));

        IEnumerable<Order> shown = open;
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            shown = open.Where(o => o.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase)
                                    || $"{o.Number}" == term.TrimStart('#')
                                    || (o.DeliveryLocationName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                                    || o.Parts.Any(p => p.PersonLabel.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
        return new CheckerBoardDto(shown.Select(o => OrderMapper.ToDto(o, now, false)).ToList(), counters, prep, now);
    }
}

// ---- searchable list of all orders -------------------------------------------------------------

public record ListOrdersQuery(string? Status, string? Payment, string? Search, DateOnly? From, DateOnly? To, int Page, int PageSize)
    : IRequest<PagedResult<OrderDto>>;

public class ListOrdersHandler(IAppDbContext db, ICurrentUser current, IClock clock) : IRequestHandler<ListOrdersQuery, PagedResult<OrderDto>>
{
    public async Task<PagedResult<OrderDto>> Handle(ListOrdersQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page); var size = Math.Clamp(q.PageSize, 5, 100);
        var query = db.Orders.AsNoTracking().Where(o => !o.IsTest);

        if (Enum.TryParse<OrderStatus>(q.Status, true, out var status)) query = query.Where(o => o.Status == status);
        if (q.From is not null || q.To is not null)
        {
            // Dates are calendar days in the business's own timezone, not UTC.
            var wsId = current.RequireWorkspaceId();
            var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == wsId, ct);
            var tz = TimeZoneInfo.FindSystemTimeZoneById(ws.Timezone);
            if (q.From is { } from)
            {
                var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), tz);
                query = query.Where(o => o.CreatedAtUtc >= fromUtc);
            }
            if (q.To is { } to)
            {
                var toUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
                query = query.Where(o => o.CreatedAtUtc < toUtc);
            }
        }
        if (q.Payment == "unpaid") query = query.Where(o => o.Status != OrderStatus.Cancelled && o.Parts.Any(p => !p.IsCancelled && p.PaidAtUtc == null));
        if (q.Payment == "paid")
            query = query.Where(o => o.Parts.Any(p => !p.IsCancelled) && !o.Parts.Any(p => !p.IsCancelled && p.PaidAtUtc == null));
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim().ToLowerInvariant();
            var number = int.TryParse(term.TrimStart('#'), out var n) ? n : -1;
            query = query.Where(o => o.Number == number || o.CustomerName.ToLower().Contains(term)
                                     || (o.CustomerPhone != null && o.CustomerPhone.Contains(term))
                                     || o.Parts.Any(p => p.PersonLabel.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.WithParts().OrderByDescending(o => o.CreatedAtUtc).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<OrderDto>(rows.Select(o => OrderMapper.ToDto(o, clock.UtcNow, false)).ToList(), total, page, size);
    }
}

public record GetOrderQuery(Guid Id) : IRequest<OrderDto>;

public class GetOrderHandler(IAppDbContext db, IClock clock) : IRequestHandler<GetOrderQuery, OrderDto>
{
    public async Task<OrderDto> Handle(GetOrderQuery q, CancellationToken ct) =>
        OrderMapper.ToDto(await OrderLoader.LoadAsync(db, q.Id, ct), clock.UtcNow, detail: true);
}

// ---- the things staff do to an order -----------------------------------------------------------

/// <summary>One command per action; each returns the fresh order so the screen updates immediately.</summary>
public abstract record OrderAction(Guid OrderId) : IRequest<OrderDto>;
public record SetPartReadyCommand(Guid OrderId, Guid PartId, bool Ready) : OrderAction(OrderId);
public record MarkAllReadyCommand(Guid OrderId) : OrderAction(OrderId);
public record ServeOrderCommand(Guid OrderId) : OrderAction(OrderId);
public record CancelOrderCommand(Guid OrderId, string? Reason) : OrderAction(OrderId);
public record TagPartPaidCommand(Guid OrderId, Guid PartId, bool Paid) : OrderAction(OrderId);
public record TagOrderPaidCommand(Guid OrderId, bool Paid) : OrderAction(OrderId);
public record AcknowledgeOrderCommand(Guid OrderId) : OrderAction(OrderId);
public record MarkNotifiedCommand(Guid OrderId, string? Channel, bool Undo) : OrderAction(OrderId);
public record AddPartCommand(Guid OrderId, PartInput Part) : OrderAction(OrderId);
public record CancelPartCommand(Guid OrderId, Guid PartId, string? Reason) : OrderAction(OrderId);

public class OrderActionHandler(IAppDbContext db, ICurrentUser current, IClock clock) :
    IRequestHandler<SetPartReadyCommand, OrderDto>, IRequestHandler<MarkAllReadyCommand, OrderDto>,
    IRequestHandler<ServeOrderCommand, OrderDto>, IRequestHandler<CancelOrderCommand, OrderDto>,
    IRequestHandler<TagPartPaidCommand, OrderDto>, IRequestHandler<TagOrderPaidCommand, OrderDto>,
    IRequestHandler<AcknowledgeOrderCommand, OrderDto>, IRequestHandler<MarkNotifiedCommand, OrderDto>,
    IRequestHandler<AddPartCommand, OrderDto>, IRequestHandler<CancelPartCommand, OrderDto>
{
    private string? Who => current.Name;

    private async Task<OrderDto> Apply(Guid id, Action<Order, DateTime> act, CancellationToken ct)
    {
        var order = await OrderLoader.LoadAsync(db, id, ct);
        var now = clock.UtcNow;
        act(order, now);
        await db.SaveChangesAsync(ct);
        return OrderMapper.ToDto(order, now, detail: true);
    }

    public Task<OrderDto> Handle(SetPartReadyCommand r, CancellationToken ct) => Apply(r.OrderId, (o, now) =>
    {
        o.SetPartReady(r.PartId, r.Ready, now, Who);
        o.Acknowledge(now); // touching an order means someone has seen it
    }, ct);

    public Task<OrderDto> Handle(MarkAllReadyCommand r, CancellationToken ct) => Apply(r.OrderId, (o, now) => { o.MarkAllReady(now, Who); o.Acknowledge(now); }, ct);
    public Task<OrderDto> Handle(ServeOrderCommand r, CancellationToken ct) => Apply(r.OrderId, (o, now) => o.Serve(now, Who), ct);

    public Task<OrderDto> Handle(CancelOrderCommand r, CancellationToken ct) =>
        Apply(r.OrderId, (o, now) => o.Cancel(string.IsNullOrWhiteSpace(r.Reason) ? "No reason given" : r.Reason.Trim(), now, Who), ct);

    public Task<OrderDto> Handle(TagPartPaidCommand r, CancellationToken ct) => Apply(r.OrderId, (o, now) => o.TagPartPaid(r.PartId, r.Paid, now, Who), ct);
    public Task<OrderDto> Handle(TagOrderPaidCommand r, CancellationToken ct) => Apply(r.OrderId, (o, now) => o.TagAllPaid(r.Paid, now, Who), ct);
    public Task<OrderDto> Handle(AcknowledgeOrderCommand r, CancellationToken ct) => Apply(r.OrderId, (o, now) => o.Acknowledge(now), ct);

    public Task<OrderDto> Handle(MarkNotifiedCommand r, CancellationToken ct) => Apply(r.OrderId, (o, now) =>
    {
        if (r.Undo) o.UndoNotified(now, Who); else o.MarkNotified(r.Channel, now, Who);
    }, ct);

    public async Task<OrderDto> Handle(AddPartCommand r, CancellationToken ct)
    {
        var order = await OrderLoader.LoadAsync(db, r.OrderId, ct);
        var version = await db.FormVersions.AsNoTracking().FirstAsync(v => v.Id == order.FormVersionId, ct);
        var settings = FormDefinition.FromJson(version.DefinitionJson).OrderItemsField?.OrderItems;
        var (parts, problems) = await PartBuilder.BuildAsync(db, order.WorkspaceId, settings, [r.Part], "Late addition", ct);
        if (problems.Count > 0) throw Problems.Validation(problems);
        order.AddPart(parts[0], clock.UtcNow, Who);
        await db.SaveChangesAsync(ct);
        return OrderMapper.ToDto(order, clock.UtcNow, detail: true);
    }

    public Task<OrderDto> Handle(CancelPartCommand r, CancellationToken ct) =>
        Apply(r.OrderId, (o, now) => o.CancelPart(r.PartId, string.IsNullOrWhiteSpace(r.Reason) ? "No reason given" : r.Reason.Trim(), now, Who), ct);
}

// ---- forms staff can enter orders against (phone / walk-in) ---------------------------------------

public record EntryFormDto(string Code, string Name, bool AllowGroupOrders, int MaxParts, string ItemSource, List<Guid> ItemIds);
public record ListEntryFormsQuery : IRequest<List<EntryFormDto>>;

public class ListEntryFormsHandler(IAppDbContext db) : IRequestHandler<ListEntryFormsQuery, List<EntryFormDto>>
{
    public async Task<List<EntryFormDto>> Handle(ListEntryFormsQuery q, CancellationToken ct)
    {
        var forms = await db.Forms.AsNoTracking().Where(f => f.PublishedVersion != null).OrderBy(f => f.Name).ToListAsync(ct);
        var ids = forms.Select(f => f.Id).ToList();
        var versions = await db.FormVersions.AsNoTracking().Where(v => ids.Contains(v.FormId))
            .Select(v => new { v.FormId, v.Number, v.DefinitionJson }).ToListAsync(ct);

        return forms.Select(f =>
        {
            var json = versions.First(v => v.FormId == f.Id && v.Number == f.PublishedVersion).DefinitionJson;
            var s = FormDefinition.FromJson(json).OrderItemsField?.OrderItems;
            return new EntryFormDto(f.ShortCode, f.Name, s?.AllowGroupOrders ?? false, s?.MaxParts ?? 1, s?.Source ?? "all", s?.ItemIds ?? []);
        }).ToList();
    }
}

// ---- tell the customer their order is ready ----------------------------------------------------

public record NotificationDto(string Message, string? WhatsAppLink, string? SmsLink, string? EmailLink, string TrackingLink);
public record GetNotificationQuery(Guid OrderId) : IRequest<NotificationDto>;

public class GetNotificationHandler(IAppDbContext db, IOptions<AppOptions> options) : IRequestHandler<GetNotificationQuery, NotificationDto>
{
    public async Task<NotificationDto> Handle(GetNotificationQuery q, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().WithParts().FirstOrDefaultAsync(x => x.Id == q.OrderId, ct) ?? throw new NotFoundException();
        var ws = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == o.WorkspaceId, ct);
        var link = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/t/{o.TrackingToken}";
        var who = o.ActiveCount > 1 ? $" (for {o.ActiveCount} people)" : "";
        var message = $"Hi {o.CustomerName}, your order #{o.Number}{who} is ready! — {ws.Name}\nTrack it: {link}";

        var digits = PhoneLinks.Digits(o.CustomerPhone, ws.PhoneCountryCode);
        return new NotificationDto(message,
            digits is null ? null : $"https://wa.me/{digits}?text={Uri.EscapeDataString(message)}",
            digits is null ? null : $"sms:+{digits}?&body={Uri.EscapeDataString(message)}",
            string.IsNullOrWhiteSpace(o.CustomerEmail) ? null : $"mailto:{o.CustomerEmail}?subject={Uri.EscapeDataString($"Your order #{o.Number} is ready")}&body={Uri.EscapeDataString(message)}",
            link);
    }
}

public static class PhoneLinks
{
    /// <summary>International digits for wa.me links, e.g. "0917 123 4567" + country code "63" → "639171234567".</summary>
    public static string? Digits(string? phone, string countryCode)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var hadPlus = phone.TrimStart().StartsWith('+');
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 6) return null;
        if (hadPlus) return digits;
        if (digits.StartsWith("00")) return digits[2..];
        if (digits.StartsWith('0') && countryCode.Length > 0) return countryCode + digits[1..];
        return countryCode.Length > 0 && digits.Length <= 10 ? countryCode + digits : digits;
    }
}
