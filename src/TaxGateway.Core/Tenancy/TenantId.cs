namespace TaxGateway.Core.Tenancy;

/// <summary>Identificador do tenant (UUIDv7 gerado na aplicação, SDD §9).</summary>
public readonly record struct TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("TenantId não pode ser vazio.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static TenantId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
