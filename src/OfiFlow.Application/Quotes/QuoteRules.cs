using FluentValidation;
using OfiFlow.Domain.Quotes;

namespace OfiFlow.Application.Quotes;

/// <summary>
/// Las reglas de forma de la cabecera y las líneas de un presupuesto (spec 023 R2 y R3), compartidas por crear y modificar: así no pueden divergir.
/// Las cotas son las constantes de <see cref="Quote"/>, la misma fuente que usan el dominio y EF Core.
/// </summary>
public static class QuoteRules
{
    public static IRuleBuilderOptions<T, string?> ValidQuoteTitle<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().MaximumLength(Quote.TitleMaxLength);

    public static IRuleBuilderOptions<T, string?> ValidQuoteNotes<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(Quote.NotesMaxLength);

    /// <summary>Una fecha de calendario entre los años 2000 y 2099 (D8). Una fecha pasada <b>no</b> se rechaza (R9).</summary>
    public static IRuleBuilderOptions<T, DateOnly> ValidQuoteValidUntil<T>(this IRuleBuilder<T, DateOnly> rule) =>
        rule.Must(date => date >= Quote.ValidUntilMin && date <= Quote.ValidUntilMax).WithErrorCode(QuoteErrors.ValidUntilInvalid);

    /// <summary>Hasta 100 líneas y cada una válida. Un borrador puede estar vacío (enviarlo es lo que exige una línea, R7).</summary>
    public static void ValidQuoteItems<T>(this IRuleBuilder<T, IReadOnlyList<QuoteItemInput>> rule)
    {
        rule.NotNull()
            .Must(items => items is null || items.Count <= Quote.MaxItems)
            .WithMessage($"Un presupuesto no puede tener más de {Quote.MaxItems} líneas.");

        rule.ForEach(item => item.NotNull().SetValidator(new QuoteItemInputValidator()));
    }
}

public sealed class QuoteItemInputValidator : AbstractValidator<QuoteItemInput>
{
    public QuoteItemInputValidator()
    {
        RuleFor(x => x.Description).NotEmpty().MaximumLength(Quote.DescriptionMaxLength);

        RuleFor(x => x.Quantity)
            .Must(q => q > 0 && q <= Quote.QuantityMax && decimal.Round(q, Quote.QuantityDecimals) == q)
            .WithErrorCode(QuoteErrors.QuantityInvalid);

        RuleFor(x => x.UnitPriceCents).InclusiveBetween(0, Quote.UnitPriceCentsMax);

        RuleFor(x => x.VatPercent)
            .Must(VatRates.IsAllowed)
            .WithErrorCode(QuoteErrors.VatInvalid);
    }
}
