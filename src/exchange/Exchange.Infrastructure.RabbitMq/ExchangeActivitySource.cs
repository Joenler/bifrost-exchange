using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Bifrost.Exchange.Tests")]

namespace Bifrost.Exchange.Infrastructure.RabbitMq;

internal static class ExchangeActivitySource
{
    internal static readonly ActivitySource Source = new("Bifrost.Exchange", "1.0.0");
}
