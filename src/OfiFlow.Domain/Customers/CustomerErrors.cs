namespace OfiFlow.Domain.Customers;

/// <summary>
/// Códigos de error del contexto Customers (ADR-011). Son contrato público: no se cambian ni se
/// reutilizan con otro significado. El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// </summary>
public static class CustomerErrors
{
    public const string NameRequired = "customer.name_required";
    public const string PhoneRequired = "customer.phone_required";
    public const string PhoneInvalid = "customer.phone_invalid";

    public const string NotFound = "customer.not_found";

    /// <summary>No se puede eliminar un cliente con trabajos que no están completados ni cancelados.</summary>
    public const string HasActiveJobs = "customer.has_active_jobs";
}
