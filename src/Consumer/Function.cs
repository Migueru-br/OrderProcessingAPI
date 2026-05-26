using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.DynamoDBv2;
using Amazon.Lambda.SQSEvents;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]


namespace Consumer;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDbClient;
    private readonly string _dynamoDbTableName;
    private readonly IAmazonSQS _sqsClient;
    private readonly string _queueDlqUrl;

    
    public Function()
    {
        _dynamoDbClient = new AmazonDynamoDBClient();
        _dynamoDbTableName = Environment.GetEnvironmentVariable("ORDERS_TABLE") ?? string.Empty;
        _sqsClient = new AmazonSQSClient();
        _queueDlqUrl = Environment.GetEnvironmentVariable("ORDERS_QUEUE_DLQ_URL") ?? string.Empty;
    }

    public async Task FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {

        foreach (var record in sqsEvent.Records)
        {   
            try {
            var orderRequest = JsonSerializer.Deserialize<Order>(record.Body);
            if (orderRequest == null) {context.Logger.LogWarning($"Order Json is null. MessageId = {record.MessageId}");continue;}
            if (string.IsNullOrWhiteSpace(orderRequest.OrderId) || string.IsNullOrWhiteSpace(orderRequest.Product) || orderRequest.Quantity <= 0 || string.IsNullOrWhiteSpace(orderRequest.RequestId)) 
            {
                context.Logger.LogWarning($"Invalid order. MessageId={record.MessageId}, orderId={orderRequest.OrderId}");
                await _sqsClient.SendMessageAsync(_queueDlqUrl, record.Body);
                continue;
            }
            orderRequest.Status = "PROCESSED";
            var request = new PutItemRequest
            {
                TableName = _dynamoDbTableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    {"requestId", new AttributeValue {S = orderRequest.RequestId }},
                    {"orderId", new AttributeValue {S = orderRequest.OrderId }},
                    {"product", new AttributeValue {S = orderRequest.Product}},
                    {"quantity", new AttributeValue {N = orderRequest.Quantity.ToString()}},
                    {"status", new AttributeValue {S = orderRequest.Status}},
                    {"createdAt", new AttributeValue {S = orderRequest.CreatedAt}},
                }
            };
            await _dynamoDbClient.PutItemAsync(request);
            }
            catch (JsonException ex)
            {
                context.Logger.LogWarning($"Poison Message. Message Id={record.MessageId}, Error={ex.Message}");
                await _sqsClient.SendMessageAsync(_queueDlqUrl, record.Body);
                continue;
            }
        }
    }
}