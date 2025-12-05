namespace WebAPI.Models
{
    public class RagSourceDoc
    {
        public string FileName { get; set; }

        // 多頁頁碼，例如 ["12","13","14"]
        public List<string> Pages { get; set; }

        // 每頁對應的內容，例如 { "12": "...", "13": "..." }
        public Dictionary<string, string> PageContents { get; set; }

        public double Score { get; set; }
    }
}
