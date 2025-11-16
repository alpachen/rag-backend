using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using System.Text;
using System.Text.RegularExpressions;

namespace RagPipeline.Extractors
{
    public class PdfTextExtractor
    {
        /// <summary>
        /// 讀取 PDF 全部頁面，使用 PdfPig 抽取可見文本，
        /// 並執行「中度清洗」：移除多餘空白、合併斷行。
        /// </summary>
        public string ExtractText(string pdfPath)
        {
            if (!File.Exists(pdfPath))
                throw new FileNotFoundException($"PDF 檔案不存在：{pdfPath}");

            var sb = new StringBuilder();

            using (var doc = PdfDocument.Open(pdfPath))
            {
                foreach (var page in doc.GetPages())
                {
                    // 使用 PdfPig 最穩定的抽字器
                    string text = ContentOrderTextExtractor.GetText(page) ?? string.Empty;

                    // 中度清洗
                    text = CleanText(text);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(text);
                        sb.AppendLine(); // 讓 chunker 易於識別段落
                    }
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 中度清洗：合併段落、壓縮空白、移除奇怪符號。
        /// （不做極端清洗，例如自動去頁碼、去表格線 → 讓文件保持完整）
        /// </summary>
        private string CleanText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            string text = input;

            // 移除多餘空白（包含換行 → 空白 + 正常化）
            text = Regex.Replace(text, @"\r\n?|\n", " ");      // 把換行變空白
            text = Regex.Replace(text, @"\s{2,}", " ");        // 連續空白壓成1個

            // 移除頁碼常見格式（例：1/11、10/11、頁數 X/X）
            text = Regex.Replace(text, @"\b\d{1,3}\/\d{1,3}\b", "");

            // 移除前後多餘空白
            return text.Trim();
        }
    }
}
