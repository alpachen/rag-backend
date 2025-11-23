namespace WebAPI.Models
{
    public class RagQueryResponse
    {
        public string Answer { get; set; } = "";
        public List<RagSourceDoc> Sources { get; set; } = new List<RagSourceDoc>();
    }

    public class RagSourceDoc
    {
        public string FileName { get; set; } = "";
        public string Page { get; set; } = "";
        public string Content { get; set; } = "";
        public double Score { get; set; }
    }
}