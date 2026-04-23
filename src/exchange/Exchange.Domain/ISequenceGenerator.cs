namespace Bifrost.Exchange.Domain;

public interface ISequenceGenerator
{
    SequenceNumber Next();
}
