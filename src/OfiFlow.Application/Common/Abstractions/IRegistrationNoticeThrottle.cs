namespace OfiFlow.Application.Common.Abstractions;

/// <summary>
/// El límite de <b>avisos «ya tienes una cuenta»</b> por email (spec 021 R7). El límite de 3 por hora y por email de los registros pendientes no cubre este caso: si el email ya tiene cuenta no se
/// crea ningún pendiente (R6), así que no hay nada que contar en la base de datos, y sin otro freno quien conociera el email de una víctima podría llenarle el buzón de avisos desde muchas IP
/// (el límite por IP no lo impide: un atacante tiene muchas). Este límite es el equivalente, <b>en memoria y por instancia</b> como el de las invitaciones (spec 017 D10): una sola instancia de la API
/// (ADR-009), valor inicial que se revisa con datos reales.
/// </summary>
public interface IRegistrationNoticeThrottle
{
    /// <summary>
    /// Gasta un cupo del email (en cualquier mayúscula y con espacios alrededor: es el mismo email). <c>false</c> si ya agotó los de la hora: entonces no se envía el aviso. La implementación no
    /// guarda el email tal cual, solo una huella.
    /// </summary>
    bool TryAcquire(string email);
}
