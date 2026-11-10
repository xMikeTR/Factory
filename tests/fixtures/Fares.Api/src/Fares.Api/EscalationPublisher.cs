using Azure.Messaging.ServiceBus;

public sealed class EscalationPublisher(ServiceBusClient client, IConfiguration config)
{
    private readonly ServiceBusSender _sender = client.CreateSender(config["ServiceBus:EscalationQueue"]);

    public Task SendAsync(Booking booking) =>
        _sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(booking)));
}
