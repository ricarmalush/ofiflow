using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Quotes;

/// <summary>
/// Un presupuesto (spec 023): el documento con el que una empresa ofrece un precio a un cliente. Es la raíz de su agregado: las líneas
/// (<see cref="QuoteItem"/>) viven y mueren con él. Las relaciones con el cliente y el trabajo van por Id, sin navegación (ADR-006).
/// <para>
/// <b>Todo lo que importa se decide aquí:</b> solo un borrador cambia (R4), los totales salen de una única función (R5), y las transiciones
/// están prohibidas desde cualquier estado que no sea el suyo (R7, R8). Application valida la <i>forma</i> de la petición antes de llegar; un
/// valor fuera de cotas que llegara igualmente es un error de programación y lanza una excepción de argumento, no un código de negocio.
/// </para>
/// </summary>
public sealed class Quote : AggregateRoot, ITenantOwned, IAuditable
{
    // Cotas: fuente única para el dominio, los validadores y EF Core (ADR-009 R3). Con ellas ninguna suma ni multiplicación desborda un long:
    // 100 líneas × (99 999,999 × 9 999 999 999 céntimos ≈ 1e15) ≈ 1e17, frente a los 9,2e18 de un long.
    public const int TitleMaxLength = 200;
    public const int NotesMaxLength = 2000;
    public const int DescriptionMaxLength = 500;
    public const int MaxItems = 100;
    public const decimal QuantityMax = 99_999.999m;
    public const int QuantityDecimals = 3;
    public const long UnitPriceCentsMax = 9_999_999_999;

    // La validez es una fecha de calendario, sin hora ni huso (D8): el intervalo es [Min, Max].
    public static readonly DateOnly ValidUntilMin = new(2000, 1, 1);
    public static readonly DateOnly ValidUntilMax = new(2099, 12, 31);

    private readonly List<QuoteItem> _items = [];

    public Guid TenantId { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid? JobId { get; private set; }

    /// <summary>Correlativo dentro de la empresa, asignado al crear (R6). Único por <c>(TenantId, Number)</c>.</summary>
    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    public DateOnly? ValidUntil { get; private set; }

    public QuoteStatus Status { get; private set; }

    // Calculados por Recalculate (R5) y guardados para poder listar sin cargar las líneas. Nada de fuera los fija.
    public long NetCents { get; private set; }

    public long VatCents { get; private set; }

    public long TotalCents { get; private set; }

    public DateTime? SentAt { get; private set; }

    public DateTime? DecidedAt { get; private set; }

    /// <summary>
    /// Sube con <b>cada</b> cambio (editar, enviar, aceptar, rechazar). Así la fila del presupuesto se actualiza siempre, aunque solo cambien
    /// las líneas, y el control de concurrencia (<see cref="RowVersion"/>) la vigila: una edición y un envío simultáneos nunca dejan un
    /// presupuesto enviado con líneas cambiadas (R10).
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>La versión de fila (control de concurrencia optimista, D9). La rellena SQL Server; el cliente la devuelve al modificar.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    /// <summary>Una vista de solo lectura: quien la reciba no puede añadir ni quitar líneas a un presupuesto (ni siquiera con un <c>cast</c> a <c>List</c>).</summary>
    public IReadOnlyList<QuoteItem> Items => _items.AsReadOnly();

    private Quote()
    {
        // Requerido por EF Core.
    }

    private Quote(Guid id, Guid tenantId, Guid customerId, int number)
        : base(id)
    {
        TenantId = tenantId;
        CustomerId = customerId;
        Number = number;
        Status = QuoteStatus.Draft;
    }

    /// <summary>Crea un borrador. El número lo asigna Application (R6); un borrador puede nacer sin líneas (R7 exige al menos una solo al enviar).</summary>
    public static Quote Create(
        Guid tenantId,
        Guid customerId,
        Guid? jobId,
        int number,
        string title,
        string? notes,
        DateOnly? validUntil,
        IReadOnlyList<QuoteItemInput> items)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);

        var quote = new Quote(Guid.NewGuid(), tenantId, customerId, number);
        quote.Apply(jobId, title, notes, validUntil, items);

        return quote;
    }

    /// <summary>
    /// Sustituye la cabecera y <b>todas</b> las líneas a la vez (D6). Solo en <c>Draft</c> (R4). El cliente no cambia: se corrige un presupuesto a
    /// un cliente equivocado eliminándolo y creando otro.
    /// </summary>
    public void Update(Guid? jobId, string title, string? notes, DateOnly? validUntil, IReadOnlyList<QuoteItemInput> items)
    {
        if (Status != QuoteStatus.Draft)
        {
            throw new DomainException(QuoteErrors.NotEditable, Status);
        }

        Apply(jobId, title, notes, validUntil, items);
    }

    /// <summary>Se comprueba antes de eliminar un borrador (R4): un presupuesto enviado es un compromiso y no se borra.</summary>
    public void EnsureCanBeDeleted()
    {
        if (Status != QuoteStatus.Draft)
        {
            throw new DomainException(QuoteErrors.CannotDelete, Status);
        }
    }

    /// <summary>Draft → Sent (R7): exige al menos una línea y deja el instante del envío. «Enviar» solo anota: no manda nada (D1).</summary>
    public void Send(DateTime nowUtc)
    {
        if (Status != QuoteStatus.Draft)
        {
            throw new DomainException(QuoteErrors.CannotSend, Status);
        }

        if (_items.Count == 0)
        {
            throw new DomainException(QuoteErrors.Empty);
        }

        Status = QuoteStatus.Sent;
        SentAt = nowUtc;
        Revision++;
    }

    /// <summary>Sent → Accepted (R8). Un borrador no se acepta y uno resuelto no se reabre.</summary>
    public void Accept(DateTime nowUtc)
    {
        if (Status != QuoteStatus.Sent)
        {
            throw new DomainException(QuoteErrors.CannotAccept, Status);
        }

        Status = QuoteStatus.Accepted;
        DecidedAt = nowUtc;
        Revision++;
    }

    /// <summary>Sent → Rejected (R8).</summary>
    public void Reject(DateTime nowUtc)
    {
        if (Status != QuoteStatus.Sent)
        {
            throw new DomainException(QuoteErrors.CannotReject, Status);
        }

        Status = QuoteStatus.Rejected;
        DecidedAt = nowUtc;
        Revision++;
    }

    /// <summary>
    /// El desglose por tipo de IVA, calculado en el momento a partir de las líneas (no se guarda: se deriva). Los totales guardados
    /// (<see cref="NetCents"/>, <see cref="VatCents"/>, <see cref="TotalCents"/>) salen de este mismo cálculo.
    /// </summary>
    public QuoteTotals CalculateTotals() => QuoteCalculator.Calculate(_items.Select(ToInput));

    private void Apply(Guid? jobId, string title, string? notes, DateOnly? validUntil, IReadOnlyList<QuoteItemInput> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(title.Trim().Length, TitleMaxLength, nameof(title));

        if (notes is not null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(notes.Length, NotesMaxLength, nameof(notes));
        }

        if (validUntil is { } date)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(date, ValidUntilMin, nameof(validUntil));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(date, ValidUntilMax, nameof(validUntil));
        }

        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, MaxItems, nameof(items));

        foreach (var item in items)
        {
            ValidateItem(item);
        }

        JobId = jobId;
        Title = title.Trim();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        ValidUntil = validUntil;

        _items.Clear();
        for (var position = 0; position < items.Count; position++)
        {
            _items.Add(new QuoteItem(TenantId, Id, position, items[position]));
        }

        Recalculate();
        Revision++;
    }

    /// <summary>La única vía a los totales guardados (R5): los fija a partir de las líneas y de nada más.</summary>
    private void Recalculate()
    {
        var totals = QuoteCalculator.Calculate(_items.Select(ToInput));

        NetCents = totals.NetCents;
        VatCents = totals.VatCents;
        TotalCents = totals.TotalCents;
    }

    private static QuoteItemInput ToInput(QuoteItem item) => new(item.Description, item.Quantity, item.UnitPriceCents, item.VatPercent);

    private static void ValidateItem(QuoteItemInput item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Description, nameof(item.Description));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(item.Description.Trim().Length, DescriptionMaxLength, nameof(item.Description));

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(item.Quantity, 0m, nameof(item.Quantity));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(item.Quantity, QuantityMax, nameof(item.Quantity));
        ArgumentOutOfRangeException.ThrowIfNotEqual(decimal.Round(item.Quantity, QuantityDecimals), item.Quantity, nameof(item.Quantity));

        ArgumentOutOfRangeException.ThrowIfNegative(item.UnitPriceCents, nameof(item.UnitPriceCents));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(item.UnitPriceCents, UnitPriceCentsMax, nameof(item.UnitPriceCents));

        if (!VatRates.IsAllowed(item.VatPercent))
        {
            throw new ArgumentOutOfRangeException(nameof(item.VatPercent), item.VatPercent, "El tipo de IVA no está en la lista cerrada.");
        }
    }
}
