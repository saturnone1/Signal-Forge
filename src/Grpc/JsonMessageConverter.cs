using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Text.Json;

namespace GrpcWorkbench.Grpc;

public interface IJsonMessageConverter
{
    Task<IMessage> JsonToMessageAsync(string json, Type messageType);
    string MessageToJson(IMessage message);
}

public class JsonMessageConverter : IJsonMessageConverter
{
    private readonly ILogger<JsonMessageConverter> _logger;

    public JsonMessageConverter(ILogger<JsonMessageConverter> logger)
    {
        _logger = logger;
    }

    public Task<IMessage> JsonToMessageAsync(string json, Type messageType)
    {
        try
        {
            if (!typeof(IMessage).IsAssignableFrom(messageType))
                throw new ArgumentException($"Type {messageType.Name} does not implement IMessage");

            var message = Activator.CreateInstance(messageType) as IMessage
                ?? throw new InvalidOperationException($"Cannot create instance of {messageType.Name}");

            var parser = messageType.GetProperty("Parser")?.GetValue(null);
            if (parser == null)
                throw new InvalidOperationException($"No Parser property found on {messageType.Name}");

            var parseMethod = parser.GetType().GetMethod("ParseJson", [typeof(string)]);
            if (parseMethod == null)
                throw new InvalidOperationException($"No ParseJson method found on parser");

            var result = parseMethod.Invoke(parser, [json]) as IMessage
                ?? throw new InvalidOperationException("Failed to parse JSON");

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert JSON to message");
            throw;
        }
    }

    public string MessageToJson(IMessage message)
    {
        try
        {
            var formatter = new JsonFormatter(new JsonFormatter.Settings(true));
            return formatter.Format(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert message to JSON");
            throw;
        }
    }
}
