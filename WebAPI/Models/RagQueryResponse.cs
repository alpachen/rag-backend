namespace WebAPI.Models
{
    public class RagQueryResponse
    {
        public string Answer { get; set; } = "";
        public List<RagSourceDoc> Sources { get; set; } = new List<RagSourceDoc>();
        // ✅ 新增：告訴前端是否已經收集完資訊可以生出表單了
        public bool IsFormReady { get; set; }
        public string FormDataJson { get; set; } = "";
    }

    public class RagSourceDoc
    {
        public string FileName { get; set; }
        public List<string> Pages { get; set; } = new();
        public Dictionary<string, string> PageContents { get; set; } = new();
        public double Score { get; set; }
    }

}