using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Supervertaler.Trados.Models
{
    public enum ChatRole
    {
        User,
        Assistant,
        System
    }

    [DataContract]
    public class ChatMessage
    {
        [DataMember(Name = "role")]
        public ChatRole Role { get; set; }

        [DataMember(Name = "content")]
        public string Content { get; set; }

        /// <summary>
        /// Optional display-only override. When set, the chat bubble shows this text instead of
        /// <see cref="Content"/>. <see cref="Content"/> is always sent to the AI unchanged.
        /// Used to show a short summary (e.g. "[source document — 47 segments]") in place of a
        /// large {{PROJECT}} expansion so the chat history stays readable.
        /// </summary>
        [DataMember(Name = "displayContent", EmitDefaultValue = false)]
        public string DisplayContent { get; set; }

        /// <summary>
        /// Optional image attachments. Null means text-only (most messages).
        /// </summary>
        [DataMember(Name = "images", EmitDefaultValue = false)]
        public List<ImageAttachment> Images { get; set; }

        /// <summary>
        /// Optional document attachments. Null means no documents.
        /// </summary>
        [DataMember(Name = "documents", EmitDefaultValue = false)]
        public List<DocumentAttachment> Documents { get; set; }

        [DataMember(Name = "timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>True if this message has one or more image attachments.</summary>
        public bool HasImages => Images != null && Images.Count > 0;

        /// <summary>True if this message has one or more document attachments.</summary>
        public bool HasDocuments => Documents != null && Documents.Count > 0;
    }

    /// <summary>
    /// An image attached to a chat message for vision-enabled AI models.
    /// </summary>
    [DataContract]
    public class ImageAttachment
    {
        /// <summary>Raw image bytes (PNG or JPEG).</summary>
        [DataMember(Name = "data")]
        public byte[] Data { get; set; }

        /// <summary>MIME type, e.g. "image/png", "image/jpeg".</summary>
        [DataMember(Name = "mimeType")]
        public string MimeType { get; set; }

        /// <summary>Display name for the thumbnail strip.</summary>
        [DataMember(Name = "fileName")]
        public string FileName { get; set; }

        /// <summary>Original image width in pixels (for layout).</summary>
        [DataMember(Name = "width")]
        public int Width { get; set; }

        /// <summary>Original image height in pixels (for layout).</summary>
        [DataMember(Name = "height")]
        public int Height { get; set; }
    }

    /// <summary>
    /// A document attached to a chat message. Text is extracted and sent as context.
    /// </summary>
    [DataContract]
    public class DocumentAttachment
    {
        /// <summary>Original file name for display.</summary>
        [DataMember(Name = "fileName")]
        public string FileName { get; set; }

        /// <summary>Extracted text content from the document.</summary>
        [DataMember(Name = "extractedText")]
        public string ExtractedText { get; set; }

        /// <summary>File size in bytes (original file, for display).</summary>
        [DataMember(Name = "fileSize")]
        public long FileSize { get; set; }
    }

    /// <summary>
    /// Event args for chat send — carries text, optional images, and optional documents.
    /// </summary>
    public class ChatSendEventArgs : EventArgs
    {
        public string Text { get; set; }
        public List<ImageAttachment> Images { get; set; }
        public List<DocumentAttachment> Documents { get; set; }

        /// <summary>
        /// Optional display-only text for the user bubble. When set, the bubble shows this
        /// instead of <see cref="Text"/>. <see cref="Text"/> is always sent to the AI.
        /// </summary>
        public string DisplayText { get; set; }

        /// <summary>
        /// Optional max output token override for this request. Used by prompt generation
        /// which needs more output tokens than regular chat (prompts can be 5000+ words).
        /// When null, the handler uses its default.
        /// </summary>
        public int? MaxTokens { get; set; }

        /// <summary>
        /// When true, the user message bubble is displayed with assistant styling (gray,
        /// left-aligned) instead of user styling (blue, right-aligned). Used for system-
        /// initiated messages like "Analyse Project &amp; Generate Prompt" where the user
        /// clicked a button but didn't type the message themselves.
        /// The message is still sent to the AI as a user message.
        /// </summary>
        public bool ShowAsStatus { get; set; }
    }
}
