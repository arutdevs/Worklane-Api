namespace Worklane.Domain.Common;

public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }
}
