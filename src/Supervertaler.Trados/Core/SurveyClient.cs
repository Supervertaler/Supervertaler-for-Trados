using System;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace Supervertaler.Trados.Core
{
    /// <summary>
    /// One-question in-app survey client (issue #43).
    ///
    /// Fetches the current active question from the stats worker and posts back
    /// an anonymous Yes/No/ignored answer. Mirrors the fetch/POST patterns of
    /// <see cref="UpdateChecker"/> and <see cref="UsageStatistics"/>: every call
    /// fails silently, so a network problem or a server hiccup never disrupts the
    /// user. No personal data is sent — only the same random anonymous UUID used
    /// for usage stats, plus a coarse licence tier (licensed / trial / unlicensed).
    /// </summary>
    public static class SurveyClient
    {
        private static readonly HttpClient _http = new HttpClient();

        // Same worker that receives usage pings (see UsageStatistics.PingUrl).
        private const string BaseUrl = "https://supervertaler-stats.michaelbeijer-co-uk.workers.dev";

        static SurveyClient()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Supervertaler-Trados-Survey/1.0");
            _http.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// The active question for a product ("trados"), or null when there is
        /// none / the request fails.
        /// </summary>
        public static async Task<SurveyQuestion> GetActiveSurveyAsync(string product)
        {
            try
            {
                var url = BaseUrl + "/survey?product=" + Uri.EscapeDataString(product ?? "trados");
                using (var resp = await _http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var dto = Deserialize<SurveyDto>(json);
                    if (dto == null || dto.SurveyId <= 0 || string.IsNullOrEmpty(dto.Question))
                        return null;
                    return new SurveyQuestion
                    {
                        SurveyId = dto.SurveyId,
                        Question = dto.Question,
                        YesLabel = string.IsNullOrEmpty(dto.YesLabel) ? "Yes" : dto.YesLabel,
                        NoLabel = string.IsNullOrEmpty(dto.NoLabel) ? "No" : dto.NoLabel,
                        Kind = (dto.Kind == "open") ? "open" : "yesno",
                    };
                }
            }
            catch
            {
                // No question available is a normal, silent outcome.
                return null;
            }
        }

        /// <summary>
        /// Posts an anonymous answer. Fire-and-forget; never throws.
        /// </summary>
        public static async Task SendResponseAsync(SurveyResponse response)
        {
            try
            {
                if (response == null || response.SurveyId <= 0) return;
                var json = Serialize(response);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _http.PostAsync(BaseUrl + "/survey/response", content).ConfigureAwait(false);
            }
            catch
            {
                // Silent — the client doesn't care about the response.
            }
        }

        private static T Deserialize<T>(string json) where T : class
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                return ser.ReadObject(ms) as T;
            }
        }

        private static string Serialize(SurveyResponse r)
        {
            using (var ms = new MemoryStream())
            {
                var ser = new DataContractJsonSerializer(typeof(SurveyResponse));
                ser.WriteObject(ms, r);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        [DataContract]
        private class SurveyDto
        {
            [DataMember(Name = "survey_id")] public int SurveyId { get; set; }
            [DataMember(Name = "question")] public string Question { get; set; }
            [DataMember(Name = "yes_label")] public string YesLabel { get; set; }
            [DataMember(Name = "no_label")] public string NoLabel { get; set; }
            [DataMember(Name = "kind")] public string Kind { get; set; }
        }
    }

    /// <summary>The active question, as shown in the survey dialog.</summary>
    public sealed class SurveyQuestion
    {
        public int SurveyId { get; set; }
        public string Question { get; set; }
        public string YesLabel { get; set; }
        public string NoLabel { get; set; }
        /// <summary>"yesno" (Yes/No + optional comment) or "open" (free-text answer).</summary>
        public string Kind { get; set; } = "yesno";
    }

    /// <summary>An anonymous answer, serialized to the /survey/response endpoint.</summary>
    [DataContract]
    public sealed class SurveyResponse
    {
        [DataMember(Name = "survey_id")] public int SurveyId { get; set; }
        [DataMember(Name = "id")] public string AnonymousId { get; set; }
        [DataMember(Name = "answer")] public string Answer { get; set; }   // yes | no | ignored
        [DataMember(Name = "text")] public string Text { get; set; }
        [DataMember(Name = "tier")] public string Tier { get; set; }       // licensed | trial | unlicensed
        [DataMember(Name = "product")] public string Product { get; set; }
        [DataMember(Name = "plugin_version")] public string PluginVersion { get; set; }
    }
}
