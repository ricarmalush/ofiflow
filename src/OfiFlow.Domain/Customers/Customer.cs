using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Customers;

public sealed class Customer : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid TenantId { get; private set; }

    public CustomerType Type { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Email? Email { get; private set; }

    public PhoneNumber? Phone { get; private set; }

    public string? Address { get; private set; }

    public string? Notes { get; private set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    private Customer()
    {
        // Requerido por EF Core.
    }

    private Customer(
        Guid id,
        Guid tenantId,
        CustomerType type,
        string name,
        Email? email,
        PhoneNumber? phone,
        string? address,
        string? notes)
        : base(id)
    {
        TenantId = tenantId;
        Type = type;
        Name = name;
        Email = email;
        Phone = phone;
        Address = address;
        Notes = notes;
    }

    public static Customer Create(
        Guid tenantId,
        CustomerType type,
        string name,
        Email? email,
        PhoneNumber? phone,
        string? address,
        string? notes)
    {
        ValidateName(name);

        return new Customer(Guid.NewGuid(), tenantId, type, name.Trim(), email, phone, address, notes);
    }

    public void UpdateContactInfo(string name, Email? email, PhoneNumber? phone, string? address, string? notes)
    {
        ValidateName(name);

        Name = name.Trim();
        Email = email;
        Phone = phone;
        Address = address;
        Notes = notes;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del cliente es obligatorio.", nameof(name));
        }
    }
}
