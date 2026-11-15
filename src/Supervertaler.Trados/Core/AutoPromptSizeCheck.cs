using System;
using Supervertaler.Core;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// AutoPrompt reads the whole document, so a very long one makes a request
    /// that is expensive, or that the model refuses. This decides whether to ask
    /// first, and what to say: nothing below <see cref="ThresholdTokens"/>, one
    /// confirm box at or above it. No settings and no state; the handler builds
    /// the request, calls <see cref="For"/> and shows what it returns.
    ///
    /// There is no sentence about whether the request fits the model: nothing in
    /// the model list or the price list gives a model's input limit.
    /// </summary>
    public static class AutoPromptSizeCheck
    {
        /// <summary>Estimated input tokens from which AutoPrompt asks before it sends.</summary>
        public const int ThresholdTokens = 100000;

        /// <summary>
        /// The reply counted in the cost. The request allows up to 32,768 output
        /// tokens; a finished prompt is far shorter, and the message says that
        /// this is an estimate.
        /// </summary>
        public const int AssumedReplyTokens = 8000;

        public const string Title = "Large document – AutoPrompt";

        /// <summary>Put before the provider's own error when it refused the request for its size.</summary>
        public const string SizeRefusalLead =
            "The document is too large for this model: the request was refused for its size. " +
            "Choose a model that can take in more, then run AutoPrompt again.";

        /// <summary>True when AutoPrompt asks before sending a request of this many estimated input tokens.</summary>
        public static bool Asks(int inputTokens)
        {
            return inputTokens >= ThresholdTokens;
        }

        /// <summary>
        /// The text of the confirm box for this request, or null when there is
        /// nothing to ask. The request is the meta-prompt alone: AutoPrompt sends
        /// no system prompt, no chat history and no tools with it. The cost is
        /// worked out as everywhere else: <see cref="TokenEstimator.EstimateCost"/>
        /// for a model in the price list, and no figure for one that is not.
        /// </summary>
        public static string For(int segments, int words, string metaPrompt, string provider, string model)
        {
            int inputTokens = TokenEstimator.EstimateTokens(metaPrompt);
            if (!Asks(inputTokens)) return null;

            var info = LlmModels.FindModel(model);
            bool isLocal = string.Equals(provider, LlmModels.ProviderOllama, StringComparison.OrdinalIgnoreCase);
            decimal? cost = TokenEstimator.HasPricing(model)
                ? TokenEstimator.EstimateCost(model, inputTokens, AssumedReplyTokens)
                : (decimal?)null;
            return Message(segments, words, inputTokens, info != null ? info.DisplayName : model, isLocal, cost);
        }

        /// <summary>
        /// The same, from the numbers. <paramref name="cost"/> is null when the
        /// model's price is not known; <paramref name="isLocal"/> is a model on
        /// the translator's own computer, which costs nothing whatever the list says.
        /// </summary>
        public static string Message(int segments, int words, int inputTokens, string modelName, bool isLocal, decimal? cost)
        {
            if (!Asks(inputTokens)) return null;

            var name = string.IsNullOrWhiteSpace(modelName) ? "this model" : modelName.Trim();
            // To the nearest thousand: the count is characters divided by four,
            // and a figure exact to the token would claim more than is known.
            long shownTokens = ((long)inputTokens + 500) / 1000 * 1000;

            string costLine;
            if (isLocal)
                costLine = $"{name} runs on your own computer, so this costs nothing, but a request this size " +
                           "may be slow, or more than the model can take in.";
            else if (cost == null)
                costLine = $"The cost is not known: {name} is not in the price list.";
            else if (cost.Value <= 0m)
                costLine = $"{name} is free of charge, so this costs nothing.";
            else
                costLine = "Estimated cost: " +
                           (cost.Value < 0.01m ? "less than $0.01" : $"about ${cost.Value:0.00}") +
                           $" with {name}. This is an estimate: it counts the request and a reply of about " +
                           $"{AssumedReplyTokens:N0} tokens, and a model that reasons before it writes can cost more.";

            return $"This document has {Math.Max(0, segments):N0} segments ({Math.Max(0, words):N0} words). " +
                   "AutoPrompt reads all of it: with its instructions, your terms and the reference material, " +
                   $"the request comes to about {shownTokens:N0} tokens.\n\n" +
                   costLine + "\n\n" +
                   "Generate the prompt anyway?";
        }

        /// <summary>
        /// True when a provider's error says the request was too large for the
        /// model. Deliberately narrow: HTTP 413, or HTTP 400 with one of the
        /// phrases the providers use for the input size. A 400 about the OUTPUT
        /// limit ("max_tokens ... maximum allowed") is not one of them, and a
        /// refusal this does not recognise is shown as it always was.
        /// </summary>
        public static bool IsSizeRefusal(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage)) return false;
            // LlmClient words every refusal "<provider> API error <status>: <body>".
            const string marker = "API error ";
            int at = errorMessage.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0 || at + marker.Length + 3 > errorMessage.Length) return false;
            var status = errorMessage.Substring(at + marker.Length, 3);
            if (status == "413") return true;
            if (status != "400") return false;

            foreach (var phrase in SizePhrases)
                if (errorMessage.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static readonly string[] SizePhrases =
        {
            "prompt is too long",            // Anthropic
            "request_too_large",             // Anthropic
            "context_length_exceeded",       // OpenAI and compatible servers
            "maximum context length",        // OpenAI and compatible servers
            "context window",                // OpenAI, OpenRouter, local servers
            "input token count",             // Gemini
            "input is too long",
            "too many input tokens",
        };
    }
}
