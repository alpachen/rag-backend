namespace WebAPI.Models
{
    public class RagQueryResponse
    {
        public string Answer { get; set; } = "";
        public List<RagSourceDoc> Sources { get; set; } = new List<RagSourceDoc>();
    }

    public class RagSourceDoc
    {
        public string FileName { get; set; }
        public List<string> Pages { get; set; } = new();
        public Dictionary<string, string> PageContents { get; set; } = new();
        public double Score { get; set; }
    }

}