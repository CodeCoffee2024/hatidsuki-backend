using HatidSuki.Application.Forms;
using HatidSuki.Application.Items;
using HatidSuki.Domain.Forms;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HatidSuki.Api.Controllers;

public record SaveItemRequest(string Name, decimal Price, string? Category, string? Description, string? Unit);
public record AvailabilityRequest(bool Available);
public record ArchivedRequest(bool Archived);
public record SaveOptionGroupsRequest(List<OptionGroupInput> Groups);

[ApiController, Route("api/items"), Authorize]
public class ItemsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public Task<List<ItemDto>> List([FromQuery] string? search, [FromQuery] bool includeArchived, CancellationToken ct) =>
        mediator.Send(new ListItemsQuery(search, includeArchived), ct);

    [HttpPost, Authorize(Roles = "Owner,Manager")]
    public Task<ItemDto> Create(SaveItemRequest r, CancellationToken ct) =>
        mediator.Send(new SaveItemCommand(null, r.Name, r.Price, r.Category, r.Description, r.Unit), ct);

    [HttpPut("{id:guid}"), Authorize(Roles = "Owner,Manager")]
    public Task<ItemDto> Update(Guid id, SaveItemRequest r, CancellationToken ct) =>
        mediator.Send(new SaveItemCommand(id, r.Name, r.Price, r.Category, r.Description, r.Unit), ct);

    /// <summary>Rapid entry: add many items at once. Send dryRun=true to preview every row without saving.</summary>
    [HttpPost("bulk"), Authorize(Roles = "Owner,Manager")]
    public Task<BulkItemsResult> Bulk(BulkItemsCommand c, CancellationToken ct) => mediator.Send(c, ct);

    // Staff may flip "sold out" during service.
    [HttpPut("{id:guid}/availability")]
    public Task<ItemDto> Availability(Guid id, AvailabilityRequest r, CancellationToken ct) =>
        mediator.Send(new SetItemAvailabilityCommand(id, r.Available), ct);

    [HttpPut("{id:guid}/archived"), Authorize(Roles = "Owner,Manager")]
    public Task<ItemDto> Archive(Guid id, ArchivedRequest r, CancellationToken ct) =>
        mediator.Send(new SetItemArchivedCommand(id, r.Archived), ct);

    // ---- option groups: sizes, flavors, add-ons (FS-008) ----

    [HttpPut("{id:guid}/options"), Authorize(Roles = "Owner,Manager")]
    public Task<ItemDto> SaveOptions(Guid id, SaveOptionGroupsRequest r, CancellationToken ct) =>
        mediator.Send(new SaveItemOptionGroupsCommand(id, r.Groups), ct);

    // Staff may 86 a single option (e.g. "no more large cups") during service.
    [HttpPut("{id:guid}/options/{groupId:guid}/{optionId:guid}/availability")]
    public Task<ItemDto> SetOptionAvailability(Guid id, Guid groupId, Guid optionId, AvailabilityRequest r, CancellationToken ct) =>
        mediator.Send(new SetItemOptionAvailabilityCommand(id, groupId, optionId, r.Available), ct);

    [HttpPost("{id:guid}/options/copy-from/{sourceId:guid}"), Authorize(Roles = "Owner,Manager")]
    public Task<ItemDto> CopyOptions(Guid id, Guid sourceId, CancellationToken ct) =>
        mediator.Send(new CopyOptionGroupsCommand(id, sourceId), ct);
}

public record CreateFormRequest(string Name);
public record UpdateFormRequest(string Name, FormDefinition Definition);
public record FormActionRequest(string Action);
public record CreateSourcesRequest(List<string>? Names, string? Prefix, int? From, int? To);
public record SourceActiveRequest(bool Active);

[ApiController, Route("api/forms"), Authorize(Roles = "Owner,Manager")]
public class FormsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public Task<List<FormListDto>> List(CancellationToken ct) => mediator.Send(new ListFormsQuery(), ct);

    [HttpGet("{id:guid}")]
    public Task<FormDetailDto> Get(Guid id, CancellationToken ct) => mediator.Send(new GetFormQuery(id), ct);

    [HttpPost]
    public Task<FormDetailDto> Create(CreateFormRequest r, CancellationToken ct) => mediator.Send(new CreateFormCommand(r.Name), ct);

    [HttpPut("{id:guid}")]
    public Task<FormDetailDto> Update(Guid id, UpdateFormRequest r, CancellationToken ct) =>
        mediator.Send(new UpdateFormDraftCommand(id, r.Name, r.Definition), ct);

    /// <summary>422 with a list of problems when the form isn't ready to publish; 200 with the form when it is.</summary>
    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
    {
        var result = await mediator.Send(new PublishFormCommand(id), ct);
        return result.Published ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpPost("{id:guid}/status")]
    public Task<FormListDto> Status(Guid id, FormActionRequest r, CancellationToken ct) => mediator.Send(new SetFormStatusCommand(id, r.Action), ct);

    [HttpGet("{id:guid}/sources")]
    public Task<List<SourceDto>> Sources(Guid id, CancellationToken ct) => mediator.Send(new ListSourcesQuery(id), ct);

    [HttpPost("{id:guid}/sources")]
    public Task<List<SourceDto>> AddSources(Guid id, CreateSourcesRequest r, CancellationToken ct) =>
        mediator.Send(new CreateSourcesCommand(id, r.Names, r.Prefix, r.From, r.To), ct);

    [HttpPut("{id:guid}/sources/{sourceId:guid}")]
    public async Task<IActionResult> SetSourceActive(Guid id, Guid sourceId, SourceActiveRequest r, CancellationToken ct)
    {
        await mediator.Send(new SetSourceActiveCommand(id, sourceId, r.Active), ct);
        return NoContent();
    }

    /// <summary>The QR code image (svg or png) for the form, or for one source such as "Table 3".</summary>
    [HttpGet("{id:guid}/qr")]
    public async Task<IActionResult> Qr(Guid id, [FromQuery] Guid? source, [FromQuery] string format = "svg", [FromQuery] int size = 600, CancellationToken ct = default)
    {
        var qr = await mediator.Send(new GetQrQuery(id, source, format, size), ct);
        Response.Headers["X-Qr-Url"] = qr.Url;
        return File(qr.Bytes, qr.ContentType);
    }
}
