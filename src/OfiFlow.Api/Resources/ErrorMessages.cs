namespace OfiFlow.Api.Resources;

/// <summary>
/// Ancla del diccionario de mensajes (ADR-011). No tiene código: IStringLocalizer&lt;ErrorMessages&gt;
/// usa su nombre completo (OfiFlow.Api.Resources.ErrorMessages) para encontrar el recurso
/// incrustado ErrorMessages.resx de esta misma carpeta.
/// ErrorMessages.resx es el idioma por defecto (español); otro idioma será un fichero hermano
/// con su sufijo (ErrorMessages.en.resx) y una entrada en la lista de idiomas de Program.cs.
/// </summary>
public sealed class ErrorMessages;
