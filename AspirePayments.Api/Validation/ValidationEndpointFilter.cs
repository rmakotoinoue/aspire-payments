using System.ComponentModel.DataAnnotations;
using AspirePayments.Api.Contracts;

namespace AspirePayments.Api.Validation;

/// <summary>
/// Valida os contratos de entrada (DataAnnotations) antes de o handler ser
/// executado e devolve 400 no mesmo formato <c>ProblemDetails</c> usado pelas
/// demais respostas de erro da API.
///
/// O filtro só inspeciona tipos do namespace de contratos da API; parâmetros
/// de framework (<see cref="HttpContext"/>, <see cref="CancellationToken"/>,
/// <see cref="Guid"/>) são ignorados.
/// </summary>
public sealed class ValidationEndpointFilter : IEndpointFilter
{
    private static readonly string ContractsNamespace = typeof(CreateOrderRequest).Namespace!;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var argument in context.Arguments)
        {
            if (argument is null || argument.GetType().Namespace != ContractsNamespace)
            {
                continue;
            }

            var results = new List<ValidationResult>();

            if (Validator.TryValidateObject(
                    argument,
                    new ValidationContext(argument),
                    results,
                    validateAllProperties: true))
            {
                continue;
            }

            foreach (var result in results)
            {
                var member = result.MemberNames.FirstOrDefault() ?? argument.GetType().Name;
                var message = result.ErrorMessage ?? "The value is invalid.";

                errors[member] = errors.TryGetValue(member, out var existing)
                    ? [.. existing, message]
                    : [message];
            }
        }

        return errors.Count == 0
            ? await next(context)
            : TypedResults.ValidationProblem(errors);
    }
}
