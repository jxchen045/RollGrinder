using System;
using FluentAssertions;
using Xunit;

namespace RollGrinder.Core.Tests;

public sealed class DomainExceptionTests
{
    [Fact]
    public void Carries_message_and_inner_exception()
    {
        var inner = new InvalidOperationException("inner");
        var exception = new DomainException("roll profile is not monotonic", inner);

        exception.Message.Should().Be("roll profile is not monotonic");
        exception.InnerException.Should().BeSameAs(inner);
    }
}
