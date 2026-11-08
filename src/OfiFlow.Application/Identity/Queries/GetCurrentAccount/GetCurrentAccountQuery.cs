using MediatR;
using OfiFlow.Application.Common.Authorization;
using OfiFlow.Domain.Tenancy;

namespace OfiFlow.Application.Identity.Queries.GetCurrentAccount;

/// <summary>
/// Los datos de la <b>propia</b> cuenta, su empresa y su rol efectivo (spec 012). No tiene parámetros: la cuenta sale del token
/// validado, nunca de la petición, así que nadie puede preguntar por otra persona por aquí.
/// </summary>
[RequiresPermission(Permission.AccountRead)]
// Spec 022 R3: una de las CUATRO operaciones que puede hacer quien debe activar el segundo factor y aún no lo tiene (quién soy: la interfaz necesita saberlo precisamente para llevar a la persona a activar el factor).
[AllowedWithoutSecondFactor]
public sealed record GetCurrentAccountQuery : IRequest<CurrentAccountDto>;
