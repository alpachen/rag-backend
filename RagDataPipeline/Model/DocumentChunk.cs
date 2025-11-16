namespace RagPipeline.Models
{
    public class DocumentChunk
    {
        public string ChunkId { get; set; } = "";
        public string SourceId { get; set; } = "";
        public string Content { get; set; } = "";
        public int Order { get; set; }
    }
}

