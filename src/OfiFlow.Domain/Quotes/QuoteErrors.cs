namespace OfiFlow.Domain.Quotes;

/// <summary>
/// Códigos de error del contexto Quotes (ADR-011). Son contrato público: no se cambian ni se reutilizan con otro significado.
/// El texto de cada uno está en Api/Resources/ErrorMessages.resx.
/// </summary>
public static class QuoteErrors
{
    public const string NotFound = "quote.not_found";

    /// <summary>Argumento 0: estado actual (no va en el texto). Solo un borrador se modifica (R4); un presupuesto enviado o resuelto es inmutable.</summary>
    public const string NotEditable = "quote.not_editable";

    /// <summary>Argumento 0: estado actual (no va en el texto). Solo un borrador se elimina (R4).</summary>
    public const string CannotDelete = "quote.cannot_delete";

    /// <summary>Argumento 0: estado actual (no va en el texto). Solo un borrador se envía (R7).</summary>
    public const string CannotSend = "quote.cannot_send";

    /// <summary>Argumento 0: estado actual (no va en el texto). Solo un presupuesto enviado se acepta (R8).</summary>
    public const string CannotAccept = "quote.cannot_accept";

    /// <summary>Argumento 0: estado actual (no va en el texto). Solo un presupuesto enviado se rechaza (R8).</summary>
    public const string CannotReject = "quote.cannot_reject";

    /// <summary>Un presupuesto sin ninguna línea no se envía (R7).</summary>
    public const string Empty = "quote.empty";

    /// <summary>El trabajo enlazado es de otro cliente que el presupuesto (R1).</summary>
    public const string JobCustomerMismatch = "quote.job_customer_mismatch";

    /// <summary>No se pudo asignar un número tras los reintentos (R6): hubo demasiadas creaciones simultáneas.</summary>
    public const string NumberUnavailable = "quote.number_unavailable";

    /// <summary>Alguien modificó el presupuesto mientras tanto (R10): no se pisa nada, se recarga.</summary>
    public const string ChangedMeanwhile = "quote.changed_meanwhile";

    /// <summary>Un error de campo: el tipo de IVA no está en la lista cerrada (R3).</summary>
    public const string VatInvalid = "quote.vat_invalid";

    /// <summary>Un error de campo: la cantidad no es mayor que 0, pasa de 3 decimales o del máximo (R3).</summary>
    public const string QuantityInvalid = "quote.quantity_invalid";

    /// <summary>Un error de campo: la fecha de validez no está entre los años 2000 y 2099 (R2).</summary>
    public const string ValidUntilInvalid = "quote.valid_until_invalid";

    /// <summary>
    /// La empresa no ha guardado su perfil fiscal (spec 024), así que no hay PDF que generar (spec 025 R3): un PDF «a medias», con el emisor en blanco, no sirve para dárselo a un cliente. Es un error de
    /// <b>negocio</b>, no de campo: no hay ningún campo de la petición que corregir.
    /// </summary>
    public const string CompanyProfileIncomplete = "quote.company_profile_incomplete";
}
