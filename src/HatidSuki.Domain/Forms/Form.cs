namespace HatidSuki.Domain.Forms;

public class Form : Entity, ITenantEntity
{
    private Form() { }

    public Guid WorkspaceId { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>Immutable short code encoded in QR codes (/q/{ShortCode}); never changes.</summary>
    public string ShortCode { get; private set; } = "";
    public FormStatus Status { get; private set; } = FormStatus.Draft;
    public string DraftJson { get; private set; } = "{}";
    public int? PublishedVersion { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public List<FormVersion> Versions { get; private set; } = [];

    public static Form Create(Guid workspaceId, string name, string shortCode, FormDefinition definition, DateTime now) => new()
    {
        WorkspaceId = workspaceId, Name = name.Trim(), ShortCode = shortCode,
        DraftJson = definition.ToJson(), CreatedAtUtc = now, UpdatedAtUtc = now
    };

    public void Rename(string name, DateTime now) { Name = name.Trim(); UpdatedAtUtc = now; }

    public void UpdateDraft(FormDefinition definition, DateTime now)
    {
        var problems = definition.StructuralProblems();
        if (problems.Count > 0) throw new DomainException(string.Join(" ", problems));
        DraftJson = definition.ToJson();
        UpdatedAtUtc = now;
    }

    /// <summary>Freezes the current draft as a new immutable version. Customers see it immediately.</summary>
    public FormVersion Publish(DateTime now)
    {
        var version = FormVersion.Create(this, (PublishedVersion ?? 0) + 1, DraftJson, now);
        Versions.Add(version);
        PublishedVersion = version.Number;
        PublishedAtUtc = now;
        Status = FormStatus.Published;
        UpdatedAtUtc = now;
        return version;
    }

    public void Close(DateTime now)
    {
        if (Status != FormStatus.Published) throw new DomainException("Only a published form can be closed.");
        Status = FormStatus.Closed;
        UpdatedAtUtc = now;
    }

    public void Reopen(DateTime now)
    {
        if (Status != FormStatus.Closed) throw new DomainException("Only a closed form can be reopened.");
        Status = FormStatus.Published;
        UpdatedAtUtc = now;
    }
}

/// <summary>Immutable snapshot of a form at publish time. Every order records the version it was placed on.</summary>
public class FormVersion : Entity, ITenantEntity
{
    private FormVersion() { }
    public Guid WorkspaceId { get; private set; }
    public Guid FormId { get; private set; }
    public int Number { get; private set; }
    public string DefinitionJson { get; private set; } = "{}";
    public DateTime PublishedAtUtc { get; private set; }

    public static FormVersion Create(Form form, int number, string json, DateTime now) => new()
    {
        WorkspaceId = form.WorkspaceId, FormId = form.Id, Number = number, DefinitionJson = json, PublishedAtUtc = now
    };
}

/// <summary>A named QR/link tag ("Table 3", "Flyer") recorded on every order placed through it.</summary>
public class FormSource : Entity, ITenantEntity
{
    private FormSource() { }
    public Guid WorkspaceId { get; private set; }
    public Guid FormId { get; private set; }
    public string Name { get; private set; } = "";
    public string Code { get; private set; } = "";
    public bool IsActive { get; private set; } = true;
    public int ScanCount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static FormSource Create(Guid workspaceId, Guid formId, string name, string code, DateTime now) =>
        new() { WorkspaceId = workspaceId, FormId = formId, Name = name.Trim(), Code = code, CreatedAtUtc = now };

    public void SetActive(bool active) => IsActive = active;

    /// <summary>Counts one page open. An aggregate number only: no IP address or personal data is kept.</summary>
    public void RecordScan() => ScanCount++;
}
