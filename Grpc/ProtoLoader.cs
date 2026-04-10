using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Text;
using System.Text.Json;
using GrpcWorkbench.Models.Grpc;

namespace GrpcWorkbench.Grpc;

public interface IProtoLoader
{
    Task<List<ServiceMetadata>> LoadProtoServicesAsync(byte[] protoContent);
    DescriptorProto? GetMessageDescriptor(FileDescriptorProto fileDescriptor, string messageName);
}

public class ProtoLoader : IProtoLoader
{
    private readonly ILogger<ProtoLoader> _logger;

    public ProtoLoader(ILogger<ProtoLoader> logger)
    {
        _logger = logger;
    }

    public async Task<List<ServiceMetadata>> LoadProtoServicesAsync(byte[] protoContent)
    {
        try
        {
            var services = new List<ServiceMetadata>();
            
            // Try to parse as text proto file first
            FileDescriptorProto fileDescriptor;
            try
            {
                var protoText = Encoding.UTF8.GetString(protoContent);
                fileDescriptor = ParseProtoText(protoText);
            }
            catch
            {
                // Fallback to binary format if text parsing fails
                fileDescriptor = FileDescriptorProto.Parser.ParseFrom(protoContent);
            }

            var messagesByName = fileDescriptor.MessageType
                .ToDictionary(m => m.Name, m => m);

            foreach (var service in fileDescriptor.Service)
            {
                var serviceMetadata = new ServiceMetadata
                {
                    ServiceName = service.Name,
                    Description = null
                };

                foreach (var method in service.Method)
                {
                    var inputType = method.InputType.TrimStart('.');
                    var outputType = method.OutputType.TrimStart('.');

                    var methodMetadata = new MethodMetadata
                    {
                        MethodName = method.Name,
                        InputType = inputType,
                        OutputType = outputType,
                        RpcType = DetermineRpcType(method).ToString(),
                        InputSchema = GenerateJsonSchema(messagesByName, inputType),
                        OutputSchema = GenerateJsonSchema(messagesByName, outputType)
                    };

                    serviceMetadata.Methods.Add(methodMetadata);
                }

                services.Add(serviceMetadata);
            }

            _logger.LogInformation("Loaded {ServiceCount} services from proto", services.Count);
            return await Task.FromResult(services);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load proto services");
            throw;
        }
    }

    private FileDescriptorProto ParseProtoText(string protoText)
    {
        var fileDescriptor = new FileDescriptorProto();

        // Remove comments and normalize whitespace
        var lines = protoText.Split('\n');
        var cleanedText = new StringBuilder();

        foreach (var line in lines)
        {
            var commentIndex = line.IndexOf("//");
            var cleanedLine = commentIndex >= 0 ? line.Substring(0, commentIndex) : line;
            cleanedText.Append(cleanedLine).Append("\n");
        }

        var text = cleanedText.ToString();

        // Parse services
        var servicePattern = @"service\s+(\w+)\s*\{([^}]*)\}";
        var serviceMatches = System.Text.RegularExpressions.Regex.Matches(text, servicePattern, System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match serviceMatch in serviceMatches)
        {
            var serviceName = serviceMatch.Groups[1].Value;
            var serviceBody = serviceMatch.Groups[2].Value;

            var service = new ServiceDescriptorProto { Name = serviceName };

            // Parse RPC methods within service
            var rpcPattern = @"rpc\s+(\w+)\s*\(\s*(stream\s+)?(\w+)\s*\)\s*returns\s*\(\s*(stream\s+)?(\w+)\s*\)";
            var rpcMatches = System.Text.RegularExpressions.Regex.Matches(serviceBody, rpcPattern);

            foreach (System.Text.RegularExpressions.Match rpcMatch in rpcMatches)
            {
                var methodName = rpcMatch.Groups[1].Value;
                var inputStreamKeyword = rpcMatch.Groups[2].Value;
                var inputType = rpcMatch.Groups[3].Value;
                var outputStreamKeyword = rpcMatch.Groups[4].Value;
                var outputType = rpcMatch.Groups[5].Value;

                var method = new MethodDescriptorProto
                {
                    Name = methodName,
                    InputType = "." + inputType,
                    OutputType = "." + outputType,
                    ClientStreaming = !string.IsNullOrEmpty(inputStreamKeyword),
                    ServerStreaming = !string.IsNullOrEmpty(outputStreamKeyword)
                };

                service.Method.Add(method);
            }

            if (service.Method.Count > 0)
                fileDescriptor.Service.Add(service);
        }

        // Parse messages
        var messagePattern = @"message\s+(\w+)\s*\{([^}]*)\}";
        var messageMatches = System.Text.RegularExpressions.Regex.Matches(text, messagePattern, System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match messageMatch in messageMatches)
        {
            var messageName = messageMatch.Groups[1].Value;
            var messageBody = messageMatch.Groups[2].Value;

            var message = new DescriptorProto { Name = messageName };

            // Parse fields
            var fieldPattern = @"(\w+)\s+(\w+)\s*=\s*(\d+)";
            var fieldMatches = System.Text.RegularExpressions.Regex.Matches(messageBody, fieldPattern);

            foreach (System.Text.RegularExpressions.Match fieldMatch in fieldMatches)
            {
                var fieldType = fieldMatch.Groups[1].Value;
                var fieldName = fieldMatch.Groups[2].Value;
                var fieldNumber = int.Parse(fieldMatch.Groups[3].Value);

                var field = new FieldDescriptorProto
                {
                    Name = fieldName,
                    Number = fieldNumber,
                    Type = ConvertProtoTypeToDescriptorType(fieldType)
                };

                message.Field.Add(field);
            }

            if (message.Field.Count > 0)
                fileDescriptor.MessageType.Add(message);
        }

        return fileDescriptor;
    }

    private FieldDescriptorProto.Types.Type ConvertProtoTypeToDescriptorType(string protoType)
    {
        return protoType switch
        {
            "string" => FieldDescriptorProto.Types.Type.String,
            "int32" => FieldDescriptorProto.Types.Type.Int32,
            "int64" => FieldDescriptorProto.Types.Type.Int64,
            "uint32" => FieldDescriptorProto.Types.Type.Uint32,
            "uint64" => FieldDescriptorProto.Types.Type.Uint64,
            "float" => FieldDescriptorProto.Types.Type.Float,
            "double" => FieldDescriptorProto.Types.Type.Double,
            "bool" => FieldDescriptorProto.Types.Type.Bool,
            "bytes" => FieldDescriptorProto.Types.Type.Bytes,
                     _ => FieldDescriptorProto.Types.Type.String
                };
            }

            public DescriptorProto? GetMessageDescriptor(FileDescriptorProto fileDescriptor, string messageName)
    {
        return fileDescriptor.MessageType.FirstOrDefault(m => m.Name == messageName);
    }

    private RpcTypeEnum DetermineRpcType(MethodDescriptorProto method)
    {
        var clientStreaming = method.ClientStreaming;
        var serverStreaming = method.ServerStreaming;

        return (clientStreaming, serverStreaming) switch
        {
            (false, false) => RpcTypeEnum.Unary,
            (true, false) => RpcTypeEnum.ClientStreaming,
            (false, true) => RpcTypeEnum.ServerStreaming,
            (true, true) => RpcTypeEnum.BidirectionalStreaming,
        };
    }

    private string? GenerateJsonSchema(Dictionary<string, DescriptorProto> messages, string messageName)
    {
        if (!messages.TryGetValue(messageName, out var message))
            return null;

        var schema = new
        {
            type = "object",
            properties = message.Field.ToDictionary(
                f => f.Name,
                f => new
                {
                    type = GetJsonType(f.Type),
                    description = ""
                }
            )
        };

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
    }

    private string GetJsonType(FieldDescriptorProto.Types.Type type) => type switch
    {
        FieldDescriptorProto.Types.Type.String => "string",
        FieldDescriptorProto.Types.Type.Int32 or
        FieldDescriptorProto.Types.Type.Int64 or
        FieldDescriptorProto.Types.Type.Uint32 or
        FieldDescriptorProto.Types.Type.Uint64 => "integer",
        FieldDescriptorProto.Types.Type.Double or
        FieldDescriptorProto.Types.Type.Float => "number",
        FieldDescriptorProto.Types.Type.Bool => "boolean",
        FieldDescriptorProto.Types.Type.Bytes => "string",
        _ => "object"
    };
}
