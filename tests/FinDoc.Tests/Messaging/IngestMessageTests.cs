using FinDoc.Core.Messaging;

namespace FinDoc.Tests.Messaging;

public class IngestMessageTests
{
    [Fact]
    public void Round_trips_through_bytes()
    {
        var original = new IngestMessage(Guid.NewGuid());

        var restored = IngestMessage.FromBytes(original.ToBytes());

        Assert.Equal(original, restored);
    }
}
