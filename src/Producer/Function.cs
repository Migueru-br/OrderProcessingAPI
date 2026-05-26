using System.Net;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Producer;


public class Function
{
    private readonly IAmazonSQS _sqsClient;
    private readonly string _queueUrl;

    public Function()
    {
        _sqsClient = new AmazonSQSClient();
        _queueUrl = Environment.GetEnvironmentVariable("ORDERS_QUEUE_URL") ?? string.Empty;

    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {   
            if (string.IsNullOrWhiteSpace(request.Body)) 
            {
                context.Logger.LogWarning("Request Body is invalid");
                 return new APIGatewayProxyResponse
                 {
                     StatusCode = (int)HttpStatusCode.BadRequest,
                     Body = JsonSerializer.Serialize(new {Error = "Request Body is Invalid"}),
                     Headers = new Dictionary<string,string> {{"Content-Type", "application/json"}}
                 };
                 }
            var order = JsonSerializer.Deserialize<Order>(request.Body);
            if (order == null)
            {
                context.Logger.LogWarning($"Invalid JSON.");
                return new APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = JsonSerializer.Serialize(new { Error = "JSON is Invalid" }),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }
            if (string.IsNullOrWhiteSpace(order.Product) || order.Quantity <= 0)
            {
                context.Logger.LogWarning($"Invalid request. requestId={request.RequestContext.RequestId}, product={order.Product}");
                return new
             APIGatewayProxyResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Body = JsonSerializer.Serialize(new { Error = "Product and Quantity are required" }),
                    Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
                };
            }

            order.RequestId = request.RequestContext.RequestId;
            order.OrderId = Guid.NewGuid().ToString();
            order.Status = "PENDING";
            order.CreatedAt = DateTime.UtcNow.ToString("o");

            var messageBody = JsonSerializer.Serialize(order);
            await _sqsClient.SendMessageAsync(_queueUrl, messageBody);
            context.Logger.LogInformation($"Order sent to SQS - OrderId: {order.OrderId}, RequestId: {order.RequestId}");

            return new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.Accepted,
                Body = JsonSerializer.Serialize(new { order.OrderId, Message = "Order Received" }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }
        catch (JsonException error)
        {
            context.Logger.LogWarning("Malformed JSON in request body");
            return new APIGatewayProxyResponse
            {
                StatusCode = (int)HttpStatusCode.BadRequest,
                Body = JsonSerializer.Serialize(new { Error = $"Malformed JSON in request body. Error = {error.Message} " }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }
    }
}
