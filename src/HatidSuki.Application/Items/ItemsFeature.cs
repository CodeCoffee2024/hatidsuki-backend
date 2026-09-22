using System.Globalization;
using System.Text.RegularExpressions;
using FluentValidation;
using HatidSuki.Application.Common;
using HatidSuki.Domain;
using HatidSuki.Domain.Items;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace HatidSuki.Application.Items;

public record OptionDto(Guid Id, string Name, decimal PriceDelta, bool IsAvailable, bool IsDefault, int SortOrder);
public record OptionGroupDto(Guid Id, string Name, string SelectionType, bool Required, int? MinSelect, int? MaxSelect,
    int SortOrder, List<OptionDto> Options);

public record ItemDto(Guid Id, string Name, string? Description, decimal Price, string? Category, string Unit,
    bool IsAvailable, bool IsArchived, int SortOrder, List<OptionGroupDto> OptionGroups, decimal DefaultPrice);

internal static class ItemMapper
{
    public static ItemDto ToDto(Item i)
    {
        var groups = i.OptionGroups;
        var groupDtos = groups.OrderBy(g => g.SortOrder)
            .Select(g => new OptionGroupDto(g.Id, g.Name, g.SelectionType, g.Required, g.MinSelect, g.MaxSelect, g.SortOrder,
                g.Options.OrderBy(o => o.SortOrder).Select(o => new OptionDto(o.Id, o.Name, o.PriceDelta, o.IsAvailable, o.IsDefault, o.SortOrder)).ToList()))
            .ToList();
        return new(i.Id, i.Name, i.Description, i.Price, i.Category, i.Unit, i.IsAvailable, i.IsArchived, i.SortOrder,
            groupDtos, PriceCalculator.DefaultPrice(i.Price, groups));
    }
}

// ---- list --------------------------------------------------------------------------------------

public record ListItemsQuery(string? Search, bool IncludeArchived) : IRequest<List<ItemDto>>;

public class ListItemsHandler(IAppDbContext db) : IRequestHandler<ListItemsQuery, List<ItemDto>>
{
    public async Task<List<ItemDto>> Handle(ListItemsQuery q, CancellationToken ct)
    {
        var query = db.Items.AsNoTracking().AsQueryable();
        if (!q.IncludeArchived) query = query.Where(i => !i.IsArchived);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim().ToLowerInvariant();
            query = query.Where(i => i.Name.ToLower().Contains(term) || (i.Category != null && i.Category.ToLower().Contains(term)));
        }
        var items = await query.OrderBy(i => i.Category).ThenBy(i => i.SortOrder).ThenBy(i => i.Name).ToListAsync(ct);
        return items.Select(ItemMapper.ToDto).ToList();
    }
}

// ---- create / update ---------------------------------------------------------------------------

public record SaveItemCommand(Guid? Id, string Name, decimal Price, string? Category, string? Description, string? Unit)
    : IRequest<ItemDto>;

public class SaveItemValidator : AbstractValidator<SaveItemCommand>
{
    public SaveItemValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120).WithMessage("Enter an item name (up to 120 characters).");
        RuleFor(x => x.Price).InclusiveBetween(0, 999_999).WithMessage("Enter a price between 0 and 999,999.");
        RuleFor(x => x.Category).MaximumLength(60);
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.Unit).MaximumLength(20);
    }
}

public class SaveItemHandler(IAppDbContext db, ICurrentUser current, IClock clock) : IRequestHandler<SaveItemCommand, ItemDto>
{
    public async Task<ItemDto> Handle(SaveItemCommand r, CancellationToken ct)
    {
        var wsId = current.RequireWorkspaceId();
        Item item;
        if (r.Id is { } id)
        {
            item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("That item no longer exists.");
            item.Update(r.Name, r.Price, r.Category, r.Description, r.Unit);
        }
        else
        {
            var next = (await db.Items.MaxAsync(i => (int?)i.SortOrder, ct) ?? 0) + 1;
            item = Item.Create(wsId, r.Name, r.Price, r.Category, r.Description, r.Unit, next, clock.UtcNow);
            db.Items.Add(item);
        }
        await db.SaveChangesAsync(ct);
        return ItemMapper.ToDto(item);
    }
}

// ---- availability & archive (quick toggles) ----------------------------------------------------

public record SetItemAvailabilityCommand(Guid Id, bool Available) : IRequest<ItemDto>;

public class SetItemAvailabilityHandler(IAppDbContext db) : IRequestHandler<SetItemAvailabilityCommand, ItemDto>
{
    public async Task<ItemDto> Handle(SetItemAvailabilityCommand r, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == r.Id, ct) ?? throw new NotFoundException();
        item.SetAvailability(r.Available);
        await db.SaveChangesAsync(ct);
        return ItemMapper.ToDto(item);
    }
}

public record SetItemArchivedCommand(Guid Id, bool Archived) : IRequest<ItemDto>;

public class SetItemArchivedHandler(IAppDbContext db) : IRequestHandler<SetItemArchivedCommand, ItemDto>
{
    public async Task<ItemDto> Handle(SetItemArchivedCommand r, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == r.Id, ct) ?? throw new NotFoundException();
        item.SetArchived(r.Archived); // items are archived, never deleted, because past orders reference them
        await db.SaveChangesAsync(ct);
        return ItemMapper.ToDto(item);
    }
}

// ---- option groups (sizes, flavors, add-ons — FS-008) -------------------------------------------

public record OptionInput(Guid? Id, string Name, decimal PriceDelta, bool IsAvailable, bool IsDefault);
public record OptionGroupInput(Guid? Id, string Name, string SelectionType, bool Required, int? MinSelect, int? MaxSelect, List<OptionInput> Options);

/// <summary>The editor always saves its whole "Options" section at once, so groups/options are simply replaced.</summary>
public record SaveItemOptionGroupsCommand(Guid ItemId, List<OptionGroupInput> Groups) : IRequest<ItemDto>;

public class SaveItemOptionGroupsValidator : AbstractValidator<SaveItemOptionGroupsCommand>
{
    public SaveItemOptionGroupsValidator()
    {
        RuleFor(x => x.Groups.Count).LessThanOrEqualTo(ItemOptionGroupValidator.MaxGroups)
            .WithMessage($"An item can have at most {ItemOptionGroupValidator.MaxGroups} option groups.");
        RuleForEach(x => x.Groups).ChildRules(g =>
        {
            g.RuleFor(x => x.Name).NotEmpty().MaximumLength(60).WithMessage("Every option group needs a name (up to 60 characters).");
            g.RuleFor(x => x.Options.Count).LessThanOrEqualTo(ItemOptionGroupValidator.MaxOptionsPerGroup)
                .WithMessage($"A group can have at most {ItemOptionGroupValidator.MaxOptionsPerGroup} options.");
            g.RuleForEach(x => x.Options).ChildRules(o =>
            {
                o.RuleFor(x => x.Name).NotEmpty().MaximumLength(60).WithMessage("Every option needs a name (up to 60 characters).");
                o.RuleFor(x => x.PriceDelta).InclusiveBetween(-999_999, 999_999);
            });
        });
    }
}

public class SaveItemOptionGroupsHandler(IAppDbContext db) : IRequestHandler<SaveItemOptionGroupsCommand, ItemDto>
{
    public async Task<ItemDto> Handle(SaveItemOptionGroupsCommand r, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == r.ItemId, ct) ?? throw new NotFoundException("That item no longer exists.");
        var groups = r.Groups.Select((g, gi) => new ItemOptionGroup
        {
            Id = g.Id ?? Guid.NewGuid(), Name = g.Name, SelectionType = g.SelectionType, Required = g.Required,
            MinSelect = g.MinSelect, MaxSelect = g.MaxSelect, SortOrder = gi,
            Options = g.Options.Select((o, oi) => new ItemOption
            {
                Id = o.Id ?? Guid.NewGuid(), Name = o.Name, PriceDelta = o.PriceDelta, IsAvailable = o.IsAvailable,
                IsDefault = o.IsDefault, SortOrder = oi
            }).ToList()
        }).ToList();
        item.SetOptionGroups(groups);
        await db.SaveChangesAsync(ct);
        return ItemMapper.ToDto(item);
    }
}

/// <summary>Quick "86 the large size" toggle, without resending the whole options editor.</summary>
public record SetItemOptionAvailabilityCommand(Guid ItemId, Guid GroupId, Guid OptionId, bool Available) : IRequest<ItemDto>;

public class SetItemOptionAvailabilityHandler(IAppDbContext db) : IRequestHandler<SetItemOptionAvailabilityCommand, ItemDto>
{
    public async Task<ItemDto> Handle(SetItemOptionAvailabilityCommand r, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == r.ItemId, ct) ?? throw new NotFoundException("That item no longer exists.");
        item.SetOptionAvailability(r.GroupId, r.OptionId, r.Available);
        await db.SaveChangesAsync(ct);
        return ItemMapper.ToDto(item);
    }
}

/// <summary>"Copy options from…" — duplicates another item's option groups onto this one.</summary>
public record CopyOptionGroupsCommand(Guid ItemId, Guid FromItemId) : IRequest<ItemDto>;

public class CopyOptionGroupsHandler(IAppDbContext db) : IRequestHandler<CopyOptionGroupsCommand, ItemDto>
{
    public async Task<ItemDto> Handle(CopyOptionGroupsCommand r, CancellationToken ct)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == r.ItemId, ct) ?? throw new NotFoundException("That item no longer exists.");
        var source = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == r.FromItemId, ct) ?? throw new NotFoundException("That item no longer exists.");
        item.CopyOptionGroupsFrom(source);
        await db.SaveChangesAsync(ct);
        return ItemMapper.ToDto(item);
    }
}

// ---- rapid entry: many items at once -----------------------------------------------------------

public record BulkRow(string? Name, string? Price, string? Category, string? Description, string? Unit);
/// <summary>OnDuplicate: "skip" (default) or "update". DryRun previews every row without saving anything.</summary>
public record BulkItemsCommand(List<BulkRow> Rows, string? OnDuplicate, bool DryRun) : IRequest<BulkItemsResult>;
public record BulkRowResult(int Row, string Status, string? Message);
public record BulkItemsResult(int Created, int Updated, int Skipped, int Errors, bool DryRun, List<BulkRowResult> Rows);

public static partial class PriceParser
{
    [GeneratedRegex(@"[^\d.,\-]")] private static partial Regex Junk();

    /// <summary>Accepts "12", "12.50", "12,50", "$1,200.50", "₱ 45".</summary>
    public static bool TryParse(string? text, out decimal price)
    {
        price = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = Junk().Replace(text.Trim(), "");
        if (s.Length == 0) return false;
        var lastComma = s.LastIndexOf(','); var lastDot = s.LastIndexOf('.');
        if (lastComma > lastDot)
        {
            // "12,50" (decimal comma) vs "1,200" (thousands comma)
            var decimals = s.Length - lastComma - 1;
            s = decimals is 1 or 2 ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        }
        else s = s.Replace(",", "");
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out price);
    }
}

public class BulkItemsValidator : AbstractValidator<BulkItemsCommand>
{
    public BulkItemsValidator()
    {
        RuleFor(x => x.Rows).NotEmpty().WithMessage("Add at least one row.");
        RuleFor(x => x.Rows.Count).LessThanOrEqualTo(500).WithMessage("You can add up to 500 items at a time.");
    }
}

public class BulkItemsHandler(IAppDbContext db, ICurrentUser current, IClock clock) : IRequestHandler<BulkItemsCommand, BulkItemsResult>
{
    public async Task<BulkItemsResult> Handle(BulkItemsCommand r, CancellationToken ct)
    {
        var wsId = current.RequireWorkspaceId();
        var update = string.Equals(r.OnDuplicate, "update", StringComparison.OrdinalIgnoreCase);
        var existing = await db.Items.ToListAsync(ct);
        string Key(string? name, string? cat) => $"{name?.Trim().ToLowerInvariant()}|{cat?.Trim().ToLowerInvariant()}";
        var byKey = existing.GroupBy(i => Key(i.Name, i.Category)).ToDictionary(g => g.Key, g => g.First());
        var seenInBatch = new HashSet<string>();
        var nextSort = (existing.Count == 0 ? 0 : existing.Max(i => i.SortOrder)) + 1;

        var results = new List<BulkRowResult>();
        int created = 0, updated = 0, skipped = 0, errors = 0;

        for (var i = 0; i < r.Rows.Count; i++)
        {
            var row = r.Rows[i]; var n = i + 1;
            var name = row.Name?.Trim();
            // Entirely empty rows (trailing blank grid lines) are ignored, not errors.
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(row.Price) && string.IsNullOrWhiteSpace(row.Category))
            { skipped++; results.Add(new(n, "skipped", "Empty row")); continue; }

            if (string.IsNullOrWhiteSpace(name)) { errors++; results.Add(new(n, "error", "Name is required.")); continue; }
            if (name.Length > 120) { errors++; results.Add(new(n, "error", "Name is longer than 120 characters.")); continue; }
            if (!PriceParser.TryParse(row.Price, out var price) || price < 0 || price > 999_999)
            { errors++; results.Add(new(n, "error", $"'{row.Price}' isn't a valid price.")); continue; }

            var key = Key(name, row.Category);
            if (!seenInBatch.Add(key)) { skipped++; results.Add(new(n, "skipped", "Duplicate of an earlier row in this list.")); continue; }

            try
            {
                if (byKey.TryGetValue(key, out var found))
                {
                    if (!update) { skipped++; results.Add(new(n, "skipped", "Already in your catalog.")); continue; }
                    if (!r.DryRun) found.Update(name, price, row.Category, row.Description ?? found.Description, row.Unit ?? found.Unit);
                    updated++; results.Add(new(n, "updated", null));
                }
                else
                {
                    if (!r.DryRun)
                        db.Items.Add(Item.Create(wsId, name, price, row.Category, row.Description, row.Unit, nextSort++, clock.UtcNow));
                    created++; results.Add(new(n, "created", null));
                }
            }
            catch (DomainException ex) { errors++; results.Add(new(n, "error", ex.Message)); }
        }

        if (!r.DryRun) await db.SaveChangesAsync(ct);
        return new BulkItemsResult(created, updated, skipped, errors, r.DryRun, results);
    }
}
