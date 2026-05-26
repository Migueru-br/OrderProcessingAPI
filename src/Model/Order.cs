namespace Model;

public class Order
{
    public string RequestId {get;set;} = string.Empty;
    public string OrderId {get; set;} = string.Empty;
    public string Product { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string Status { get; set; } = string.Empty;
    public string CreatedAt { get; set; } =  string.Empty;
}