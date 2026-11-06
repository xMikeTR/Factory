using Azure.Messaging.ServiceBus;

public sealed class ReferralPublisher(ServiceBusClient client, IConfiguration config)
{
    private readonly ServiceBusSender _sender = client.CreateSender(config["ServiceBus:ReferralQueue"]);

    public Task SendAsync(Quote quote) =>
        _sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(quote)));
}
