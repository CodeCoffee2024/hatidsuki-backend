using HatidSuki.Application.Delivery;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HatidSuki.Api.Controllers;

public record SaveLocationRequest(string Name, string? Note);
public record AddLocationsRequest(List<string> Names);
public record LocationActiveRequest(bool Active);

[ApiController, Route("api/delivery-locations"), Authorize]
public class DeliveryController(IMediator mediator) : ControllerBase
{
    /// <summary>Everyone signed in can read the list (staff need it for phone orders); only owners and managers change it.</summary>
    [HttpGet]
    public Task<List<DeliveryLocationDto>> List([FromQuery] bool includeHidden, CancellationToken ct) =>
        mediator.Send(new ListDeliveryLocationsQuery(includeHidden), ct);

    [HttpPost, Authorize(Roles = "Owner,Manager")]
    public Task<DeliveryLocationDto> Create(SaveLocationRequest r, CancellationToken ct) =>
        mediator.Send(new SaveDeliveryLocationCommand(null, r.Name, r.Note), ct);

    [HttpPut("{id:guid}"), Authorize(Roles = "Owner,Manager")]
    public Task<DeliveryLocationDto> Update(Guid id, SaveLocationRequest r, CancellationToken ct) =>
        mediator.Send(new SaveDeliveryLocationCommand(id, r.Name, r.Note), ct);

    [HttpPost("bulk"), Authorize(Roles = "Owner,Manager")]
    public Task<List<DeliveryLocationDto>> AddMany(AddLocationsRequest r, CancellationToken ct) =>
        mediator.Send(new AddDeliveryLocationsCommand(r.Names), ct);

    [HttpPut("{id:guid}/active"), Authorize(Roles = "Owner,Manager")]
    public Task<DeliveryLocationDto> SetActive(Guid id, LocationActiveRequest r, CancellationToken ct) =>
        mediator.Send(new SetDeliveryLocationActiveCommand(id, r.Active), ct);
}
