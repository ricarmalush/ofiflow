using System.Text;

namespace OfiFlow.Admin;

/// <summary>
/// Lo poco que este programa hace con la terminal. La contraseña se lee <b>aquí, sin eco y nunca por argumento</b>: un
/// argumento queda en el historial de la shell y en la lista de procesos del servidor, y lo ve cualquiera con acceso a ellos.
/// </summary>
internal static class Prompt
{
    public static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public static string? ReadLine(string question)
    {
        Console.Write(question);

        return Console.ReadLine()?.Trim();
    }

    /// <summary>Lee una contraseña sin mostrar lo que se teclea. Devuelve <c>null</c> si se cancela con Escape.</summary>
    public static string? ReadSecret(string question)
    {
        Console.Write(question);

        var typed = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }
}
