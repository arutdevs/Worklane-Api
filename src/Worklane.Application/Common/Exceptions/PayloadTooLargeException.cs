using System.Net;

namespace Worklane.Application.Common.Exceptions;

public class PayloadTooLargeException : AppException
{
    public override HttpStatusCode StatusCode => HttpStatusCode.RequestEntityTooLarge;

    public PayloadTooLargeException(long maxBytes)
        : base(
            "file_too_large",
            $"File exceeds the maximum size of {maxBytes} bytes.",
            ErrorParams.Of(("maxBytes", maxBytes)))
    {
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
}
