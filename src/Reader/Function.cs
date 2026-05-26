using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.DynamoDBv2.Model;
using Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Reader;


public class Function
{
    private readonly IAmazonDynamoDB _dynamoDbClient;
    private readonly string _dynamoDbTableName;

    public Function()
    {
        _dynamoDbClient = new AmazonDynamoDBClient();
        _dynamoDbTableName = Environment.GetEnvironmentVariable("ORDERS_TABLE") ?? string.Empty;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {

            if (request.PathParameters != null && request.PathParameters.TryGetValue("orderId", out var orderId))
            {
                var dynamoDbRequest = new GetItemRequest
                {
                    TableName = _dynamoDbTableName,
                    Key = new Dictionary<string, AttributeValue> { { "orderId", new AttributeValue { S = $"{orderId}" } } }
                };
                var res = await _dynamoDbClient.GetItemAsync(dynamoDbRequest);
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.OK,
                    Body = JsonSerializer.Serialize(MapToOrder(res.Item)),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }

                };
            }

            var dynamoDbScanRequest = new ScanRequest
            {
                TableName = _dynamoDbTableName
            };
            var dynamoDbScanResponse = await _dynamoDbClient.ScanAsync(dynamoDbScanRequest);
            var orders = dynamoDbScanResponse.Items.Select(MapToOrder).ToList();
            return new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.OK,
                Body = JsonSerializer.Serialize(orders),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }
        catch (Exception error)
        {
            context.Logger.LogWarning($"An error ocurred. Error: {error.Message}");
            return new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.BadRequest,
                Body = JsonSerializer.Serialize(new { Error = $"Malformed JSON in request body. Error = {error.Message} " }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }
    }

    private static Order? MapToOrder(Dictionary<string, AttributeValue> item)
    {
        if (item == null || item.Count == 0) return null;

        return new Order
        {
            OrderId = item.TryGetValue("orderId", out var orderId) ? orderId.S : string.Empty,
            RequestId = item.TryGetValue("requestId", out var requestId) ? requestId.S : string.Empty,
            Product = item.TryGetValue("product", out var product) ? product.S : string.Empty,
            Quantity = item.TryGetValue("quantity", out var quantity) && int.TryParse(quantity.N, out var q) ? q : 0,
            Status = item.TryGetValue("status", out var status) ? status.S : string.Empty,
            CreatedAt = item.TryGetValue("createdAt", out var createdAt) ? createdAt.S : string.Empty
        };
    }
}
