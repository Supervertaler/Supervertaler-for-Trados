using System.Runtime.Serialization;

namespace Supervertaler.Trados.Settings
{
    /// <summary>
    /// A stored GroupShare server credential, used by SuperSearch to reach
    /// server-based (GroupShare) translation memories (issue #35). Studio does
    /// not expose its own credential store to plugin code, so the user enters
    /// the server login once here. The password is DPAPI-encrypted (CurrentUser)
    /// via <see cref="Core.DpapiSecret"/> and is never stored in clear text.
    /// </summary>
    [DataContract]
    public class GroupShareServerCredential
    {
        /// <summary>Server base URL, e.g. <c>https://groupsharedev.sdlproducts.com/</c>. Matched by host.</summary>
        [DataMember(Name = "baseUrl")]
        public string BaseUrl { get; set; } = "";

        /// <summary>
        /// Login provider: <c>"GroupShare"</c> (SDL/GroupShare authentication, the
        /// default) or <c>"Windows"</c> (AD / Windows authentication, e.g. FAU).
        /// Maps to the SDK's <c>TranslationProviderServer useWindowsCredentials</c>
        /// flag. NB: DataContract deserialization of an older settings file leaves
        /// this null, which callers treat as "GroupShare".
        /// </summary>
        [DataMember(Name = "authMode")]
        public string AuthMode { get; set; } = "GroupShare";

        /// <summary>GroupShare username.</summary>
        [DataMember(Name = "username")]
        public string Username { get; set; } = "";

        /// <summary>DPAPI-protected (CurrentUser) password, base64. Empty = none stored.</summary>
        [DataMember(Name = "passwordProtected")]
        public string PasswordProtected { get; set; } = "";
    }
}
