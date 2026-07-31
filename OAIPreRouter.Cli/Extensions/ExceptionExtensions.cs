namespace OAIPreRouter.Cli.Extensions;

public static class ExceptionExtensions
{
    public static bool IsOperationCanceled(this Exception ex) =>
        ex is OperationCanceledException or TimeoutException;
}
