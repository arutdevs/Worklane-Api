namespace Worklane.Application.Common.Exceptions;

public class PayloadTooLargeException : Exception
{
    public PayloadTooLargeException(long maxBytes)
        : base($"File exceeds the maximum size of {maxBytes} bytes.")
    {
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
}
