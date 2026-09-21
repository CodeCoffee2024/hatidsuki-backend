using FluentValidation;
using HatidSuki.Application.Common;
using HatidSuki.Domain;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace HatidSuki.Application.Delivery;

public record DeliveryLocationDto(Guid Id, string Name, string? Note, bool IsActive, int SortOrder);

internal static class DeliveryMapper
{
    public static DeliveryLocationDto ToDto(DeliveryLocation l) => new(l.Id, l.Name, l.Note, l.IsActive, l.SortOrder);
}

// ---- list (everyone signed in: staff need it when entering a phone order) ------------------------

public record ListDeliveryLocationsQuery(bool IncludeHidden) : IRequest<List<DeliveryLocationDto>>;

public class ListDeliveryLocationsHandler(IAppDbContext db) : IRequestHandler<ListDeliveryLocationsQuery, List<DeliveryLocationDto>>
{
    public async Task<List<DeliveryLocationDto>> Handle(ListDeliveryLocationsQuery q, CancellationToken ct)
    {
        var query = db.DeliveryLocations.AsNoTracking().AsQueryable();
        if (!q.IncludeHidden) query = query.Where(l => l.IsActive);
        var list = await query.OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
        return list.Select(DeliveryMapper.ToDto).ToList();
    }
}

// ---- add or edit one ------------------------------------------------------------------------------

public record SaveDeliveryLocationCommand(Guid? Id, string Name, string? Note) : IRequest<DeliveryLocationDto>;

public class SaveDeliveryLocationValidator : AbstractValidator<SaveDeliveryLocationCommand>
{
    public SaveDeliveryLocationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80).WithMessage("Enter a name of up to 80 characters.");
        RuleFor(x => x.Note).MaximumLength(200).WithMessage("Keep the note under 200 characters.");
    }
}

public class SaveDeliveryLocationHandler(IAppDbContext db, ICurrentUser current, IClock clock)
    : IRequestHandler<SaveDeliveryLocationCommand, DeliveryLocationDto>
{
    public async Task<DeliveryLocationDto> Handle(SaveDeliveryLocationCommand r, CancellationToken ct)
    {
        var wsId = current.RequireWorkspaceId();
        var name = r.Name.Trim();
        var duplicate = await db.DeliveryLocations.AnyAsync(l => l.Id != r.Id && l.Name.ToLower() == name.ToLower(), ct);
        if (duplicate) throw new ConflictException($"You already have a location called “{name}”.");

        DeliveryLocation location;
        if (r.Id is { } id)
        {
            location = await db.DeliveryLocations.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("That location no longer exists.");
            location.Update(name, r.Note);
        }
        else
        {
            var next = (await db.DeliveryLocations.MaxAsync(l => (int?)l.SortOrder, ct) ?? 0) + 1;
            location = DeliveryLocation.Create(wsId, name, r.Note, next, clock.UtcNow);
            db.DeliveryLocations.Add(location);
        }
        await db.SaveChangesAsync(ct);
        return DeliveryMapper.ToDto(location);
    }
}

// ---- add many at once (one per line) -----------------------------------------------------------------

public record AddDeliveryLocationsCommand(List<string> Names) : IRequest<List<DeliveryLocationDto>>;

public class AddDeliveryLocationsHandler(IAppDbContext db, ICurrentUser current, IClock clock)
    : IRequestHandler<AddDeliveryLocationsCommand, List<DeliveryLocationDto>>
{
    public async Task<List<DeliveryLocationDto>> Handle(AddDeliveryLocationsCommand r, CancellationToken ct)
    {
        var wsId = current.RequireWorkspaceId();
        var names = r.Names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) throw new DomainException("Enter at least one location.");
        if (names.Count > 200) throw new DomainException("You can add up to 200 locations at a time.");
        if (names.Any(n => n.Length > 80)) throw new DomainException("Location names can be up to 80 characters.");

        var existing = (await db.DeliveryLocations.Select(l => l.Name).ToListAsync(ct)).Select(n => n.ToLowerInvariant()).ToHashSet();
        var next = (await db.DeliveryLocations.MaxAsync(l => (int?)l.SortOrder, ct) ?? 0) + 1;
        foreach (var name in names.Where(n => !existing.Contains(n.ToLowerInvariant())))
            db.DeliveryLocations.Add(DeliveryLocation.Create(wsId, name, null, next++, clock.UtcNow));

        await db.SaveChangesAsync(ct);
        var all = await db.DeliveryLocations.AsNoTracking().OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct);
        return all.Select(DeliveryMapper.ToDto).ToList();
    }
}

// ---- hide / show ---------------------------------------------------------------------------------------

public record SetDeliveryLocationActiveCommand(Guid Id, bool Active) : IRequest<DeliveryLocationDto>;

public class SetDeliveryLocationActiveHandler(IAppDbContext db) : IRequestHandler<SetDeliveryLocationActiveCommand, DeliveryLocationDto>
{
    public async Task<DeliveryLocationDto> Handle(SetDeliveryLocationActiveCommand r, CancellationToken ct)
    {
        var l = await db.DeliveryLocations.FirstOrDefaultAsync(x => x.Id == r.Id, ct) ?? throw new NotFoundException("That location no longer exists.");
        l.SetActive(r.Active); // hidden, not deleted: past orders keep their location name
        await db.SaveChangesAsync(ct);
        return DeliveryMapper.ToDto(l);
    }
}
