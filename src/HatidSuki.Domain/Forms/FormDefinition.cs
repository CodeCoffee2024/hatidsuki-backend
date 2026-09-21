using System.Text.Json;
using System.Text.Json.Serialization;

namespace HatidSuki.Domain.Forms;

public static class FieldTypes
{
    public const string ShortText = "shortText", Paragraph = "paragraph", Number = "number", Email = "email",
        Phone = "phone", SingleChoice = "singleChoice", Dropdown = "dropdown", MultiChoice = "multiChoice",
        Date = "date", Time = "time", Consent = "consent", Heading = "heading", OrderItems = "orderItems";

    public static readonly HashSet<string> All =
    [
        ShortText, Paragraph, Number, Email, Phone, SingleChoice, Dropdown, MultiChoice, Date, Time, Consent, Heading, OrderItems
    ];
}

/// <summary>Meaning of a field to the rest of the app (customer records, notifications).</summary>
public static class FieldRoles
{
    public const string CustomerName = "customerName", CustomerEmail = "customerEmail",
        CustomerPhone = "customerPhone", Notes = "notes", Address = "address", FulfillmentDate = "fulfillmentDate";

    public static readonly HashSet<string> All = [CustomerName, CustomerEmail, CustomerPhone, Notes, Address, FulfillmentDate];
}

public class OrderItemsSettings
{
    /// <summary>"all" = every orderable item in the catalog; "selected" = only <see cref="ItemIds"/>.</summary>
    public string Source { get; set; } = "all";
    public List<Guid> ItemIds { get; set; } = [];
    /// <summary>Lets a customer add several people to one order (each person's order is tracked separately).</summary>
    public bool AllowGroupOrders { get; set; } = true;
    public int MaxParts { get; set; } = 30;
    public bool AllowLineNotes { get; set; }
}

public class FormField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = FieldTypes.ShortText;
    public string Label { get; set; } = "";
    public string? HelpText { get; set; }
    public bool Required { get; set; }
    public string? Role { get; set; }
    public List<string> Options { get; set; } = [];
    public OrderItemsSettings? OrderItems { get; set; }
}

public class FormDefinition
{
    public string? Intro { get; set; }
    public string? ThankYou { get; set; }
    /// <summary>Shown to customers, e.g. "Pay cash when you pick up".</summary>
    public string? PaymentMessage { get; set; } = "Payment is cash on pickup.";
    public string? ClosedMessage { get; set; }
    public List<FormField> Fields { get; set; } = [];

    [JsonIgnore]
    public FormField? OrderItemsField => Fields.FirstOrDefault(f => f.Type == FieldTypes.OrderItems);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static FormDefinition FromJson(string json) =>
        JsonSerializer.Deserialize<FormDefinition>(json, Options) ?? new FormDefinition();

    /// <summary>Structural problems that make a definition invalid to save at all.</summary>
    public List<string> StructuralProblems()
    {
        var problems = new List<string>();
        if (Fields.Count > 60) problems.Add("A form can have at most 60 fields.");
        if (Fields.Select(f => f.Id).Distinct().Count() != Fields.Count) problems.Add("Field ids must be unique.");
        foreach (var f in Fields)
        {
            if (!FieldTypes.All.Contains(f.Type)) problems.Add($"Unknown field type '{f.Type}'.");
            if (string.IsNullOrWhiteSpace(f.Label)) problems.Add("Every field needs a label.");
            if (f.Role is not null && !FieldRoles.All.Contains(f.Role)) problems.Add($"Unknown field role '{f.Role}'.");
            var isChoice = f.Type is FieldTypes.SingleChoice or FieldTypes.Dropdown or FieldTypes.MultiChoice;
            if (isChoice && (f.Options.Count == 0 || f.Options.Count > 50))
                problems.Add($"'{f.Label}' needs between 1 and 50 options.");
        }
        var duplicateRoles = Fields.Where(f => f.Role is not null).GroupBy(f => f.Role).Where(g => g.Count() > 1).Select(g => g.Key);
        problems.AddRange(duplicateRoles.Select(r => $"Only one field can have the role '{r}'."));
        if (Fields.Count(f => f.Type == FieldTypes.OrderItems) > 1) problems.Add("A form can have only one order-items field.");
        return problems;
    }
}
