using System.Collections.Generic;

namespace Supervertaler.Core.Models
{
    /// <summary>
    /// Represents a folder in the prompt library tree structure.
    /// </summary>
    public class PromptFolderNode
    {
        /// <summary>Display name (folder name on disk).</summary>
        public string Name { get; set; }

        /// <summary>Path relative to the prompt library root.</summary>
        public string RelativePath { get; set; }

        /// <summary>Child folders.</summary>
        public List<PromptFolderNode> Children { get; set; } = new List<PromptFolderNode>();

        /// <summary>Prompts directly in this folder.</summary>
        public List<PromptTemplate> Prompts { get; set; } = new List<PromptTemplate>();
    }
}
