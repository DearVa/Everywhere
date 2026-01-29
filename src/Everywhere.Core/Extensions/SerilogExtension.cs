using Everywhere.Common;
using Serilog;

namespace Everywhere.Extensions;

public static class SerilogExtension
{
    public static AnonymousExceptionHandler ToExceptionHandler(this ILogger logger)
    {
        return new AnonymousExceptionHandler((exception, message, source, lineNumber) =>
        {
            logger.Error(exception, "[{MemberName}: {LineNumber}] {Message}", source, lineNumber, message);
        });
    }
}