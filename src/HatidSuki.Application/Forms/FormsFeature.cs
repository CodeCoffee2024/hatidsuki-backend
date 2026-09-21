using System.Security.Cryptography;
using FluentValidation;
using HatidSuki.Application.Common;
using HatidSuki.Domain;
using HatidSuki.Domain.Forms;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HatidSuki.Application.Forms;

public record FormListDto(Guid Id, string Name, string Status, string ShortCode, int? PublishedVersion,
    bool HasUnpublishedChanges, int OrdersCount, string PublicUrl);

public record FormDetailDto(Guid Id, string Name, string Status, string ShortCode, int? PublishedVersion,
    bool HasUnpublishedChanges, FormDefinition Draft, string PublicUrl);

public static class ShortCodes
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // no look-alike characters (0/o, 1/l/i)

    public static string Generate(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }
}

public static class FormLinks
{
    public static string Public(string baseUrl, string code, string? sourceCode = null) =>
        $"{baseUrl.TrimEnd('/')}/q/{code}" + (sourceCode is null ? "" : $"?s={sourceCode}");
}

public static class FormTemplates
{
    /// <summary>A sensible starting form: who you are, when you need it, what you want, and notes.</summary>
    public static FormDefinition Simple() => new()
    {
        Intro = "Pick what you'd like and we'll get it ready.",
        ThankYou = "Thank you! We've received your order.",
        PaymentMessage = "Payment is cash on pickup.",
        Fields =
        [
            new FormField { Type = FieldTypes.ShortText, Label = "Your name", Required = true, Role = FieldRoles.CustomerName },
            new FormField { Type = FieldTypes.Phone, Label = "Phone number", Required = true, Role = FieldRoles.CustomerPhone },
            new FormField { Type = FieldTypes.ShortText, Label = "When do you need it?", HelpText = "For example: today 5pm" },
            new FormField
            {
                Type = FieldTypes.OrderItems, Label = "Your order", Required = true,
                OrderItems = new OrderItemsSettings { Source = "all", AllowGroupOrders = true }
            },
            new FormField { Type = FieldTypes.Paragraph, Label = "Anything we should know?", Role = FieldRoles.Notes }
        ]
    };
}

/// <summary>The checks a form must pass before customers can see it.</summary>
public static class FormPublishChecker
{
    public static async Task<List<string>> CheckAsync(IAppDbContext db, FormDefinition def, CancellationToken ct)
    {
        var problems = def.StructuralProblems();
        if (def.Fields.Count == 0) problems.Add("Add at least one field.");
        if (!def.Fields.Any(f => f.Role is FieldRoles.CustomerPhone or FieldRoles.CustomerEmail))
            problems.Add("Add a phone or email field (marked as the customer's contact) so you can reach the customer.");
        if (!def.Fields.Any(f => f.Role == FieldRoles.CustomerName))
            problems.Add("Add a name field (marked as the customer's name) so you know who ordered.");

        // Every order form asks where to deliver, so there has to be somewhere to choose.
        if (!await db.DeliveryLocations.AnyAsync(l => l.IsActive, ct))
            problems.Add("Add at least one delivery location on the Delivery page. Every order form asks customers where to deliver.");

        if (def.OrderItemsField?.OrderItems is { } s)
        {
            var orderable = await Public.ItemResolver.ResolveAsync(db, s, ct);
            if (!orderable.Any(i => i.IsAvailable)) problems.Add("Your order form has no available items yet. Add items to your catalog first.");
        }
        return problems;
    }
}

internal static class FormMapper
{
    public static async Task<bool> HasUnpublishedChangesAsync(IAppDbContext db, Form f, CancellationToken ct)
    {
        if (f.PublishedVersion is null) return true;
        var published = await db.FormVersions.AsNoTracking()
            .Where(v => v.FormId == f.Id && v.Number == f.PublishedVersion).Select(v => v.DefinitionJson).FirstOrDefaultAsync(ct);
        return published != f.DraftJson;
    }
}

// ---- list / get --------------------------------------------------------------------------------

public record ListFormsQuery : IRequest<List<FormListDto>>;

public class ListFormsHandler(IAppDbContext db, IOptions<AppOptions> options) : IRequestHandler<ListFormsQuery, List<FormListDto>>
{
    public async Task<List<FormListDto>> Handle(ListFormsQuery q, CancellationToken ct)
    {
        var forms = await db.Forms.AsNoTracking().OrderByDescending(f => f.UpdatedAtUtc).ToListAsync(ct);
        var ids = forms.Select(f => f.Id).ToList();
        var versions = await db.FormVersions.AsNoTracking().Where(v => ids.Contains(v.FormId))
            .Select(v => new { v.FormId, v.Number, v.DefinitionJson }).ToListAsync(ct);
        var counts = await db.Orders.AsNoTracking().Where(o => ids.Contains(o.FormId))
            .GroupBy(o => o.FormId).Select(g => new { FormId = g.Key, Count = g.Count() }).ToListAsync(ct);

        return forms.Select(f =>
        {
            var published = versions.FirstOrDefault(v => v.FormId == f.Id && v.Number == f.PublishedVersion)?.DefinitionJson;
            return new FormListDto(f.Id, f.Name, f.Status.ToString(), f.ShortCode, f.PublishedVersion,
                published is null || published != f.DraftJson, counts.FirstOrDefault(c => c.FormId == f.Id)?.Count ?? 0,
                FormLinks.Public(options.Value.PublicBaseUrl, f.ShortCode));
        }).ToList();
    }
}

public record GetFormQuery(Guid Id) : IRequest<FormDetailDto>;

public class GetFormHandler(IAppDbContext db, IOptions<AppOptions> options) : IRequestHandler<GetFormQuery, FormDetailDto>
{
    public async Task<FormDetailDto> Handle(GetFormQuery q, CancellationToken ct)
    {
        var f = await db.Forms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == q.Id, ct) ?? throw new NotFoundException("That form doesn't exist.");
        return new FormDetailDto(f.Id, f.Name, f.Status.ToString(), f.ShortCode, f.PublishedVersion,
            await FormMapper.HasUnpublishedChangesAsync(db, f, ct), FormDefinition.FromJson(f.DraftJson),
            FormLinks.Public(options.Value.PublicBaseUrl, f.ShortCode));
    }
}

// ---- create / edit draft -----------------------------------------------------------------------

public record CreateFormCommand(string Name) : IRequest<FormDetailDto>;

public class CreateFormValidator : AbstractValidator<CreateFormCommand>
{
    public CreateFormValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(80).WithMessage("Give your form a name.");
}

public class CreateFormHandler(IAppDbContext db, ICurrentUser current, IClock clock, IOptions<AppOptions> options)
    : IRequestHandler<CreateFormCommand, FormDetailDto>
{
    public async Task<FormDetailDto> Handle(CreateFormCommand r, CancellationToken ct)
    {
        var wsId = current.RequireWorkspaceId();
        string code;
        do code = ShortCodes.Generate(8);
        while (await db.Forms.IgnoreQueryFilters().AnyAsync(f => f.ShortCode == code, ct));

        var form = Form.Create(wsId, r.Name, code, FormTemplates.Simple(), clock.UtcNow);
        db.Forms.Add(form);
        await db.SaveChangesAsync(ct);
        return new FormDetailDto(form.Id, form.Name, form.Status.ToString(), form.ShortCode, null, true,
            FormDefinition.FromJson(form.DraftJson), FormLinks.Public(options.Value.PublicBaseUrl, form.ShortCode));
    }
}

public record UpdateFormDraftCommand(Guid Id, string Name, FormDefinition Definition) : IRequest<FormDetailDto>;

public class UpdateFormDraftValidator : AbstractValidator<UpdateFormDraftCommand>
{
    public UpdateFormDraftValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Definition).NotNull();
    }
}

public class UpdateFormDraftHandler(IAppDbContext db, IClock clock, IOptions<AppOptions> options)
    : IRequestHandler<UpdateFormDraftCommand, FormDetailDto>
{
    public async Task<FormDetailDto> Handle(UpdateFormDraftCommand r, CancellationToken ct)
    {
        var f = await db.Forms.FirstOrDefaultAsync(x => x.Id == r.Id, ct) ?? throw new NotFoundException("That form doesn't exist.");
        f.Rename(r.Name, clock.UtcNow);
        f.UpdateDraft(r.Definition, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return new FormDetailDto(f.Id, f.Name, f.Status.ToString(), f.ShortCode, f.PublishedVersion,
            await FormMapper.HasUnpublishedChangesAsync(db, f, ct), FormDefinition.FromJson(f.DraftJson),
            FormLinks.Public(options.Value.PublicBaseUrl, f.ShortCode));
    }
}

// ---- publish / close / reopen ------------------------------------------------------------------

public record PublishResult(bool Published, List<string> Problems, FormDetailDto? Form);
public record PublishFormCommand(Guid Id) : IRequest<PublishResult>;

public class PublishFormHandler(IAppDbContext db, IClock clock, IOptions<AppOptions> options) : IRequestHandler<PublishFormCommand, PublishResult>
{
    public async Task<PublishResult> Handle(PublishFormCommand r, CancellationToken ct)
    {
        var f = await db.Forms.FirstOrDefaultAsync(x => x.Id == r.Id, ct) ?? throw new NotFoundException("That form doesn't exist.");
        var problems = await FormPublishChecker.CheckAsync(db, FormDefinition.FromJson(f.DraftJson), ct);
        if (problems.Count > 0) return new PublishResult(false, problems, null);

        f.Publish(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return new PublishResult(true, [], new FormDetailDto(f.Id, f.Name, f.Status.ToString(), f.ShortCode, f.PublishedVersion,
            false, FormDefinition.FromJson(f.DraftJson), FormLinks.Public(options.Value.PublicBaseUrl, f.ShortCode)));
    }
}

public record SetFormStatusCommand(Guid Id, string Action) : IRequest<FormListDto>;

public class SetFormStatusHandler(IAppDbContext db, IClock clock, IOptions<AppOptions> options) : IRequestHandler<SetFormStatusCommand, FormListDto>
{
    public async Task<FormListDto> Handle(SetFormStatusCommand r, CancellationToken ct)
    {
        var f = await db.Forms.FirstOrDefaultAsync(x => x.Id == r.Id, ct) ?? throw new NotFoundException("That form doesn't exist.");
        if (r.Action == "close") f.Close(clock.UtcNow);
        else if (r.Action == "reopen") f.Reopen(clock.UtcNow);
        else throw new DomainException("Unknown action.");
        await db.SaveChangesAsync(ct);
        return new FormListDto(f.Id, f.Name, f.Status.ToString(), f.ShortCode, f.PublishedVersion,
            await FormMapper.HasUnpublishedChangesAsync(db, f, ct), await db.Orders.CountAsync(o => o.FormId == f.Id, ct),
            FormLinks.Public(options.Value.PublicBaseUrl, f.ShortCode));
    }
}

// ---- sources (one QR per table / flyer / post) -------------------------------------------------

public record SourceDto(Guid Id, string Name, string Code, bool IsActive, int ScanCount, int OrdersCount, string Url);

public record ListSourcesQuery(Guid FormId) : IRequest<List<SourceDto>>;

public class ListSourcesHandler(IAppDbContext db, IOptions<AppOptions> options) : IRequestHandler<ListSourcesQuery, List<SourceDto>>
{
    public async Task<List<SourceDto>> Handle(ListSourcesQuery q, CancellationToken ct)
    {
        var form = await db.Forms.AsNoTracking().FirstOrDefaultAsync(f => f.Id == q.FormId, ct) ?? throw new NotFoundException();
        var sources = await db.FormSources.AsNoTracking().Where(s => s.FormId == q.FormId).OrderBy(s => s.CreatedAtUtc).ThenBy(s => s.Name).ToListAsync(ct);
        var counts = await db.Orders.AsNoTracking().Where(o => o.FormId == q.FormId && o.SourceCode != null)
            .GroupBy(o => o.SourceCode).Select(g => new { Code = g.Key, Count = g.Count() }).ToListAsync(ct);
        return sources.Select(s => new SourceDto(s.Id, s.Name, s.Code, s.IsActive, s.ScanCount,
            counts.FirstOrDefault(c => c.Code == s.Code)?.Count ?? 0,
            FormLinks.Public(options.Value.PublicBaseUrl, form.ShortCode, s.Code))).ToList();
    }
}

/// <summary>Add named sources, and/or a numbered run such as "Table 1" to "Table 20".</summary>
public record CreateSourcesCommand(Guid FormId, List<string>? Names, string? Prefix, int? From, int? To) : IRequest<List<SourceDto>>;

public class CreateSourcesHandler(IAppDbContext db, ICurrentUser current, IClock clock, IOptions<AppOptions> options)
    : IRequestHandler<CreateSourcesCommand, List<SourceDto>>
{
    public async Task<List<SourceDto>> Handle(CreateSourcesCommand r, CancellationToken ct)
    {
        var wsId = current.RequireWorkspaceId();
        var form = await db.Forms.FirstOrDefaultAsync(f => f.Id == r.FormId, ct) ?? throw new NotFoundException();

        var names = (r.Names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        if (r.From is { } from && r.To is { } to)
        {
            if (to < from || to - from >= 200) throw new DomainException("Choose a range of up to 200 numbers.");
            var prefix = string.IsNullOrWhiteSpace(r.Prefix) ? "Table" : r.Prefix.Trim();
            for (var n = from; n <= to; n++) names.Add($"{prefix} {n}");
        }
        if (names.Count == 0) throw new DomainException("Enter at least one name.");
        if (names.Any(n => n.Length > 60)) throw new DomainException("Names can be up to 60 characters.");

        var existing = await db.FormSources.Where(s => s.FormId == form.Id).ToListAsync(ct);
        var known = existing.Select(s => s.Name.ToLowerInvariant()).ToHashSet();
        if (existing.Count + names.Count > 300) throw new DomainException("A form can have up to 300 sources.");

        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => !known.Contains(n.ToLowerInvariant())))
        {
            string code;
            do code = ShortCodes.Generate(6);
            while (await db.FormSources.IgnoreQueryFilters().AnyAsync(s => s.FormId == form.Id && s.Code == code, ct));
            db.FormSources.Add(FormSource.Create(wsId, form.Id, name, code, clock.UtcNow));
        }
        await db.SaveChangesAsync(ct);
        return await new ListSourcesHandler(db, options).Handle(new ListSourcesQuery(form.Id), ct);
    }
}

public record SetSourceActiveCommand(Guid FormId, Guid SourceId, bool Active) : IRequest;

public class SetSourceActiveHandler(IAppDbContext db) : IRequestHandler<SetSourceActiveCommand>
{
    public async Task Handle(SetSourceActiveCommand r, CancellationToken ct)
    {
        var s = await db.FormSources.FirstOrDefaultAsync(x => x.Id == r.SourceId && x.FormId == r.FormId, ct) ?? throw new NotFoundException();
        s.SetActive(r.Active);
        await db.SaveChangesAsync(ct);
    }
}

// ---- QR code -----------------------------------------------------------------------------------

public record QrResult(byte[] Bytes, string ContentType, string Url);
public record GetQrQuery(Guid FormId, Guid? SourceId, string Format, int Size) : IRequest<QrResult>;

public class GetQrHandler(IAppDbContext db, IQrCodeService qr, IOptions<AppOptions> options) : IRequestHandler<GetQrQuery, QrResult>
{
    public async Task<QrResult> Handle(GetQrQuery r, CancellationToken ct)
    {
        var form = await db.Forms.AsNoTracking().FirstOrDefaultAsync(f => f.Id == r.FormId, ct) ?? throw new NotFoundException();
        if (form.PublishedVersion is null) throw new ConflictException("Publish your form first, then you can get its QR code.");
        string? sourceCode = null;
        if (r.SourceId is { } sid)
            sourceCode = (await db.FormSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.FormId == form.Id, ct) ?? throw new NotFoundException()).Code;

        // The QR encodes the immutable short link, so printed codes survive any rename.
        var url = FormLinks.Public(options.Value.PublicBaseUrl, form.ShortCode, sourceCode);
        if (string.Equals(r.Format, "png", StringComparison.OrdinalIgnoreCase))
            return new QrResult(qr.Png(url, Math.Clamp(r.Size / 30, 4, 40)), "image/png", url);
        return new QrResult(System.Text.Encoding.UTF8.GetBytes(qr.Svg(url)), "image/svg+xml", url);
    }
}
