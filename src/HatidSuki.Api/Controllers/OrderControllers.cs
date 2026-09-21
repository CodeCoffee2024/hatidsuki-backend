using HatidSuki.Application.Common;
using HatidSuki.Application.Dashboard;
using HatidSuki.Application.Orders;
using HatidSuki.Application.Public;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HatidSuki.Api.Controllers;

/// <summary>What a customer sends. Deliberately has no prices or totals: the server always computes them.</summary>
public record PublicOrderRequest(string FormCode, string? SourceCode, string? IdempotencyKey,
    Dictionary<string, List<string>>? Answers, List<PartInput>? Parts, Guid? DeliveryLocationId, string? DeliveryNote);

/// <summary>Anonymous endpoints used by the order page a customer reaches from a QR code.</summary>
[ApiController, Route("api/public"), AllowAnonymous, EnableRateLimiting("public")]
public class PublicController(IMediator mediator) : ControllerBase
{
    [HttpGet("forms/{code}")]
    public Task<PublicFormDto> Form(string code, [FromQuery(Name = "s")] string? source, CancellationToken ct) =>
        mediator.Send(new GetPublicFormQuery(code, source), ct);

    [HttpPost("orders")]
    public Task<PlacedOrderDto> PlaceOrder(PublicOrderRequest r, CancellationToken ct) =>
        mediator.Send(new PlaceOrderCommand(r.FormCode, r.SourceCode, r.IdempotencyKey, r.Answers, r.Parts,
            null, null, null, MarkPaid: false, MarkServed: false, IsStaff: false,
            DeliveryLocationId: r.DeliveryLocationId, DeliveryNote: r.DeliveryNote), ct);

    [HttpGet("track/{token}")]
    public Task<TrackingDto> Track(string token, CancellationToken ct) => mediator.Send(new GetTrackingQuery(token), ct);
}

public record ManualOrderRequest(string FormCode, string? CustomerName, string? CustomerPhone, string? ManualType,
    Dictionary<string, List<string>>? Answers, List<PartInput>? Parts, bool MarkPaid, bool MarkServed,
    Guid? DeliveryLocationId, string? DeliveryNote);

public record ReadyRequest(bool Ready);
public record PaidRequest(bool Paid);
public record ReasonRequest(string? Reason);
public record NotifiedRequest(string? Channel, bool Undo);

[ApiController, Route("api/orders"), Authorize]
public class OrdersController(IMediator mediator) : ControllerBase
{
    /// <summary>The working screen: every open order with its people, counters and prep totals.</summary>
    [HttpGet("board")]
    public Task<CheckerBoardDto> Board([FromQuery] string? search, CancellationToken ct) => mediator.Send(new GetCheckerBoardQuery(search), ct);

    [HttpGet]
    public Task<PagedResult<OrderDto>> List([FromQuery] string? status, [FromQuery] string? payment, [FromQuery] string? search,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        mediator.Send(new ListOrdersQuery(status, payment, search, from, to, page, pageSize), ct);

    [HttpGet("{id:guid}")]
    public Task<OrderDto> Get(Guid id, CancellationToken ct) => mediator.Send(new GetOrderQuery(id), ct);

    /// <summary>Published forms staff can use to enter a phone or walk-in order.</summary>
    [HttpGet("entry-forms")]
    public Task<List<EntryFormDto>> EntryForms(CancellationToken ct) => mediator.Send(new ListEntryFormsQuery(), ct);

    /// <summary>Phone/walk-in orders entered by staff. Same pricing and rules as a customer's order.</summary>
    [HttpPost("manual")]
    public Task<PlacedOrderDto> Manual(ManualOrderRequest r, CancellationToken ct) =>
        mediator.Send(new PlaceOrderCommand(r.FormCode, null, null, r.Answers, r.Parts, r.CustomerName, r.CustomerPhone,
            r.ManualType, r.MarkPaid, r.MarkServed, IsStaff: true,
            DeliveryLocationId: r.DeliveryLocationId, DeliveryNote: r.DeliveryNote), ct);

    [HttpPost("{id:guid}/parts/{partId:guid}/ready")]
    public Task<OrderDto> PartReady(Guid id, Guid partId, ReadyRequest r, CancellationToken ct) => mediator.Send(new SetPartReadyCommand(id, partId, r.Ready), ct);

    [HttpPost("{id:guid}/ready-all")]
    public Task<OrderDto> ReadyAll(Guid id, CancellationToken ct) => mediator.Send(new MarkAllReadyCommand(id), ct);

    [HttpPost("{id:guid}/serve")]
    public Task<OrderDto> Serve(Guid id, CancellationToken ct) => mediator.Send(new ServeOrderCommand(id), ct);

    [HttpPost("{id:guid}/cancel")]
    public Task<OrderDto> Cancel(Guid id, ReasonRequest r, CancellationToken ct) => mediator.Send(new CancelOrderCommand(id, r.Reason), ct);

    [HttpPost("{id:guid}/parts/{partId:guid}/paid")]
    public Task<OrderDto> PartPaid(Guid id, Guid partId, PaidRequest r, CancellationToken ct) => mediator.Send(new TagPartPaidCommand(id, partId, r.Paid), ct);

    [HttpPost("{id:guid}/paid")]
    public Task<OrderDto> Paid(Guid id, PaidRequest r, CancellationToken ct) => mediator.Send(new TagOrderPaidCommand(id, r.Paid), ct);

    [HttpPost("{id:guid}/acknowledge")]
    public Task<OrderDto> Acknowledge(Guid id, CancellationToken ct) => mediator.Send(new AcknowledgeOrderCommand(id), ct);

    [HttpPost("{id:guid}/notified")]
    public Task<OrderDto> Notified(Guid id, NotifiedRequest r, CancellationToken ct) => mediator.Send(new MarkNotifiedCommand(id, r.Channel, r.Undo), ct);

    [HttpGet("{id:guid}/notification")]
    public Task<NotificationDto> Notification(Guid id, CancellationToken ct) => mediator.Send(new GetNotificationQuery(id), ct);

    /// <summary>"Oh, and add one more for Carlo": a late person on an existing order.</summary>
    [HttpPost("{id:guid}/parts")]
    public Task<OrderDto> AddPart(Guid id, PartInput part, CancellationToken ct) => mediator.Send(new AddPartCommand(id, part), ct);

    [HttpPost("{id:guid}/parts/{partId:guid}/cancel")]
    public Task<OrderDto> CancelPart(Guid id, Guid partId, ReasonRequest r, CancellationToken ct) => mediator.Send(new CancelPartCommand(id, partId, r.Reason), ct);
}

[ApiController, Route("api/dashboard"), Authorize(Roles = "Owner,Manager")]
public class DashboardController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public Task<DashboardDto> Get([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        mediator.Send(new GetDashboardQuery(from, to), ct);
}
