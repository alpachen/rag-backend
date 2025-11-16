using System.Text;
using System.Text.RegularExpressions;

namespace RagPipeline.Processing
{
    public class TextChunker
    {
        private readonly int _maxTokens;
        private readonly int _overlap;

        public TextChunker(int maxTokens = 600, int overlap = 100)
        {
            _maxTokens = maxTokens;
            _overlap = overlap;
        }

        public IEnumerable<string> Chunk(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            // 1. 清洗 PDF 垃圾資訊
            text = CleanText(text);

            // 2. 按章節切分
            var sections = SplitBySections(text);

            // 3. 再依 token 長度做次切分
            foreach (var s in sections)
            {
                foreach (var c in TokenChunk(s))
                    yield return c;
            }
        }

        // ------------------------------------------------------------
        // ① 清洗 PDF 雜訊
        // ------------------------------------------------------------
        private string CleanText(string text)
        {
            // 清除「新光醫療…文件編號…版本…頁數…」
            text = Regex.Replace(text, @"新光醫療[\s\S]*?頁數", "", RegexOptions.Multiline);

            // 清除頁碼
            text = Regex.Replace(text, @"^第?\s*\d+\s*頁.*$", "", RegexOptions.Multiline);

            // 清除表格線
            text = Regex.Replace(text, @"[-‐─—]+", " ", RegexOptions.Multiline);

            return text.Trim();
        }

        // ------------------------------------------------------------
        // ② 按章節切：例如 6.6、6.6.1、6.6.2.3
        // ------------------------------------------------------------
        private List<string> SplitBySections(string text)
        {
            var result = new List<string>();

            // 章節標題 regex：例如 "6.6 通報程序"
            var regex = new Regex(@"(?=^\d+(\.\d+){0,3}\s+.+)", RegexOptions.Multiline);

            var matches = regex.Matches(text);
            if (matches.Count == 0)
            {
                result.Add(text);
                return result;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = (i == matches.Count - 1) ? text.Length : matches[i + 1].Index;

                string section = text.Substring(start, end - start).Trim();
                result.Add(section);
            }

            return result;
        }

        // ------------------------------------------------------------
        // ③ Token-based chunking
        // ------------------------------------------------------------
        private IEnumerable<string> TokenChunk(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
                yield break;

            var tokens = section.Length;
            if (tokens <= _maxTokens)
            {
                yield return section;
                yield break;
            }

            int start = 0;

            while (start < section.Length)
            {
                int length = Math.Min(_maxTokens, section.Length - start);
                string chunk = section.Substring(start, length);
                yield return chunk.Trim();

                start += (_maxTokens - _overlap);
                if (start < 0) break;
            }
        }
    }
}
