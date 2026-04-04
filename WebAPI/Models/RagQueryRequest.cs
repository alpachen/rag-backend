namespace WebAPI.Models
{
    public class RagQueryRequest
    {
        public string Query { get; set; } = "";
        public string? Mode { get; set; }
    }
}
