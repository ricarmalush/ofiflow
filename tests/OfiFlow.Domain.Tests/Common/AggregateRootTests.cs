using OfiFlow.Domain.Common;

namespace OfiFlow.Domain.Tests.Common;

public class AggregateRootTests
{
    private sealed record SomethingHappened : IDomainEvent;

    private sealed class TestAggregate : AggregateRoot
    {
        public TestAggregate(Guid id)
            : base(id)
        {
        }

        public void RaiseSomethingHappened() => AddDomainEvent(new SomethingHappened());
    }

    [Fact]
    public void TwoEntitiesWithTheSameIdAndType_AreEqual()
    {
        var id = Guid.NewGuid();
        var first = new TestAggregate(id);
        var second = new TestAggregate(id);

        Assert.Equal(first, second);
    }

    [Fact]
    public void TwoEntitiesWithDifferentIds_AreNotEqual()
    {
        var first = new TestAggregate(Guid.NewGuid());
        var second = new TestAggregate(Guid.NewGuid());

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AddDomainEvent_AddsItToDomainEvents()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());

        aggregate.RaiseSomethingHappened();

        Assert.Single(aggregate.DomainEvents);
    }

    [Fact]
    public void ClearDomainEvents_RemovesAllPendingEvents()
    {
        var aggregate = new TestAggregate(Guid.NewGuid());
        aggregate.RaiseSomethingHappened();

        aggregate.ClearDomainEvents();

        Assert.Empty(aggregate.DomainEvents);
    }
}
