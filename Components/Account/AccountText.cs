using Microsoft.AspNetCore.Identity;

namespace MiBitacora.Components.Account;

internal static class AccountText
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;
    public const string PasswordLengthMessage = "La contraseña debe tener entre 12 y 128 caracteres.";

    public static string DescribeErrors(IdentityResult result) => string.Join(" ", result.Errors.Select(DescribeError).Distinct());

    private static string DescribeError(IdentityError error) => error.Code switch
    {
        "DuplicateEmail" or "DuplicateUserName" => "Ya hay una cuenta con ese correo.",
        "InvalidEmail" or "InvalidUserName" => "Revisá que el correo electrónico esté escrito correctamente.",
        "PasswordMismatch" => "La contraseña actual es incorrecta.",
        "PasswordTooShort" => PasswordLengthMessage,
        "PasswordRequiresNonAlphanumeric" => "Agregá un símbolo a la contraseña.",
        "PasswordRequiresDigit" => "Agregá un número a la contraseña.",
        "PasswordRequiresLower" => "Agregá una letra minúscula a la contraseña.",
        "PasswordRequiresUpper" => "Agregá una letra mayúscula a la contraseña.",
        "PasswordRequiresUniqueChars" => "Usá más caracteres distintos en la contraseña.",
        "ConcurrencyFailure" => "La cuenta se actualizó en otra pestaña. Recargá esta página y volvé a intentar.",
        _ => "No pudimos guardar el cambio. Volvé a intentar."
    };
}
